using System.Data.Common;
using FluentMigrator.Runner.Processors.SQLite;
using Microsoft.Data.Sqlite;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents an ADO.NET provider factory creating <see cref="NopSqliteConnection"/> connections
/// </summary>
public sealed class NopSqliteProviderFactory : DbProviderFactory
{
    #region Ctor

    private NopSqliteProviderFactory()
    {
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    public override DbCommand CreateCommand() => new SqliteCommand();

    /// <inheritdoc />
    public override DbConnection CreateConnection() => new NopSqliteConnection();

    /// <inheritdoc />
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new SqliteConnectionStringBuilder();

    /// <inheritdoc />
    public override DbParameter CreateParameter() => new SqliteParameter();

    #endregion

    #region Properties

    /// <summary>
    /// Gets the factory instance
    /// </summary>
    public static NopSqliteProviderFactory Instance { get; } = new();

    #endregion
}

/// <summary>
/// Represents the FluentMigrator database factory: migrations use the same connections (and so the same database file) as the store
/// </summary>
/// <remarks>
/// The stock factory locates Microsoft.Data.Sqlite by reflection among the host's dependencies, where a plugin dependency isn't
/// found, and it would resolve a relative "Data Source" against the current directory instead of the application root
/// </remarks>
public class NopSqliteDbFactory : SQLiteDbFactory
{
    #region Ctor

    public NopSqliteDbFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    #endregion

    #region Utilities

    /// <inheritdoc />
    protected override DbProviderFactory CreateFactory() => NopSqliteProviderFactory.Instance;

    #endregion
}
