using System.Runtime.InteropServices;
using SQLitePCL;

namespace Nop.Plugin.Data.Sqlite;

/// <summary>
/// Makes the native SQLite library shipped with the plugin loadable
/// </summary>
/// <remarks>
/// The native library is in the plugin's "runtimes/{rid}/native" folders. The host only probes the native assets of its own
/// dependencies, and nopCommerce deletes the plugin's .deps.json when it deploys the plugin, so the library is located here
/// </remarks>
public static class SqliteNativeLibraryResolver
{
    #region Fields

    private static int _registered;

    #endregion

    #region Utilities

    /// <summary>
    /// Gets the runtime identifiers to probe, from the most to the least specific
    /// </summary>
    /// <returns>Runtime identifiers</returns>
    private static IEnumerable<string> GetRuntimeIdentifiers()
    {
        //e.g. "win-x64", "linux-musl-arm64", "osx-arm64" (portable identifiers since .NET 8)
        yield return RuntimeInformation.RuntimeIdentifier;

        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        if (OperatingSystem.IsWindows())
            yield return $"win-{architecture}";
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            yield return $"osx-{architecture}";
            yield return "osx";
        }
        else
        {
            yield return $"linux-{architecture}";
            yield return $"linux-musl-{architecture}";
        }
    }

    /// <summary>
    /// Gets the file name of a native library on the current platform
    /// </summary>
    /// <param name="libraryName">Library name (e.g. "e_sqlite3")</param>
    /// <returns>File name</returns>
    private static string GetLibraryFileName(string libraryName)
    {
        if (OperatingSystem.IsWindows())
            return $"{libraryName}.dll";

        return OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ? $"lib{libraryName}.dylib" : $"lib{libraryName}.so";
    }

    /// <summary>
    /// Loads a native library from the plugin directory
    /// </summary>
    /// <param name="libraryName">Library name</param>
    /// <returns>Library handle; IntPtr.Zero to fall back to the default probing</returns>
    private static IntPtr Resolve(string libraryName)
    {
        var pluginDirectory = Path.GetDirectoryName(typeof(SqliteNativeLibraryResolver).Assembly.Location);
        if (string.IsNullOrEmpty(pluginDirectory))
            return IntPtr.Zero;

        var fileName = GetLibraryFileName(libraryName);

        foreach (var runtimeIdentifier in GetRuntimeIdentifiers().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(pluginDirectory, "runtimes", runtimeIdentifier, "native", fileName);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }

        //e.g. a self-contained publish flattens the native assets into the application directory
        var flatPath = Path.Combine(pluginDirectory, fileName);

        return File.Exists(flatPath) && NativeLibrary.TryLoad(flatPath, out var flatHandle) ? flatHandle : IntPtr.Zero;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Registers the resolver; must be called before the first SQLite connection is created
    /// </summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SQLite3Provider_e_sqlite3).Assembly,
                (libraryName, _, _) => Resolve(libraryName));
        }
        catch (InvalidOperationException)
        {
            //a resolver is already set for the provider assembly
        }
    }

    #endregion
}
