using Nop.Core;
using Nop.Services.Plugins;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the plugin supplying the SQLite data provider
/// </summary>
public class SqlitePlugin : BasePlugin
{
    #region Utilities

    /// <summary>
    /// Ensure the store doesn't run on SQLite
    /// </summary>
    /// <exception cref="NopException">The store runs on SQLite</exception>
    protected virtual void EnsureStoreDoesNotRunOnSqlite()
    {
        //the store would lose access to its database
        if (SqliteDataSettings.IsSqliteStore())
            throw new NopException("The store runs on SQLite, so the SQLite data provider can't be uninstalled");
    }

    #endregion

    #region Methods

    /// <summary>
    /// Prepare plugin to the uninstallation
    /// </summary>
    /// <returns>A task that represents the asynchronous operation</returns>
    public override Task PreparePluginToUninstallAsync()
    {
        //refuse right away in the admin area
        EnsureStoreDoesNotRunOnSqlite();

        return base.PreparePluginToUninstallAsync();
    }

    /// <summary>
    /// Uninstall plugin
    /// </summary>
    /// <returns>A task that represents the asynchronous operation</returns>
    public override Task UninstallAsync()
    {
        //e.g. when the plugin was marked to be uninstalled in App_Data/plugins.json
        EnsureStoreDoesNotRunOnSqlite();

        return base.UninstallAsync();
    }

    #endregion
}
