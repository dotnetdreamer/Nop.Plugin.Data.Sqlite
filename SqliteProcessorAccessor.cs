using FluentMigrator;
using Nop.Data;
using Nop.Data.Migrations;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the migration processor accessor selecting the SQLite processor for a store running on SQLite
/// </summary>
/// <remarks>
/// Replaces NopProcessorAccessor, which only knows the processors of the built-in data providers
/// </remarks>
public class SqliteProcessorAccessor : NopProcessorAccessor
{
    #region Ctor

    public SqliteProcessorAccessor(IEnumerable<IMigrationProcessor> processors) : base(processors)
    {
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Configure the processor for the configured data provider
    /// </summary>
    /// <param name="processors">Collection of migration processors</param>
    protected override void ConfigureProcessor(IList<IMigrationProcessor> processors)
    {
        if (SqliteDataSettings.IsSqlite(DataSettingsManager.LoadSettings()))
            Processor = FindProcessor(processors, SqliteDataProviderDefaults.MigrationProcessorId);
        else
            base.ConfigureProcessor(processors);
    }

    #endregion
}
