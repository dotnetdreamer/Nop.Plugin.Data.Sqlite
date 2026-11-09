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
    /// temp_store = MEMORY keeps temporary tables and indexes in memory
    /// </remarks>
    public static string ConnectionPragmas => "PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;";

    /// <summary>
    /// Gets the maximum number of identifiers per "DELETE ... WHERE Id IN (...)" statement
    /// </summary>
    public static int DeleteBatchSize => 500;

    /// <summary>
    /// Gets the hash algorithm used for field hashes (the same as SQL Server's HASHBYTES('SHA2_512'))
    /// </summary>
    public static string HashAlgorithm => "SHA512";
}
