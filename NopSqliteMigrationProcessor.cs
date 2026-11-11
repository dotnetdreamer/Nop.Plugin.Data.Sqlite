using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using FluentMigrator.Expressions;
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
/// - SQLite has a single, database-wide write lock. FluentMigrator runs every migration in a transaction (which takes that lock),
///   while nopCommerce data migrations write through their services, i.e. through other connections: those writes would wait
///   for the lock held by the migration on the same thread and fail with "database is locked" (SQL Server doesn't notice since
///   it locks rows). So migration statements are executed in autocommit mode, as on MySQL where DDL can't be transactional either;
/// - SQLite has no ALTER COLUMN. A column that already has the requested definition is left as it is (nopCommerce migrations
///   alter columns that a new database already created that way); any other change rebuilds the table, see <see cref="RebuildTable"/>
/// </remarks>
public partial class NopSqliteMigrationProcessor : SQLiteProcessor
{
    #region Fields

    protected readonly SQLiteQuoter _quoter;

    #endregion

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
        _quoter = quoter;
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Executes a statement on the migration connection
    /// </summary>
    /// <param name="sql">SQL statement</param>
    /// <param name="parameters">Parameters (name, value)</param>
    protected virtual void ExecuteStatement(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(sql, parameters);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Executes a query on the migration connection and reads its rows
    /// </summary>
    /// <param name="sql">SQL query</param>
    /// <param name="parameters">Parameters (name, value)</param>
    /// <returns>Rows, each as an array of its values</returns>
    protected virtual IList<object[]> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(sql, parameters);
        using var reader = command.ExecuteReader();

        var rows = new List<object[]>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }

        return rows;
    }

    /// <summary>
    /// Creates a command on the migration connection
    /// </summary>
    /// <param name="sql">SQL statement</param>
    /// <param name="parameters">Parameters (name, value)</param>
    /// <returns>Command</returns>
    protected virtual IDbCommand CreateCommand(string sql, params (string Name, object Value)[] parameters)
    {
        EnsureConnectionIsOpen();

        var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = Transaction;
        command.CommandTimeout = Options.Timeout is { } timeout ? (int)timeout.TotalSeconds : command.CommandTimeout;

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <summary>
    /// Splits a CREATE TABLE statement into its column and constraint definitions
    /// </summary>
    /// <param name="sql">CREATE TABLE statement, as stored in sqlite_schema</param>
    /// <returns>Definitions, in their order, and the text after the closing parenthesis (table options such as WITHOUT ROWID)</returns>
    protected static (List<string> Definitions, string TableOptions) SplitTableDefinition(string sql)
    {
        var definitions = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        char? quote = null;

        for (var i = 0; i < sql.Length; i++)
        {
            var character = sql[i];

            if (quote is { } openingQuote)
            {
                if (depth > 0)
                    current.Append(character);

                if (character == (openingQuote == '[' ? ']' : openingQuote))
                {
                    //a doubled quote is an escaped quote inside the name or literal
                    if (openingQuote != '[' && i + 1 < sql.Length && sql[i + 1] == openingQuote)
                    {
                        if (depth > 0)
                            current.Append(sql[i + 1]);
                        i++;
                        continue;
                    }

                    quote = null;
                }

                continue;
            }

            switch (character)
            {
                case '\'' or '"' or '`' or '[':
                    quote = character;
                    break;

                case '(':
                    depth++;
                    if (depth == 1)
                        continue;
                    break;

                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        definitions.Add(current.ToString().Trim());
                        return (definitions, sql[(i + 1)..].TrimEnd().TrimEnd(';'));
                    }
                    break;

                case ',' when depth == 1:
                    definitions.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
            }

            if (depth > 0)
                current.Append(character);
        }

        throw new DataException($"Unexpected table definition: {sql}");
    }

    /// <summary>
    /// Gets the name of the column a definition declares
    /// </summary>
    /// <param name="definition">Column or table constraint definition</param>
    /// <returns>Column name; null for a table constraint</returns>
    protected static string GetColumnName(string definition)
    {
        if (string.IsNullOrEmpty(definition))
            return null;

        var opening = definition[0];
        if (opening is '"' or '`' or '[' or '\'')
        {
            var closing = opening == '[' ? ']' : opening;
            var name = new StringBuilder();

            for (var i = 1; i < definition.Length; i++)
            {
                if (definition[i] == closing)
                {
                    if (closing != ']' && i + 1 < definition.Length && definition[i + 1] == closing)
                    {
                        name.Append(closing);
                        i++;
                        continue;
                    }

                    return name.ToString();
                }

                name.Append(definition[i]);
            }

            return null;
        }

        var word = definition.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries)[0];

        //the keywords starting a table constraint can't be unquoted column names
        return word.ToUpperInvariant() is "CONSTRAINT" or "PRIMARY" or "UNIQUE" or "CHECK" or "FOREIGN" ? null : word;
    }

    /// <summary>
    /// Normalizes a definition for comparison (case and white space don't matter)
    /// </summary>
    /// <param name="definition">Definition</param>
    /// <returns>Normalized definition</returns>
    protected static string NormalizeDefinition(string definition)
    {
        return WhiteSpaceRegex().Replace(definition.Trim(), " ").ToUpperInvariant();
    }

    /// <summary>
    /// Rebuilds a table with new definitions of its columns and constraints, the way the SQLite documentation describes
    /// for the schema changes ALTER TABLE doesn't support (https://www.sqlite.org/lang_altertable.html#otheralter)
    /// </summary>
    /// <param name="tableName">Table name</param>
    /// <param name="definitions">New column and constraint definitions; every column of the table must remain</param>
    /// <param name="tableOptions">Table options (text after the definitions in CREATE TABLE)</param>
    /// <remarks>
    /// The new table is created under a temporary name, filled, the old table dropped and the new one renamed: renaming the old
    /// table first would rewrite the foreign keys referencing it. Foreign keys are off meanwhile, otherwise dropping the old
    /// table would delete the rows referencing it (ON DELETE CASCADE). Indexes and triggers are created again, the AUTOINCREMENT
    /// sequence keeps its value (identifiers are never reused), and the foreign keys are checked before the commit
    /// </remarks>
    protected virtual void RebuildTable(string tableName, IList<string> definitions, string tableOptions)
    {
        var table = _quoter.QuoteTableName(tableName);
        var temporaryName = $"{tableName}_nop_rebuild";
        var temporaryTable = _quoter.QuoteTableName(temporaryName);

        var columns = string.Join(", ", Query("SELECT name FROM pragma_table_info(@table);", ("@table", tableName))
            .Select(row => _quoter.QuoteColumnName(Convert.ToString(row[0]))));

        //indexes and triggers of the table (the indexes of PRIMARY KEY and UNIQUE constraints have no SQL: the table creates them)
        var dependents = Query("SELECT sql FROM sqlite_schema WHERE tbl_name = @table COLLATE NOCASE AND type IN ('index', 'trigger') AND sql IS NOT NULL;",
            ("@table", tableName)).Select(row => Convert.ToString(row[0])).ToList();

        var hasSequence = Query("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'sqlite_sequence';").Count > 0;
        var sequence = hasSequence
            ? Query("SELECT seq FROM sqlite_sequence WHERE name = @table COLLATE NOCASE;", ("@table", tableName)).Select(row => Convert.ToInt64(row[0])).FirstOrDefault()
            : 0;

        var foreignKeys = Convert.ToInt64(Query("PRAGMA foreign_keys;")[0][0]) == 1;
        if (foreignKeys)
            ExecuteStatement("PRAGMA foreign_keys = OFF;");

        try
        {
            ExecuteStatement("BEGIN IMMEDIATE;");

            try
            {
                ExecuteStatement($"DROP TABLE IF EXISTS {temporaryTable};");
                ExecuteStatement($"CREATE TABLE {temporaryTable} ({string.Join(", ", definitions)}){tableOptions};");
                ExecuteStatement($"INSERT INTO {temporaryTable} ({columns}) SELECT {columns} FROM {table};");
                ExecuteStatement($"DROP TABLE {table};");
                ExecuteStatement($"ALTER TABLE {temporaryTable} RENAME TO {table};");

                foreach (var sql in dependents)
                    ExecuteStatement(sql);

                if (sequence > 0)
                {
                    ExecuteStatement("UPDATE sqlite_sequence SET seq = MAX(seq, @sequence) WHERE name = @table;", ("@sequence", sequence), ("@table", tableName));
                    ExecuteStatement("INSERT INTO sqlite_sequence (name, seq) SELECT @table, @sequence WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = @table);",
                        ("@sequence", sequence), ("@table", tableName));
                }

                if (foreignKeys && Query($"PRAGMA foreign_key_check({table});") is { Count: > 0 } violations)
                    throw new DataException($"Rebuilding the table {tableName} would break {violations.Count} foreign key(s), e.g. to {violations[0][2]}");

                ExecuteStatement("COMMIT;");
            }
            catch
            {
                ExecuteStatement("ROLLBACK;");
                throw;
            }
        }
        finally
        {
            if (foreignKeys)
                ExecuteStatement("PRAGMA foreign_keys = ON;");
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpaceRegex();

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

    /// <summary>
    /// Alters a column (SQLite has no ALTER COLUMN, see remarks of the class)
    /// </summary>
    /// <param name="expression">Expression</param>
    public override void Process(AlterColumnExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (Generator is not NopSqliteMigrationGenerator generator)
            throw new NotSupportedException($"Altering a column needs the {nameof(NopSqliteMigrationGenerator)}");

        var tableName = expression.TableName;
        var columnName = expression.Column.Name;
        var definition = generator.GenerateColumnDefinition(expression.Column);

        var createTable = Query("SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = @table COLLATE NOCASE;", ("@table", tableName))
            .Select(row => Convert.ToString(row[0])).FirstOrDefault()
            ?? throw new DataException($"The table {tableName} doesn't exist");

        var (definitions, tableOptions) = SplitTableDefinition(createTable);
        var index = definitions.FindIndex(item => string.Equals(GetColumnName(item), columnName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new DataException($"The column {tableName}.{columnName} doesn't exist");

        if (NormalizeDefinition(definitions[index]) == NormalizeDefinition(definition))
        {
            Logger.LogInformation("SQLite: {Table}.{Column} already is {Definition}, nothing to alter", tableName, columnName, definition);
            return;
        }

        if (expression.Column.IsPrimaryKey || expression.Column.IsIdentity || NormalizeDefinition(definitions[index]).Contains("PRIMARY KEY"))
            throw new NotSupportedException($"SQLite: altering the primary key column {tableName}.{columnName} isn't supported");

        Logger.LogInformation("SQLite: rebuilding the table {Table} to alter {Old} into {New}", tableName, definitions[index], definition);

        if (Options.PreviewOnly)
            return;

        definitions[index] = definition;
        RebuildTable(tableName, definitions, tableOptions);
    }

    #endregion
}
