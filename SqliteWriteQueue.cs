using System.Collections.Concurrent;
using LinqToDB.Data;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the background writer of a SQLite database
/// </summary>
/// <remarks>
/// SQLite allows one writer at a time for the whole database. When concurrent requests write through their own connections,
/// they wait for each other inside SQLite (busy timeout) and the slowest ones fail with "database is locked".
/// Instead, every write of the application is queued and executed by this dedicated background thread, in arrival order, on a
/// single long-lived connection: writers never compete for the lock, and callers simply await their own write.
/// Reads don't go through the queue: in WAL mode they run in parallel and are never blocked by the writer
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
        public abstract void Execute(DataConnection connection);

        public abstract void Fail(Exception exception);
    }

    private sealed class WorkItem<TResult> : WorkItem
    {
        private readonly Func<DataConnection, TResult> _work;

        public WorkItem(Func<DataConnection, TResult> work)
        {
            _work = work;
        }

        //continuations must not run on the writer thread
        public TaskCompletionSource<TResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Execute(DataConnection connection)
        {
            Completion.SetResult(_work(connection));
        }

        public override void Fail(Exception exception)
        {
            Completion.TrySetException(exception);
        }
    }

    #endregion

    #region Utilities

    private void Run()
    {
        DataConnection connection = null;

        try
        {
            foreach (var workItem in _workItems.GetConsumingEnumerable())
            {
                try
                {
                    connection ??= _connectionFactory();
                    _writerConnection = connection;
                    workItem.Execute(connection);
                }
                catch (Exception exception)
                {
                    workItem.Fail(exception);

                    //start the next write on a clean connection (no pending transaction, no broken state)
                    connection?.Dispose();
                    connection = null;
                }
                finally
                {
                    _writerConnection = null;
                }
            }
        }
        finally
        {
            connection?.Dispose();
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
    /// <typeparam name="TResult">Result type</typeparam>
    /// <returns>A task that represents the asynchronous operation; the task result contains the result of the write</returns>
    public Task<TResult> EnqueueAsync<TResult>(Func<DataConnection, TResult> work)
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

        var workItem = new WorkItem<TResult>(work);
        _workItems.Add(workItem);

        return workItem.Completion.Task;
    }

    /// <summary>
    /// Queues a write and waits for it to be executed
    /// </summary>
    /// <param name="work">Write to execute with the writer connection</param>
    /// <typeparam name="TResult">Result type</typeparam>
    /// <returns>Result of the write</returns>
    public TResult Execute<TResult>(Func<DataConnection, TResult> work)
    {
        return EnqueueAsync(work).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Queues a write and returns once it's executed
    /// </summary>
    /// <param name="work">Write to execute with the writer connection</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public Task EnqueueAsync(Action<DataConnection> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return EnqueueAsync(connection =>
        {
            work(connection);
            return true;
        });
    }

    /// <summary>
    /// Queues a write and waits for it to be executed
    /// </summary>
    /// <param name="work">Write to execute with the writer connection</param>
    public void Execute(Action<DataConnection> work)
    {
        EnqueueAsync(work).GetAwaiter().GetResult();
    }

    #endregion
}
