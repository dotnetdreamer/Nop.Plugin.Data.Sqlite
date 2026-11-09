using FluentMigrator.Runner.Generators.SQLite;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Processors;
using FluentMigrator.Runner.Processors.SQLite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the SQLite migration processor
/// </summary>
/// <remarks>
/// SQLite has a single, database-wide write lock. FluentMigrator runs every migration in a transaction (which takes that lock),
/// while nopCommerce data migrations write through their services, i.e. through other connections: those writes would wait
/// for the lock held by the migration on the same thread and fail with "database is locked" (SQL Server doesn't notice since
/// it locks rows). So migration statements are executed in autocommit mode, as on MySQL where DDL can't be transactional either
/// </remarks>
public class NopSqliteMigrationProcessor : SQLiteProcessor
{
    #region Ctor

    public NopSqliteMigrationProcessor(SQLiteDbFactory factory,
        SQLiteGenerator generator,
        ILogger<SQLiteProcessor> logger,
        IOptionsSnapshot<ProcessorOptions> options,
        IConnectionStringAccessor connectionStringAccessor,
        IServiceProvider serviceProvider,
        SQLiteQuoter quoter)
        : base(factory, generator, logger, options, connectionStringAccessor, serviceProvider, quoter)
    {
    }

    #endregion

    #region Methods

    /// <summary>
    /// Begins a transaction (statements are committed one by one, see remarks of the class)
    /// </summary>
    public override void BeginTransaction()
    {
    }

    /// <summary>
    /// Commits the transaction (statements are committed one by one, see remarks of the class)
    /// </summary>
    public override void CommitTransaction()
    {
    }

    /// <summary>
    /// Rolls back the transaction (statements are committed one by one, see remarks of the class)
    /// </summary>
    public override void RollbackTransaction()
    {
    }

    #endregion
}
