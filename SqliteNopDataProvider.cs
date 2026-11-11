using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Data.Sqlite;
using Nop.Core;
using Nop.Data;
using Nop.Data.DataProviders;
using Nop.Data.Mapping;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the SQLite data provider: the whole store lives in a single local file and no database server is required
/// </summary>
/// <remarks>
/// All writes are executed by the background writer of the database (see <see cref="SqliteWriteQueue"/>),
/// reads run in parallel on their own connections
/// </remarks>
public partial class SqliteNopDataProvider : BaseDataProvider, INopDataProvider
{
    #region Fields

    protected static readonly Lazy<IDataProvider> _dataProvider = new(() => SQLiteTools.GetDataProvider(ProviderName.SQLiteMS), true);

    #endregion

    #region Utilities

    /// <summary>
    /// Gets a connection to the database for a current data provider
    /// </summary>
    /// <param name="connectionString">Connection string</param>
    /// <returns>Connection to a database</returns>
    protected override DbConnection GetInternalDbConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        return new NopSqliteConnection(connectionString);
    }

    /// <summary>
    /// Gets the SQLite connection of a connection used by the store
    /// </summary>
    /// <param name="connection">Connection</param>
    /// <returns>SQLite connection</returns>
    protected static SqliteConnection GetSqliteConnection(DbConnection connection)
    {
        return connection as SqliteConnection
            ?? throw new NopException($"Unexpected connection type: {connection?.GetType().Name}");
    }

    /// <summary>
    /// Gets the connection string builder of the current connection string (with the database path resolved)
    /// </summary>
    /// <returns>The connection string builder</returns>
    protected static SqliteConnectionStringBuilder GetConnectionStringBuilder()
    {
        return new SqliteConnectionStringBuilder(SqliteConnectionStringHelper.Normalize(DataSettingsManager.LoadSettings().ConnectionString));
    }

    /// <summary>
    /// Gets the descriptor of a mapped entity
    /// </summary>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>Entity descriptor</returns>
    protected static NopEntityDescriptor GetEntityDescriptor<TEntity>() where TEntity : BaseEntity
    {
        return NopMappingSchema.GetEntityDescriptor(typeof(TEntity))
            ?? throw new NopException($"Mapped entity descriptor is not found: {typeof(TEntity).Name}");
    }

    /// <summary>
    /// Gets the next identifier of a table
    /// </summary>
    /// <param name="dataConnection">Database connection; it must hold the write lock until the identifiers are used</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>Next identifier</returns>
    /// <remarks>
    /// Unlike MAX(Id) + 1 it never reuses identifiers of deleted rows (the AUTOINCREMENT guarantee), so a row of
    /// a table without a foreign key (generic attributes, URL records, localized properties...) can't be attached to a new entity
    /// </remarks>
    protected virtual long GetNextIdentity<TEntity>(DataConnection dataConnection) where TEntity : BaseEntity
    {
        var descriptor = GetEntityDescriptor<TEntity>();
        var keyColumn = descriptor.Fields.FirstOrDefault(field => field.IsPrimaryKey)?.Name ?? nameof(BaseEntity.Id);

        var lastIdentity = dataConnection.Execute<long?>(
            $"SELECT MAX(COALESCE((SELECT seq FROM sqlite_sequence WHERE name = @tableName), 0), COALESCE((SELECT MAX(\"{keyColumn}\") FROM \"{descriptor.EntityName}\"), 0))",
            new DataParameter("tableName", descriptor.EntityName, DataType.NVarChar));

        return (lastIdentity ?? 0) + 1;
    }

    /// <summary>
    /// Assigns identifiers to new entities
    /// </summary>
    /// <param name="dataConnection">Database connection; it must hold the write lock until the entities are inserted</param>
    /// <param name="entities">Entities</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    protected virtual void AssignIdentities<TEntity>(DataConnection dataConnection, IList<TEntity> entities) where TEntity : BaseEntity
    {
        var identity = GetNextIdentity<TEntity>(dataConnection);
        foreach (var entity in entities)
            entity.Id = checked((int)identity++);
    }

    /// <summary>
    /// Inserts entities in one transaction, keeping the assigned identifiers
    /// </summary>
    /// <param name="dataConnection">Writer connection</param>
    /// <param name="entities">Entities</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    protected virtual void BulkInsert<TEntity>(DataConnection dataConnection, IList<TEntity> entities) where TEntity : BaseEntity
    {
        //Microsoft.Data.Sqlite begins transactions with BEGIN IMMEDIATE: the identifiers can't be taken by another process meanwhile
        using var transaction = dataConnection.BeginTransaction();

        AssignIdentities(dataConnection, entities);
        dataConnection.BulkCopy(new BulkCopyOptions { KeepIdentity = true }, entities);

        transaction.Commit();
    }

    /// <summary>
    /// Updates entities in one transaction (every SQLite commit is a disk write)
    /// </summary>
    /// <param name="dataConnection">Writer connection</param>
    /// <param name="entities">Entities</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    protected virtual void UpdateAll<TEntity>(DataConnection dataConnection, IList<TEntity> entities) where TEntity : BaseEntity
    {
        using var transaction = dataConnection.BeginTransaction();

        foreach (var entity in entities)
            dataConnection.Update(entity);

        transaction.Commit();
    }

    /// <summary>
    /// Deletes entities in one transaction
    /// </summary>
    /// <param name="dataConnection">Writer connection</param>
    /// <param name="entities">Entities</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    protected virtual void DeleteAll<TEntity>(DataConnection dataConnection, IList<TEntity> entities) where TEntity : BaseEntity
    {
        using var transaction = dataConnection.BeginTransaction();

        if (entities.All(entity => entity.Id == 0))
        {
            foreach (var entity in entities)
                dataConnection.Delete(entity);
        }
        else
        {
            //batches keep every statement far below the SQLite limit of bound variables
            foreach (var ids in entities.Select(entity => entity.Id).Distinct().Chunk(SqliteDataProviderDefaults.DeleteBatchSize))
                dataConnection.GetTable<TEntity>().Where(entity => ids.Contains(entity.Id)).Delete();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Opens a connection to the database file without nopCommerce defaults (journal mode, foreign keys...)
    /// </summary>
    /// <param name="dataSource">Path of the database file</param>
    /// <param name="mode">Open mode</param>
    /// <returns>Opened connection</returns>
    protected static SqliteConnection OpenPlainConnection(string dataSource, SqliteOpenMode mode)
    {
        //no pooling: the file must not stay locked once the connection is closed (backups are downloaded, deleted...)
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = mode,
            Pooling = false
        }.ConnectionString);
        connection.Open();

        return connection;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Creates the database by using the loaded connection string
    /// </summary>
    /// <param name="triesToConnect">Count of tries to connect to the database after creating (not used: the file is created synchronously)</param>
    /// <remarks>
    /// Text columns get the case-insensitive NOCASE collation, whatever collation the data settings name
    /// </remarks>
    public void CreateDatabase(int triesToConnect = 10)
    {
        var builder = GetConnectionStringBuilder();

        if (!SqliteConnectionStringHelper.IsInMemory(builder))
        {
            var directory = Path.GetDirectoryName(builder.DataSource);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            builder.Mode = SqliteOpenMode.ReadWriteCreate;
        }

        //SQLite has no "CREATE DATABASE": the file is created when the first connection opens it,
        //and that connection also switches the new database to WAL mode
        using var connection = new NopSqliteConnection(builder.ConnectionString);
        connection.Open();
    }

    /// <summary>
    /// Checks if the specified database exists, returns true if database exists
    /// </summary>
    /// <returns>Returns true if the database exists.</returns>
    public bool DatabaseExists()
    {
        var builder = GetConnectionStringBuilder();

        if (SqliteConnectionStringHelper.IsInMemory(builder))
            return true;

        //opening the connection would create the file, so check it first
        if (!File.Exists(builder.DataSource))
            return false;

        try
        {
            using var connection = OpenPlainConnection(builder.DataSource, SqliteOpenMode.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            command.ExecuteScalar();

            return true;
        }
        catch (SqliteException)
        {
            //the file isn't a SQLite database
            return false;
        }
    }

    /// <summary>
    /// Checks if the specified database exists, returns true if database exists
    /// </summary>
    /// <returns>
    /// A task that represents the asynchronous operation
    /// The task result contains the returns true if the database exists.
    /// </returns>
    public Task<bool> DatabaseExistsAsync()
    {
        return Task.FromResult(DatabaseExists());
    }

    /// <summary>
    /// Returns queryable source for specified mapping class for current connection,
    /// mapped to database table or view.
    /// </summary>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>Queryable source</returns>
    public override IQueryable<TEntity> GetTable<TEntity>()
    {
        //the base implementation lets linq2db create plain connections from the connection string,
        //so queries wouldn't resolve the database path nor get the connection settings
        var options = new DataOptions()
            .UseConnectionFactory(LinqToDbDataProvider, _ => CreateDbConnection())
            .UseMappingSchema(NopMappingSchema.GetMappingSchema(ConfigurationName, LinqToDbDataProvider));

        return new DataContext(options)
        {
            CommandTimeout = DataSettingsManager.GetSqlCommandTimeout()
        }
        .GetTable<TEntity>();
    }

    /// <summary>
    /// Inserts record into table. Returns inserted entity with identity
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="TEntity"></typeparam>
    /// <returns>
    /// A task that represents the asynchronous operation
    /// The task result contains the inserted entity
    /// </returns>
    public override Task<TEntity> InsertEntityAsync<TEntity>(TEntity entity)
    {
        return Writer.EnqueueAsync(connection =>
        {
            entity.Id = connection.InsertWithInt32Identity(entity);
            return entity;
        }, batchable: true);
    }

    /// <summary>
    /// Inserts record into table. Returns inserted entity with identity
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="TEntity"></typeparam>
    /// <returns>Inserted entity</returns>
    public override TEntity InsertEntity<TEntity>(TEntity entity)
    {
        return Writer.Execute(connection =>
        {
            entity.Id = connection.InsertWithInt32Identity(entity);
            return entity;
        }, batchable: true);
    }

    /// <summary>
    /// Updates record in table, using values from entity parameter.
    /// Record to update identified by match on primary key value from obj value.
    /// </summary>
    /// <param name="entity">Entity with data to update</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>A task that represents the asynchronous operation</returns>
    public override Task UpdateEntityAsync<TEntity>(TEntity entity)
    {
        return Writer.EnqueueAsync(connection => connection.Update(entity), batchable: true);
    }

    /// <summary>
    /// Updates record in table, using values from entity parameter.
    /// Record to update identified by match on primary key value from obj value.
    /// </summary>
    /// <param name="entity">Entity with data to update</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    public override void UpdateEntity<TEntity>(TEntity entity)
    {
        Writer.Execute(connection => connection.Update(entity), batchable: true);
    }

    /// <summary>
    /// Updates records in table, using values from entity parameter.
    /// Records to update are identified by match on primary key value from obj value.
    /// </summary>
    /// <param name="entities">Entities with data to update</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>A task that represents the asynchronous operation</returns>
    public override Task UpdateEntitiesAsync<TEntity>(IEnumerable<TEntity> entities)
    {
        var items = entities.ToList();

        return items.Count > 0 ? Writer.EnqueueAsync(connection => UpdateAll(connection, items)) : Task.CompletedTask;
    }

    /// <summary>
    /// Updates records in table, using values from entity parameter.
    /// Records to update are identified by match on primary key value from obj value.
    /// </summary>
    /// <param name="entities">Entities with data to update</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    public override void UpdateEntities<TEntity>(IEnumerable<TEntity> entities)
    {
        var items = entities.ToList();

        if (items.Count > 0)
            Writer.Execute(connection => UpdateAll(connection, items));
    }

    /// <summary>
    /// Deletes record in table. Record to delete identified
    /// by match on primary key value from obj value.
    /// </summary>
    /// <param name="entity">Entity for delete operation</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>A task that represents the asynchronous operation</returns>
    public override Task DeleteEntityAsync<TEntity>(TEntity entity)
    {
        return Writer.EnqueueAsync(connection => connection.Delete(entity), batchable: true);
    }

    /// <summary>
    /// Deletes record in table. Record to delete identified
    /// by match on primary key value from obj value.
    /// </summary>
    /// <param name="entity">Entity for delete operation</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    public override void DeleteEntity<TEntity>(TEntity entity)
    {
        Writer.Execute(connection => connection.Delete(entity), batchable: true);
    }

    /// <summary>
    /// Performs delete records in a table
    /// </summary>
    /// <param name="entities">Entities for delete operation</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>A task that represents the asynchronous operation</returns>
    public override Task BulkDeleteEntitiesAsync<TEntity>(IList<TEntity> entities)
    {
        var items = entities.ToList();

        return items.Count > 0 ? Writer.EnqueueAsync(connection => DeleteAll(connection, items)) : Task.CompletedTask;
    }

    /// <summary>
    /// Performs delete records in a table
    /// </summary>
    /// <param name="entities">Entities for delete operation</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    public override void BulkDeleteEntities<TEntity>(IList<TEntity> entities)
    {
        var items = entities.ToList();

        if (items.Count > 0)
            Writer.Execute(connection => DeleteAll(connection, items));
    }

    /// <summary>
    /// Performs delete records in a table by a condition
    /// </summary>
    /// <param name="predicate">A function to test each element for a condition.</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>
    /// A task that represents the asynchronous operation
    /// The task result contains the number of deleted records
    /// </returns>
    public override Task<int> BulkDeleteEntitiesAsync<TEntity>(Expression<Func<TEntity, bool>> predicate)
    {
        return Writer.EnqueueAsync(connection => connection.GetTable<TEntity>().Where(predicate).Delete(), batchable: true);
    }

    /// <summary>
    /// Performs delete records in a table by a condition
    /// </summary>
    /// <param name="predicate">A function to test each element for a condition.</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>
    /// The number of deleted records
    /// </returns>
    public override int BulkDeleteEntities<TEntity>(Expression<Func<TEntity, bool>> predicate)
    {
        return Writer.Execute(connection => connection.GetTable<TEntity>().Where(predicate).Delete(), batchable: true);
    }

    /// <summary>
    /// Performs bulk insert operation for entity collection.
    /// </summary>
    /// <param name="entities">Entities for insert operation</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>A task that represents the asynchronous operation</returns>
    public override Task BulkInsertEntitiesAsync<TEntity>(IEnumerable<TEntity> entities)
    {
        var items = entities.ToList();

        return items.Count > 0 ? Writer.EnqueueAsync(connection => BulkInsert(connection, items)) : Task.CompletedTask;
    }

    /// <summary>
    /// Performs bulk insert operation for entity collection.
    /// </summary>
    /// <param name="entities">Entities for insert operation</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    public override void BulkInsertEntities<TEntity>(IEnumerable<TEntity> entities)
    {
        var items = entities.ToList();

        if (items.Count > 0)
            Writer.Execute(connection => BulkInsert(connection, items));
    }

    /// <summary>
    /// Executes command asynchronously and returns number of affected records
    /// </summary>
    /// <param name="sql">Command text</param>
    /// <param name="dataParameters">Command parameters</param>
    /// <returns>
    /// A task that represents the asynchronous operation
    /// The task result contains the number of records, affected by command execution.
    /// </returns>
    public override Task<int> ExecuteNonQueryAsync(string sql, params DataParameter[] dataParameters)
    {
        return Writer.EnqueueAsync(connection => new CommandInfo(connection, sql, dataParameters).Execute());
    }

    /// <summary>
    /// Truncates database table
    /// </summary>
    /// <param name="resetIdentity">Performs reset identity column</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    public override Task TruncateAsync<TEntity>(bool resetIdentity = false)
    {
        return Writer.EnqueueAsync(connection => connection.GetTable<TEntity>().Truncate(resetIdentity));
    }

    /// <summary>
    /// Get hash values of a stored entity field
    /// </summary>
    /// <param name="predicate">A function to test each element for a condition.</param>
    /// <param name="keySelector">A key selector which should project to a dictionary key</param>
    /// <param name="fieldSelector">A field selector to apply a transform to a hash value</param>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>Dictionary</returns>
    public override async Task<IDictionary<int, string>> GetFieldHashesAsync<TEntity>(Expression<Func<TEntity, bool>> predicate,
        Expression<Func<TEntity, int>> keySelector,
        Expression<Func<TEntity, object>> fieldSelector)
    {
        if (keySelector.Body is not MemberExpression keyMember ||
            keyMember.Member is not PropertyInfo keyPropInfo)
        {
            throw new ArgumentException($"Expression '{keySelector}' refers to method or field, not a property.");
        }

        var fieldBody = fieldSelector.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : fieldSelector.Body;
        if (fieldBody is not MemberExpression member ||
            member.Member is not PropertyInfo propInfo)
        {
            throw new ArgumentException($"Expression '{fieldSelector}' refers to a method or field, not a property.");
        }

        //SQLite has no SHA-2 function, so values are hashed by the application; the database is local, there is no network
        //to cross. The hash is SHA-512 of the whole value as hex, the format the other providers return
        var values = GetTable<TEntity>()
            .Where(predicate)
            .Select(entity => new
            {
                Id = Sql.Property<int>(entity, keyPropInfo.Name),
                Value = Sql.Property<byte[]>(entity, propInfo.Name)
            });

        var hashes = new Dictionary<int, string>();
        await foreach (var value in LinqToDB.AsyncExtensions.AsAsyncEnumerable(values))
            hashes[value.Id] = HashHelper.CreateHash(value.Value ?? [], SqliteDataProviderDefaults.HashAlgorithm);

        return hashes;
    }

    /// <summary>
    /// Get the current identity value
    /// </summary>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <returns>
    /// A task that represents the asynchronous operation
    /// The task result contains the last identity value issued for the table (1 if no row has been inserted yet, like SQL Server)
    /// </returns>
    public virtual async Task<int?> GetTableIdentAsync<TEntity>() where TEntity : BaseEntity
    {
        using var dataConnection = CreateDataConnection();

        var identity = await dataConnection.ExecuteAsync<long?>("SELECT seq FROM sqlite_sequence WHERE name = @tableName",
            new DataParameter("tableName", GetEntityDescriptor<TEntity>().EntityName, DataType.NVarChar));

        return identity.HasValue ? Convert.ToInt32(identity.Value) : 1;
    }

    /// <summary>
    /// Set table identity (is supported)
    /// </summary>
    /// <typeparam name="TEntity">Entity type</typeparam>
    /// <param name="ident">Identity value</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public virtual Task SetTableIdentAsync<TEntity>(int ident) where TEntity : BaseEntity
    {
        var tableName = GetEntityDescriptor<TEntity>().EntityName;

        return Writer.EnqueueAsync(connection =>
        {
            var parameters = new[]
            {
                new DataParameter("tableName", tableName, DataType.NVarChar),
                new DataParameter("ident", ident, DataType.Int32)
            };

            //like SQL Server, the identity only moves forward
            var currentIdent = connection.Execute<long?>("SELECT seq FROM sqlite_sequence WHERE name = @tableName", parameters) ?? 1;
            if (ident <= currentIdent)
                return;

            //sqlite_sequence is an ordinary table: a row exists only once the table got its first record
            if (connection.Execute("UPDATE sqlite_sequence SET seq = @ident WHERE name = @tableName", parameters) == 0)
                connection.Execute("INSERT INTO sqlite_sequence (name, seq) VALUES (@tableName, @ident)", parameters);
        }, batchable: true);
    }

    /// <summary>
    /// Creates a backup of the database
    /// </summary>
    /// <param name="fileName">Path of the backup file</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public virtual Task BackupDatabaseAsync(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        var directory = Path.GetDirectoryName(fileName);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var source = new NopSqliteConnection(DataSettings.ConnectionString);
        source.Open();

        using var destination = OpenPlainConnection(fileName, SqliteOpenMode.ReadWriteCreate);

        //the online backup API takes a consistent snapshot while the store keeps running
        source.BackupDatabase(destination);

        //the backup is a single self-contained file (no -wal/-shm companions)
        using var command = destination.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = DELETE;";
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores the database from a backup
    /// </summary>
    /// <param name="backupFileName">The name of the backup file</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public virtual Task RestoreDatabaseAsync(string backupFileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(backupFileName);

        if (!File.Exists(backupFileName))
            throw new FileNotFoundException("The database backup file is not found", backupFileName);

        //executed by the writer: no write of the store can happen while the database is replaced
        return Writer.EnqueueAsync(connection =>
        {
            using var source = OpenPlainConnection(backupFileName, SqliteOpenMode.ReadWrite);

            //never replace the store data with a damaged file
            using (var check = source.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check;";
                var result = check.ExecuteScalar() as string;

                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new DataException($"The database backup file is damaged: {result}");
            }

            var destination = GetSqliteConnection(connection.Connection);

            //the live database is replaced page by page; other connections see the restored data with their next statement
            source.BackupDatabase(destination);

            //the backup carries its own journal mode: switch the store database back to WAL
            connection.Execute("PRAGMA journal_mode = WAL;");
        });
    }

    /// <summary>
    /// Re-index database tables
    /// </summary>
    /// <returns>A task that represents the asynchronous operation</returns>
    public virtual Task ReIndexTablesAsync()
    {
        //rebuild the indexes, refresh the statistics of the query planner, defragment the file and shrink the write-ahead log
        return Writer.EnqueueAsync(connection =>
        {
            connection.Execute("REINDEX;");
            connection.Execute("ANALYZE;");
            connection.Execute("VACUUM;");
            connection.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        });
    }

    /// <summary>
    /// Shrinks database
    /// </summary>
    /// <returns>A task that represents the asynchronous operation</returns>
    public virtual Task ShrinkDatabaseAsync()
    {
        //rebuild the file without its free pages, then empty the write-ahead log
        return Writer.EnqueueAsync(connection =>
        {
            connection.Execute("VACUUM;");
            connection.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        });
    }

    /// <summary>
    /// Build the connection string
    /// </summary>
    /// <param name="nopConnectionString">Connection string info</param>
    /// <returns>Connection string</returns>
    public virtual string BuildConnectionString(INopConnectionStringInfo nopConnectionString)
    {
        ArgumentNullException.ThrowIfNull(nopConnectionString);

        //an embedded database has no server and no authentication: the database name is the name of the file
        var fileName = string.Concat((nopConnectionString.DatabaseName ?? string.Empty).Trim().Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrEmpty(fileName))
            fileName = SqliteDataProviderDefaults.DefaultDatabaseName;

        if (!Path.HasExtension(fileName))
            fileName += SqliteDataProviderDefaults.DatabaseFileExtension;

        //a relative path keeps the configuration valid when the application folder moves
        return new SqliteConnectionStringBuilder
        {
            DataSource = $"{SqliteDataProviderDefaults.DatabaseDirectory}/{fileName}"
        }.ConnectionString;
    }

    /// <summary>
    /// Gets the name of a foreign key
    /// </summary>
    /// <param name="foreignTable">Foreign key table</param>
    /// <param name="foreignColumn">Foreign key column name</param>
    /// <param name="primaryTable">Primary table</param>
    /// <param name="primaryColumn">Primary key column name</param>
    /// <returns>Name of a foreign key</returns>
    public virtual string CreateForeignKeyName(string foreignTable, string foreignColumn, string primaryTable, string primaryColumn)
    {
        return $"FK_{foreignTable}_{foreignColumn}_{primaryTable}_{primaryColumn}";
    }

    /// <summary>
    /// Gets the name of an index
    /// </summary>
    /// <param name="targetTable">Target table name</param>
    /// <param name="targetColumn">Target column name</param>
    /// <returns>Name of an index</returns>
    public virtual string GetIndexName(string targetTable, string targetColumn)
    {
        return $"IX_{targetTable}_{targetColumn}";
    }

    /// <summary>
    /// Executes command using System.Data.CommandType.StoredProcedure command type and
    /// returns results as collection of values of specified type
    /// </summary>
    /// <typeparam name="T">Result record type</typeparam>
    /// <param name="procedureName">Procedure name</param>
    /// <param name="parameters">Command parameters</param>
    /// <returns>Nothing: SQLite has no stored procedures</returns>
    public override Task<IList<T>> QueryProcAsync<T>(string procedureName, params DataParameter[] parameters)
    {
        throw new NotSupportedException($"SQLite doesn't support stored procedures ('{procedureName}')");
    }

    /// <summary>
    /// Gets the name of the database collation
    /// </summary>
    /// <returns>
    /// A task that represents the asynchronous operation
    /// The task result contains the collation name
    /// </returns>
    /// <remarks>
    /// SQLite has no database collation: the plugin creates every text column with NOCASE (see NopSqliteMigrationGenerator)
    /// </remarks>
    public virtual Task<string> GetDataBaseCollationAsync()
    {
        return Task.FromResult("NOCASE");
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the background writer of the database
    /// </summary>
    protected SqliteWriteQueue Writer => SqliteWriteQueue.Get(DataSettings.ConnectionString, CreateDataConnection);

    /// <summary>
    /// Linq2Db data provider (SQLite through Microsoft.Data.Sqlite)
    /// </summary>
    protected override IDataProvider LinqToDbDataProvider => _dataProvider.Value;

    /// <summary>
    /// Gets allowed a limit input value of the data for hashing functions, returns 0 if not limited
    /// </summary>
    public int SupportedLengthOfBinaryHash => 0;

    /// <summary>
    /// Gets a value indicating whether this data provider supports backup
    /// </summary>
    public bool BackupSupported => true;

    #endregion
}
