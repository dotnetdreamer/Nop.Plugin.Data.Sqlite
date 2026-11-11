using SQLitePCL;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Represents the process-wide settings of SQLite, applied before the first connection opens
/// </summary>
public static class SqliteGlobalSettings
{
    #region Fields

    private const int SQLITE_CONFIG_MEMSTATUS = 9;

    private static int _applied;

    #endregion

    #region Methods

    /// <summary>
    /// Applies the settings; only the first call does something, as they can't change once SQLite is initialized
    /// </summary>
    /// <remarks>
    /// Memory statistics are turned off. With them, SQLite updates process-wide counters under one global mutex at every
    /// allocation, so concurrent queries queue on it: with 64 concurrent admin requests writing, a store with them off handled
    /// 6-10% more requests per second, at a lower median latency (single requests and reads showed no difference). SQLite
    /// recommends this setting for speed; only the memory counters of sqlite3_status() and sqlite3_memory_used() stop working,
    /// and nopCommerce doesn't use them
    /// </remarks>
    public static void Apply()
    {
        if (Interlocked.Exchange(ref _applied, 1) == 1)
            return;

        //the native library has to be found first (nopCommerce deletes the .deps.json of plugins)
        SqliteNativeLibraryResolver.Register();
        Batteries_V2.Init();

        //SQLITE_MISUSE if SQLite was already initialized (another component used it first): it keeps its defaults then
        raw.sqlite3_config(SQLITE_CONFIG_MEMSTATUS, 0);
    }

    #endregion
}
