using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents a connection to a SQLite database configured the way nopCommerce needs it
/// </summary>
/// <remarks>
/// - the database file path is resolved against the application root (see <see cref="SqliteConnectionStringHelper"/>);
/// - foreign keys are enforced, so cascade deletes work as on the other databases;
/// - the database runs in WAL mode: readers never block the writer and the writer never blocks readers;
/// - a connection waits for a lock held by another one (busy timeout) instead of failing with "database is locked"
/// It's used by both linq2db and FluentMigrator, so all database access shares the same behavior
/// </remarks>
public class NopSqliteConnection : SqliteConnection
{
    #region Fields

    private static readonly ConditionalWeakTable<sqlite3, object> _initializedHandles = new();
    private static readonly object _initializedMarker = new();
    private static readonly ConcurrentDictionary<string, bool> _walDatabases = new(StringComparer.OrdinalIgnoreCase);

    #endregion

    #region Ctor

    static NopSqliteConnection()
    {
        //must happen before Microsoft.Data.Sqlite loads the native library
        SqliteNativeLibraryResolver.Register();
    }

    public NopSqliteConnection()
    {
    }

    public NopSqliteConnection(string connectionString) : base(connectionString)
    {
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Prepares a newly opened physical connection; pooled connections keep their native handle, so it runs once per handle
    /// </summary>
    protected virtual void Initialize()
    {
        var handle = Handle;
        if (handle is null || _initializedHandles.TryGetValue(handle, out _))
            return;

        using (var command = CreateCommand())
        {
            //SQLite allows a single writer at a time: wait for the lock (up to the command timeout) instead of failing immediately
            var busyTimeout = (DefaultTimeout > 0 ? DefaultTimeout : 30) * 1000L;
            command.CommandText = $"PRAGMA busy_timeout = {busyTimeout}; {SqliteDataProviderDefaults.ConnectionPragmas}";
            command.ExecuteNonQuery();
        }

        EnsureWriteAheadLog();

        _initializedHandles.TryAdd(handle, _initializedMarker);
    }

    /// <summary>
    /// Switches the database to the write-ahead log journal mode (it's persistent, so it's done once per database file)
    /// </summary>
    protected virtual void EnsureWriteAheadLog()
    {
        var databasePath = DataSource;
        if (string.IsNullOrEmpty(databasePath) || _walDatabases.ContainsKey(databasePath))
            return;

        try
        {
            using var command = CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            var journalMode = command.ExecuteScalar() as string;

            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(journalMode, "memory", StringComparison.OrdinalIgnoreCase))
            {
                command.CommandText = "PRAGMA journal_mode = WAL;";
                command.ExecuteScalar();
            }

            _walDatabases.TryAdd(databasePath, true);
        }
        catch (SqliteException)
        {
            //e.g. a read-only database or a file locked by another process: keep the current journal mode, try again next time
        }
    }

    #endregion

    #region Methods

    /// <summary>
    /// Opens a connection to the database
    /// </summary>
    public override void Open()
    {
        base.Open();
        Initialize();
    }

    /// <summary>
    /// Opens a connection to the database
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        await base.OpenAsync(cancellationToken);
        Initialize();
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets or sets the connection string (it's normalized, see <see cref="SqliteConnectionStringHelper.Normalize"/>)
    /// </summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => base.ConnectionString;
        set => base.ConnectionString = SqliteConnectionStringHelper.Normalize(value);
    }

    #endregion
}
