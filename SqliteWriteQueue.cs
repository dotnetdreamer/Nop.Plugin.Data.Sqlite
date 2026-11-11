using System.Collections.Concurrent;
using System.Diagnostics;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the background writer of a SQLite database
/// </summary>
/// <remarks>
/// SQLite allows one writer at a time for the whole database. When concurrent requests write through their own connections,
/// they wait for each other inside SQLite (busy timeout) and the slowest ones fail with "database is locked".
/// Instead, every write of the application is queued and executed by this dedicated background thread, in arrival order, on a
/// single long-lived connection: writers never compete for the lock, and callers simply await their own write.
/// Reads don't go through the queue: in WAL mode they run in parallel and are never blocked by the writer.
/// <list type="bullet">
/// <item>Group commit: when several single-statement writes are waiting, they run in one transaction committed once, each write
/// inside its own savepoint, instead of one transaction per write. A write which fails is rolled back to its savepoint, the
/// writes before it are committed, and it runs again on its own: every caller gets the outcome it would get without batching.
/// A write is reported as done only once its transaction is committed.</item>
/// <item>Idle checkpoint: once no write came for a moment, the write-ahead log is checkpointed and emptied, so it doesn't keep
/// its largest size.</item>
/// </list>
/// </remarks>
public sealed class SqliteWriteQueue
{
    #region Fields

    private static readonly ConcurrentDictionary<string, SqliteWriteQueue> _queues = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static DataConnection _writerConnection;

    private readonly Func<DataConnection> _connectionFactory;
    private readonly BlockingCollection<WorkItem> _workItems = new();
    private readonly Thread _thread;
    private DataConnection _connection;

    #endregion

    #region Ctor

    private SqliteWriteQueue(string name, Func<DataConnection> connectionFactory)
    {
        _connectionFactory = connectionFactory;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"SQLite writer ({name})"
        };
        _thread.Start();

        //let the pending writes finish when the application stops
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    #endregion

    #region Nested classes

    private abstract class WorkItem
    {
        protected WorkItem(bool batchable)
        {
            Batchable = batchable;
        }

        /// <summary>
        /// Gets a value indicating whether the write may share a transaction with other writes (it doesn't manage transactions itself)
        /// </summary>
        public bool Batchable { get; }

        /// <summary>
        /// Executes the write; its result is kept until <see cref="Complete"/>
        /// </summary>
        /// <param name="connection">Writer connection</param>
        public abstract void Run(DataConnection connection);

        /// <summary>
        /// Hands the result over to the caller
        /// </summary>
        public abstract void Complete();

        /// <summary>
        /// Hands the failure over to the caller
        /// </summary>
        /// <param name="exception">Exception</param>
        public abstract void Fail(Exception exception);
    }

    private sealed class WorkItem<TResult> : WorkItem
    {
        private readonly Func<DataConnection, TResult> _work;
        private TResult _result;

        public WorkItem(Func<DataConnection, TResult> work, bool batchable) : base(batchable)
        {
            _work = work;
        }

        //continuations must not run on the writer thread
        public TaskCompletionSource<TResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Run(DataConnection connection)
        {
            _result = _work(connection);
        }

        public override void Complete()
        {
            Completion.TrySetResult(_result);
        }

        public override void Fail(Exception exception)
        {
            Completion.TrySetException(exception);
        }
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Gets a value indicating whether a transaction is open on a connection
    /// </summary>
    /// <param name="connection">Connection</param>
    /// <returns>True if a transaction is open; otherwise false</returns>
    private static bool IsInTransaction(DataConnection connection)
    {
        return connection.Connection is SqliteConnection { Handle: { } handle } && raw.sqlite3_get_autocommit(handle) == 0;
    }

    /// <summary>
    /// Executes a write on the writer connection
    /// </summary>
    /// <param name="workItem">Write</param>
    /// <param name="connection">Writer connection</param>
    private static void RunWork(WorkItem workItem, DataConnection connection)
    {
        _writerConnection = connection;
        try
        {
            workItem.Run(connection);
        }
        finally
        {
            _writerConnection = null;
        }
    }

    /// <summary>
    /// Starts the next write on a clean connection (no pending transaction, no broken state)
    /// </summary>
    private void ResetConnection()
    {
        var connection = _connection;
        _connection = null;

        try
        {
            connection?.Dispose();
        }
        catch (Exception)
        {
            //the connection is dropped anyway
        }
    }

    /// <summary>
    /// Takes the next write from the queue
    /// </summary>
    /// <param name="checkpointPending">Whether a checkpoint is due once the queue stays empty for a moment</param>
    /// <returns>The next write; null if none came meanwhile or the queue is completed</returns>
    private WorkItem Take(bool checkpointPending)
    {
        try
        {
            if (!checkpointPending)
                return _workItems.Take();

            return _workItems.TryTake(out var workItem, SqliteDataProviderDefaults.IdleCheckpointDelay) ? workItem : null;
        }
        catch (InvalidOperationException)
        {
            //the queue is completed: the application stops
            return null;
        }
    }

    /// <summary>
    /// Executes a write on its own, in its own transaction
    /// </summary>
    /// <param name="workItem">Write</param>
    private void ExecuteAlone(WorkItem workItem)
    {
        try
        {
            RunWork(workItem, Connection);
            workItem.Complete();
        }
        catch (Exception exception)
        {
            workItem.Fail(exception);
            ResetConnection();
        }
    }

    /// <summary>
    /// Rolls back the transaction of the writer connection, if any
    /// </summary>
    private void Rollback()
    {
        try
        {
            if (_connection is not null && IsInTransaction(_connection))
                _connection.Execute("ROLLBACK;");
        }
        catch (Exception)
        {
            ResetConnection();
        }
    }

    /// <summary>
    /// Commits a batch and reports its writes as done
    /// </summary>
    /// <param name="batch">Writes executed in the transaction</param>
    private void Commit(List<WorkItem> batch)
    {
        try
        {
            Connection.Execute("COMMIT;");
        }
        catch (Exception)
        {
            //nothing of the batch is saved: execute its writes one by one, as without batching
            Rollback();
            foreach (var workItem in batch)
                ExecuteAlone(workItem);

            batch.Clear();

            return;
        }

        foreach (var workItem in batch)
            workItem.Complete();

        batch.Clear();
    }

    /// <summary>
    /// Executes waiting writes in one transaction, each one inside its own savepoint
    /// </summary>
    /// <param name="first">First write</param>
    /// <param name="second">Second write, already taken from the queue</param>
    /// <returns>A write taken from the queue which couldn't join the batch, to execute next; otherwise null</returns>
    private WorkItem ExecuteBatch(WorkItem first, WorkItem second)
    {
        var savepoint = SqliteDataProviderDefaults.WriteSavepointName;
        var started = Stopwatch.GetTimestamp();
        var batch = new List<WorkItem>();
        DataConnection connection;

        try
        {
            connection = Connection;
            connection.Execute("BEGIN IMMEDIATE;");
        }
        catch (Exception)
        {
            ResetConnection();
            ExecuteAlone(first);

            return second;
        }

        var workItem = first;
        var waiting = second;
        WorkItem next = null;

        while (workItem is not null)
        {
            try
            {
                connection.Execute($"SAVEPOINT {savepoint};");
                RunWork(workItem, connection);
                connection.Execute($"RELEASE {savepoint};");
                batch.Add(workItem);
            }
            catch (Exception)
            {
                var onlyThisWriteUndone = false;
                if (IsInTransaction(connection))
                {
                    try
                    {
                        connection.Execute($"ROLLBACK TO {savepoint};");
                        connection.Execute($"RELEASE {savepoint};");
                        onlyThisWriteUndone = true;
                    }
                    catch (Exception)
                    {
                        //the savepoint is gone: handled as a lost transaction
                    }
                }

                if (onlyThisWriteUndone)
                {
                    //commit the writes before it, then execute it again on its own: it sees what it would see without batching,
                    //and its caller gets the outcome of that execution
                    Commit(batch);
                    ExecuteAlone(workItem);
                }
                else
                {
                    //SQLite rolled the whole transaction back (e.g. the disk is full): nothing of the batch is saved
                    Rollback();
                    foreach (var done in batch)
                        ExecuteAlone(done);

                    ExecuteAlone(workItem);
                }

                return waiting;
            }

            if (waiting is not null)
            {
                workItem = waiting;
                waiting = null;
                continue;
            }

            workItem = null;
            if (batch.Count < SqliteDataProviderDefaults.WriteBatchSize
                && Stopwatch.GetElapsedTime(started) < SqliteDataProviderDefaults.WriteBatchDuration
                && _workItems.TryTake(out var candidate))
            {
                if (candidate.Batchable)
                    workItem = candidate;
                else
                    next = candidate;
            }
        }

        Commit(batch);

        return next;
    }

    /// <summary>
    /// Executes a write, together with the writes waiting behind it when they can share a transaction
    /// </summary>
    /// <param name="workItem">Write</param>
    /// <returns>A write taken from the queue which couldn't join, to execute next; otherwise null</returns>
    private WorkItem Execute(WorkItem workItem)
    {
        if (!workItem.Batchable || !_workItems.TryTake(out var nextItem))
        {
            ExecuteAlone(workItem);
            return null;
        }

        if (!nextItem.Batchable)
        {
            ExecuteAlone(workItem);
            return nextItem;
        }

        return ExecuteBatch(workItem, nextItem);
    }

    /// <summary>
    /// Checkpoints the write-ahead log and empties it, without waiting for readers
    /// </summary>
    /// <returns>True if the log is emptied or there's nothing to do; false if readers still use it, then it's tried again later</returns>
    private bool TryCheckpoint()
    {
        try
        {
            var connection = Connection;
            var busyTimeout = connection.Execute<long>("PRAGMA busy_timeout;");

            //waiting for readers would hold back the writes queued meanwhile
            connection.Execute("PRAGMA busy_timeout = 0;");
            try
            {
                //the result row is (busy, log frames, checkpointed frames): busy is 1 while a reader still uses the log
                return connection.Execute<int>("PRAGMA wal_checkpoint(TRUNCATE);") == 0;
            }
            finally
            {
                connection.Execute($"PRAGMA busy_timeout = {busyTimeout};");
            }
        }
        catch (Exception)
        {
            ResetConnection();
            return true;
        }
    }

    private void Run()
    {
        WorkItem next = null;
        var checkpointPending = false;

        try
        {
            while (true)
            {
                var workItem = next ?? Take(checkpointPending);
                next = null;

                if (workItem is null)
                {
                    if (_workItems.IsCompleted)
                        break;

                    //no write came for a moment
                    checkpointPending = !TryCheckpoint();
                    continue;
                }

                next = Execute(workItem);
                checkpointPending = true;
            }
        }
        finally
        {
            _connection?.Dispose();
        }
    }

    private void Stop()
    {
        _workItems.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(10));
    }

    #endregion

    #region Methods

    /// <summary>
    /// Gets the writer of a database
    /// </summary>
    /// <param name="connectionString">Connection string of the database</param>
    /// <param name="connectionFactory">Factory of the connection used by the writer</param>
    /// <returns>Writer</returns>
    public static SqliteWriteQueue Get(string connectionString, Func<DataConnection> connectionFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        return _queues.GetOrAdd(connectionString, key =>
            new SqliteWriteQueue(Path.GetFileName(SqliteConnectionStringHelper.Normalize(key)), connectionFactory));
    }

    /// <summary>
    /// Queues a write and returns its result once it's executed
    /// </summary>
    /// <param name="work">Write to execute with the writer connection</param>
    /// <param name="batchable">
    /// Whether the write may run in a transaction shared with other writes: true only for writes which don't begin,
    /// commit or roll back transactions themselves and don't run statements forbidden in a transaction (e.g. VACUUM)
    /// </param>
    /// <typeparam name="TResult">Result type</typeparam>
    /// <returns>A task that represents the asynchronous operation; the task result contains the result of the write</returns>
    public Task<TResult> EnqueueAsync<TResult>(Func<DataConnection, TResult> work, bool batchable = false)
    {
        ArgumentNullException.ThrowIfNull(work);

        //a write requested by the write in progress (on the writer thread) is executed at once, waiting for it would never end
        if (_writerConnection is { } writerConnection)
        {
            try
            {
                return Task.FromResult(work(writerConnection));
            }
            catch (Exception exception)
            {
                return Task.FromException<TResult>(exception);
            }
        }

        var workItem = new WorkItem<TResult>(work, batchable);
        _workItems.Add(workItem);

        return workItem.Completion.Task;
    }

    /// <summary>
    /// Queues a write and waits for it to be executed
    /// </summary>
    /// <param name="work">Write to execute with the writer connection</param>
    /// <param name="batchable">Whether the write may run in a transaction shared with other writes (see <see cref="EnqueueAsync{TResult}"/>)</param>
    /// <typeparam name="TResult">Result type</typeparam>
    /// <returns>Result of the write</returns>
    public TResult Execute<TResult>(Func<DataConnection, TResult> work, bool batchable = false)
    {
        return EnqueueAsync(work, batchable).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Queues a write and returns once it's executed
    /// </summary>
    /// <param name="work">Write to execute with the writer connection</param>
    /// <param name="batchable">Whether the write may run in a transaction shared with other writes (see <see cref="EnqueueAsync{TResult}"/>)</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public Task EnqueueAsync(Action<DataConnection> work, bool batchable = false)
    {
        ArgumentNullException.ThrowIfNull(work);

        return EnqueueAsync(connection =>
        {
            work(connection);
            return true;
        }, batchable);
    }

    /// <summary>
    /// Queues a write and waits for it to be executed
    /// </summary>
    /// <param name="work">Write to execute with the writer connection</param>
    /// <param name="batchable">Whether the write may run in a transaction shared with other writes (see <see cref="EnqueueAsync{TResult}"/>)</param>
    public void Execute(Action<DataConnection> work, bool batchable = false)
    {
        EnqueueAsync(work, batchable).GetAwaiter().GetResult();
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the writer connection
    /// </summary>
    private DataConnection Connection => _connection ??= _connectionFactory();

    #endregion
}
