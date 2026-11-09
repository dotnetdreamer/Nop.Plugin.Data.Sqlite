using FluentMigrator;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Generators;
using FluentMigrator.Runner.Generators.SQLite;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Processors;
using FluentMigrator.Runner.Processors.SQLite;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nop.Core;
using Nop.Core.Infrastructure;
using Nop.Data;
using Nop.Data.Migrations;

namespace Nop.Plugin.Data.Sqlite.Infrastructure;

/// <summary>
/// Represents the startup registering the SQLite data provider
/// </summary>
/// <remarks>
/// It runs right after NopDbStartup, whose registrations it overrides. On a store running on another database it changes nothing.
/// </remarks>
public class SqliteDataProviderStartup : INopStartup
{
    #region Utilities

    /// <summary>
    /// Apply the schema migrations skipped by NopDbStartup (see <see cref="SqliteEarlyStartup"/>), the way it does
    /// </summary>
    /// <param name="services">Collection of service descriptors</param>
    protected virtual void ApplySchemaMigrations(IServiceCollection services)
    {
        var migrationAssemblies = Singleton<ITypeFinder>.Instance.FindClassesOfType<MigrationBase>()
            .Select(type => type.Assembly)
            .Where(assembly => !assembly.FullName.Contains("FluentMigrator.Runner"))
            .Distinct()
            .ToArray();

        using var scope = services.BuildServiceProvider().CreateScope();
        var migrationManager = scope.ServiceProvider.GetRequiredService<IMigrationManager>();
        foreach (var assembly in migrationAssemblies)
            migrationManager.ApplyUpSchemaMigrations(assembly);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Add and configure any of the middleware
    /// </summary>
    /// <param name="services">Collection of service descriptors</param>
    /// <param name="configuration">Configuration of the application</param>
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        //first of all make the database visible again
        var schemaMigrationsSkipped = SqliteDataSettings.RestoreDatabase();

        //a store running on another database keeps the nopCommerce services
        if (DataSettingsManager.IsDatabaseInstalled() && !SqliteDataSettings.IsSqlite(DataSettingsManager.LoadSettings()))
            return;

        SqliteNativeLibraryResolver.Register();

        //the data provider (INopDataProvider is resolved through IDataProviderManager)
        services.AddTransient<IDataProviderManager, SqliteDataProviderManager>();

        //FluentMigrator: the SQLite processor, using the store connections and the nopCommerce-aware DDL generator
        services.AddScoped<IProcessorAccessor, SqliteProcessorAccessor>();
        services.ConfigureRunner(runnerBuilder => runnerBuilder.AddSQLite());
        services.Replace(ServiceDescriptor.Scoped<SQLiteDbFactory, NopSqliteDbFactory>());
        services.Replace(ServiceDescriptor.Scoped<SQLiteGenerator>(serviceProvider => new NopSqliteMigrationGenerator(new SQLiteQuoter(),
            serviceProvider.GetRequiredService<ISQLiteTypeMap>(),
            serviceProvider.GetRequiredService<IOptions<GeneratorOptions>>(),
            serviceProvider.GetService<ILogger<NopSqliteMigrationGenerator>>())));
        services.Replace(ServiceDescriptor.Scoped<SQLiteProcessor>(serviceProvider => new NopSqliteMigrationProcessor(
            serviceProvider.GetRequiredService<SQLiteDbFactory>(),
            serviceProvider.GetRequiredService<SQLiteGenerator>(),
            serviceProvider.GetRequiredService<ILogger<SQLiteProcessor>>(),
            serviceProvider.GetRequiredService<IOptionsSnapshot<ProcessorOptions>>(),
            serviceProvider.GetRequiredService<IConnectionStringAccessor>(),
            serviceProvider,
            new SQLiteQuoter())));

        if (schemaMigrationsSkipped)
            ApplySchemaMigrations(services);
    }

    /// <summary>
    /// Configure the using of added middleware
    /// </summary>
    /// <param name="application">Builder for configuring an application's request pipeline</param>
    public void Configure(IApplicationBuilder application)
    {
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets order of this startup configuration implementation
    /// </summary>
    /// <remarks>
    /// Right after NopDbStartup (10)
    /// </remarks>
    public int Order => 11;

    #endregion
}
