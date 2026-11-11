using System.Data;
using FluentMigrator.Exceptions;
using FluentMigrator.Expressions;
using FluentMigrator.Model;
using FluentMigrator.Runner.Generators;
using FluentMigrator.Runner.Generators.SQLite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the SQLite migration (DDL) generator adapted to the nopCommerce schema
/// </summary>
/// <remarks>
/// - text columns are created with the NOCASE collation: nopCommerce expects case-insensitive comparisons (emails, slugs, names...),
///   which SQL Server provides by its default collation and PostgreSQL by the citext type;
/// - foreign keys declared together with their column are emitted inside CREATE TABLE (that's the only way SQLite supports them);
///   a foreign key added to an existing table (e.g. the circular Order - RewardPointsHistory key) can't be created
///   without rebuilding the table, so it's skipped instead of failing the migration
/// </remarks>
public class NopSqliteMigrationGenerator : SQLiteGenerator
{
    #region Fields

    protected readonly ILogger _logger;

    #endregion

    #region Ctor

    public NopSqliteMigrationGenerator(SQLiteQuoter quoter,
        ISQLiteTypeMap typeMap,
        IOptions<GeneratorOptions> generatorOptions,
        ILogger<NopSqliteMigrationGenerator> logger = null) : base(quoter, typeMap, generatorOptions)
    {
        _logger = logger;
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Applies the case-insensitive collation to text columns which don't specify one
    /// </summary>
    /// <param name="columns">Column definitions</param>
    protected virtual void ApplyCaseInsensitiveCollation(IEnumerable<ColumnDefinition> columns)
    {
        foreach (var column in columns)
        {
            if (!string.IsNullOrEmpty(column.CollationName))
                continue;

            if (column.Type is DbType.String or DbType.StringFixedLength or DbType.AnsiString or DbType.AnsiStringFixedLength)
                column.CollationName = SqliteDataProviderDefaults.CaseInsensitiveCollation;
        }
    }

    /// <summary>
    /// Generates the SQL of an operation SQLite may not support on an existing table
    /// </summary>
    /// <param name="generate">Generation function</param>
    /// <param name="description">Operation description</param>
    /// <returns>SQL statement; empty if the operation is skipped</returns>
    protected virtual string GenerateIfSupported(Func<string> generate, string description)
    {
        try
        {
            return generate();
        }
        catch (DatabaseOperationNotSupportedException)
        {
            _logger?.LogWarning("SQLite: {Operation} skipped, SQLite can only declare foreign keys when a table is created", description);

            return string.Empty;
        }
    }

    #endregion

    #region Methods

    /// <summary>
    /// Generates the definition of a column, as CREATE TABLE declares it
    /// </summary>
    /// <param name="column">Column definition</param>
    /// <returns>Column definition SQL, e.g. "TypeId" INTEGER NOT NULL</returns>
    public virtual string GenerateColumnDefinition(ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);

        ApplyCaseInsensitiveCollation([column]);

        return Column.Generate(column);
    }

    /// <inheritdoc />
    public override string Generate(CreateTableExpression expression)
    {
        ApplyCaseInsensitiveCollation(expression.Columns);

        return base.Generate(expression);
    }

    /// <inheritdoc />
    public override string Generate(CreateColumnExpression expression)
    {
        ApplyCaseInsensitiveCollation([expression.Column]);

        return base.Generate(expression);
    }

    /// <inheritdoc />
    public override string Generate(CreateForeignKeyExpression expression)
    {
        return GenerateIfSupported(() => base.Generate(expression),
            $"Foreign key {expression.ForeignKey.Name} ({expression.ForeignKey.ForeignTable} -> {expression.ForeignKey.PrimaryTable})");
    }

    /// <inheritdoc />
    public override string Generate(DeleteForeignKeyExpression expression)
    {
        return GenerateIfSupported(() => base.Generate(expression),
            $"Deletion of foreign key {expression.ForeignKey.Name} ({expression.ForeignKey.ForeignTable})");
    }

    #endregion
}
