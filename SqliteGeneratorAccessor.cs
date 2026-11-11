using FluentMigrator;
using FluentMigrator.Runner.Generators;
using Nop.Data;
using Nop.Data.Migrations;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the migration generator accessor selecting the SQLite generator for a store running on SQLite
/// </summary>
/// <remarks>
/// Replaces NopGeneratorAccessor, which only knows the generators of the built-in data providers
/// </remarks>
public class SqliteGeneratorAccessor : NopGeneratorAccessor
{
    #region Ctor

    public SqliteGeneratorAccessor(IEnumerable<IMigrationGenerator> generators) : base(generators)
    {
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Configure the generator for the configured data provider
    /// </summary>
    /// <param name="generators">Collection of migration generators</param>
    protected override void ConfigureGenerator(IList<IMigrationGenerator> generators)
    {
        if (SqliteDataSettings.IsSqlite(DataSettingsManager.LoadSettings()))
            Generator = FindGenerator(generators, GeneratorIdConstants.SQLite);
        else
            base.ConfigureGenerator(generators);
    }

    #endregion
}
