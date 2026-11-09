using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nop.Core.Infrastructure;
using Nop.Data;
using Nop.Plugin.Data.Sqlite.Filters;
using Nop.Plugin.Data.Sqlite.Installation;
using Nop.Web.Infrastructure.Installation;

namespace Nop.Plugin.Data.Sqlite.Infrastructure;

/// <summary>
/// Represents the startup adding SQLite to the installation wizard and to the admin area
/// </summary>
public class SqliteInstallationStartup : INopStartup
{
    /// <summary>
    /// Add and configure any of the middleware
    /// </summary>
    /// <param name="services">Collection of service descriptors</param>
    /// <param name="configuration">Configuration of the application</param>
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        if (!DataSettingsManager.IsDatabaseInstalled())
        {
            //installation wizard: list SQLite and install the store when it's selected
            services.AddScoped<InstallationLocalizationService>();
            services.AddScoped<IInstallationLocalizationService, SqliteInstallationLocalizationService>();
            services.Configure<MvcOptions>(options => options.Filters.Add<SqliteInstallationFilter>());

            return;
        }

        //admin area: keep SQLite on the "App settings" page
        if (SqliteDataSettings.IsSqlite(DataSettingsManager.LoadSettings()))
            services.Configure<MvcOptions>(options => options.Filters.Add<SqliteAppSettingsFilter>());
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
    /// <remarks>
    /// After the NopStartup of Nop.Web (2002), which registers the installation localization service
    /// </remarks>
    public int Order => 2100;
}
