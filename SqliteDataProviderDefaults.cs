namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents default values of the SQLite data provider
/// </summary>
public static class SqliteDataProviderDefaults
{
    /// <summary>
    /// Gets the value of "DataProvider" in App_Data/appsettings.json for a store running on SQLite
    /// </summary>
    /// <remarks>
    /// nopCommerce's DataProviderType enum only names its built-in providers (1-3), so SQLite uses a number outside of them,
    /// far enough not to collide with providers nopCommerce may add
    /// </remarks>
    public static int DataProviderTypeId => 100;

    /// <summary>
    /// Gets the name of the data provider shown in the installation wizard and in the admin area
    /// </summary>
    public static string DataProviderName => "SQLite";

    /// <summary>
    /// Gets the identifier of the FluentMigrator SQLite processor
    /// </summary>
    public static string MigrationProcessorId => "SQLite";

    /// <summary>
    /// Gets the directory (relative to the application root) of the database files created by the installation wizard
    /// </summary>
    public static string DatabaseDirectory => "App_Data";

    /// <summary>
    /// Gets the extension of the database files created by the installation wizard
    /// </summary>
    public static string DatabaseFileExtension => ".sqlite";

    /// <summary>
    /// Gets the name of the database file when none is specified
    /// </summary>
    public static string DefaultDatabaseName => "nopcommerce";

    /// <summary>
    /// Gets the collation applied to text columns, so string comparisons are case-insensitive like on SQL Server (ASCII letters only)
    /// </summary>
    /// <remarks>
    /// It's a built-in collation, so the database file stays readable by any SQLite tool
    /// </remarks>
    public static string CaseInsensitiveCollation => "NOCASE";

    /// <summary>
    /// Gets the statements executed once for every new physical connection
    /// </summary>
    /// <remarks>
    /// synchronous = NORMAL is corruption-safe in WAL mode and much faster than FULL (after a power loss the last commits may be rolled back);
    /// temp_store = MEMORY keeps temporary tables and indexes in memory;
    /// journal_size_limit shrinks the write-ahead log file back to that size whenever SQLite starts reusing it after a checkpoint
    /// </remarks>
    public static string ConnectionPragmas => $"PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA journal_size_limit = {WalSizeLimit};";

    /// <summary>
    /// Gets the size (in bytes) the write-ahead log file is shrunk to once its content is checkpointed
    /// </summary>
    /// <remarks>
    /// The size of the default automatic checkpoint (1000 pages of 4 KB). It isn't a cap: the log grows past it while a reader keeps
    /// an old snapshot, or while writes come faster than they're checkpointed
    /// </remarks>
    public static long WalSizeLimit => 4 * 1024 * 1024;

    /// <summary>
    /// Gets how long the writer waits for a new write before it checkpoints and empties the write-ahead log
    /// </summary>
    public static TimeSpan IdleCheckpointDelay => TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the maximum number of waiting writes the writer commits together in one transaction
    /// </summary>
    public static int WriteBatchSize => 64;

    /// <summary>
    /// Gets the time after which the writer commits a batch of writes, even if more writes are waiting
    /// </summary>
    /// <remarks>
    /// The writes of a batch are reported as done only once the batch is committed, so it bounds how long the first one waits
    /// </remarks>
    public static TimeSpan WriteBatchDuration => TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Gets the name of the savepoint isolating each write of a batch
    /// </summary>
    public static string WriteSavepointName => "nop_write";

    /// <summary>
    /// Gets the maximum number of identifiers per "DELETE ... WHERE Id IN (...)" statement
    /// </summary>
    public static int DeleteBatchSize => 500;

    /// <summary>
    /// Gets the hash algorithm used for field hashes (the same as SQL Server's HASHBYTES('SHA2_512'))
    /// </summary>
    public static string HashAlgorithm => "SHA512";
}
