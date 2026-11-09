using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Nop.Core;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents a helper for SQLite connection strings
/// </summary>
public static class SqliteConnectionStringHelper
{
    #region Fields

    private const string DATA_DIRECTORY_MACRO = "|DataDirectory|";

    private static readonly ConcurrentDictionary<string, string> _normalizedConnectionStrings = new(StringComparer.Ordinal);

    #endregion

    #region Methods

    /// <summary>
    /// Makes a connection string independent of the current directory and applies the defaults nopCommerce relies on
    /// </summary>
    /// <param name="connectionString">Connection string as configured (e.g. "Data Source=App_Data/nopcommerce.sqlite")</param>
    /// <returns>Normalized connection string</returns>
    public static string Normalize(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            return connectionString;

        return _normalizedConnectionStrings.GetOrAdd(connectionString, static value =>
        {
            var builder = new SqliteConnectionStringBuilder(value);
            builder.DataSource = ResolveDataSource(builder.DataSource, builder.Mode);

            //nopCommerce relies on the cascade deletes declared by its foreign keys, which SQLite enforces only on request
            builder.ForeignKeys ??= true;

            return builder.ConnectionString;
        });
    }

    /// <summary>
    /// Gets a value indicating whether a connection string points to an in-memory database
    /// </summary>
    /// <param name="builder">Connection string builder</param>
    /// <returns>True if the database lives in memory; otherwise false</returns>
    public static bool IsInMemory(SqliteConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Mode == SqliteOpenMode.Memory
            || string.IsNullOrWhiteSpace(builder.DataSource)
            || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the database file path: |DataDirectory| points to App_Data, "~/" and relative paths to the application root
    /// </summary>
    /// <param name="dataSource">Data source as configured</param>
    /// <param name="mode">Open mode</param>
    /// <returns>Absolute path of the database file (or the data source as is for in-memory databases and URIs)</returns>
    public static string ResolveDataSource(string dataSource, SqliteOpenMode mode)
    {
        if (mode == SqliteOpenMode.Memory || string.IsNullOrWhiteSpace(dataSource)
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return dataSource;

        //relative paths are resolved by SQLite against the current directory, which isn't always the application root
        var fileProvider = CommonHelper.DefaultFileProvider;
        var path = dataSource.Trim();

        if (path.Contains(DATA_DIRECTORY_MACRO, StringComparison.OrdinalIgnoreCase))
            path = path.Replace(DATA_DIRECTORY_MACRO, fileProvider.MapPath($"~/{SqliteDataProviderDefaults.DatabaseDirectory}/"), StringComparison.OrdinalIgnoreCase);
        else if (!Path.IsPathRooted(path) || path.StartsWith("~"))
            path = fileProvider.MapPath($"~/{path.TrimStart('~', '/', '\\')}");

        return Path.GetFullPath(path);
    }

    #endregion
}
