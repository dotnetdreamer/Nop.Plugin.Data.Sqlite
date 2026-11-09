using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nop.Core.Infrastructure;

namespace Nop.Plugin.Data.Sqlite.Infrastructure;

/// <summary>
/// Represents the startup running before NopDbStartup
/// </summary>
/// <remarks>
/// NopDbStartup (10) applies the schema migrations as soon as it has registered the migration services, with the processors
/// of the built-in data providers only. On a store running on SQLite this startup hides the database from it, so it skips them;
/// <see cref="SqliteDataProviderStartup"/> (11) restores the database and applies them with the SQLite processor.
/// </remarks>
public class SqliteEarlyStartup : INopStartup
{
    /// <summary>
    /// Add and configure any of the middleware
    /// </summary>
    /// <param name="services">Collection of service descriptors</param>
    /// <param name="configuration">Configuration of the application</param>
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        if (SqliteDataSettings.IsSqliteStore())
            SqliteDataSettings.HideDatabase();
    }

    /// <summary>
    /// Configure the using of added middleware
    /// </summary>
    /// <param name="application">Builder for configuring an application's request pipeline</param>
    public void Configure(IApplicationBuilder application)
    {
    }

    /// <summary>
    /// Gets order of this startup configuration implementation
    /// </summary>
    public int Order => 5;
}
