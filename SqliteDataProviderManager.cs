using Nop.Data;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the data provider manager supplying the SQLite data provider to a store running on SQLite
/// </summary>
/// <remarks>
/// Replaces DataProviderManager, which only knows the built-in data providers; other providers are created by it as usual
/// </remarks>
public class SqliteDataProviderManager : IDataProviderManager
{
    /// <summary>
    /// Gets data provider
    /// </summary>
    public virtual INopDataProvider DataProvider
    {
        get
        {
            var dataSettings = DataSettingsManager.LoadSettings();

            return SqliteDataSettings.IsSqlite(dataSettings)
                ? new SqliteNopDataProvider()
                : DataProviderManager.GetDataProvider(dataSettings.DataProvider);
        }
    }
}
