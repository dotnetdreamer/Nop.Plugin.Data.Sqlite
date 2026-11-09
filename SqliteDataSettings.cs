using Nop.Data;
using Nop.Data.Configuration;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the SQLite-specific handling of the data settings (App_Data/appsettings.json)
/// </summary>
/// <remarks>
/// nopCommerce's DataProviderType enum only names the built-in providers, so a store running on SQLite is configured
/// with a value outside of them: "DataProvider": 100 (see <see cref="SqliteDataProviderDefaults.DataProviderTypeId"/>).
/// The class derives from DataSettingsManager to reach its cached "database is installed" flag (see <see cref="HideDatabase"/>).
/// </remarks>
public class SqliteDataSettings : DataSettingsManager
{
    #region Fields

    protected static bool _databaseHidden;

    #endregion

    #region Methods

    /// <summary>
    /// Check whether the data settings select SQLite
    /// </summary>
    /// <param name="dataConfig">Data settings</param>
    /// <returns>True if the data settings select SQLite; otherwise false</returns>
    public static bool IsSqlite(DataConfig dataConfig)
    {
        return dataConfig?.DataProvider == ProviderType;
    }

    /// <summary>
    /// Check whether nopCommerce is installed and runs on SQLite
    /// </summary>
    /// <returns>True if the store runs on SQLite; otherwise false</returns>
    public static bool IsSqliteStore()
    {
        return IsDatabaseInstalled() && IsSqlite(LoadSettings());
    }

    /// <summary>
    /// Make the installed database invisible to nopCommerce until <see cref="RestoreDatabase"/> is called
    /// </summary>
    /// <remarks>
    /// NopDbStartup applies the schema migrations as soon as it has registered the migration services, with the processors
    /// of the built-in providers only. Hiding the database lets it skip them; SqliteDataProviderStartup, right after it,
    /// restores the database and applies the migrations with the SQLite processor.
    /// </remarks>
    public static void HideDatabase()
    {
        _databaseIsInstalled = false;
        _databaseHidden = true;
    }

    /// <summary>
    /// Make the database hidden by <see cref="HideDatabase"/> visible again
    /// </summary>
    /// <returns>True if the database was hidden; otherwise false</returns>
    public static bool RestoreDatabase()
    {
        if (!_databaseHidden)
            return false;

        _databaseHidden = false;
        _databaseIsInstalled = null;

        return true;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the data provider type of SQLite
    /// </summary>
    public static DataProviderType ProviderType => (DataProviderType)SqliteDataProviderDefaults.DataProviderTypeId;

    #endregion
}
