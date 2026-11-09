using Nop.Web.Infrastructure.Installation;

namespace Nop.Plugin.Data.Sqlite.Installation;

/// <summary>
/// Represents the localization service of the installation wizard, listing SQLite with the built-in data providers
/// </summary>
public class SqliteInstallationLocalizationService : IInstallationLocalizationService
{
    #region Fields

    protected readonly InstallationLocalizationService _installationLocalizationService;

    #endregion

    #region Ctor

    public SqliteInstallationLocalizationService(InstallationLocalizationService installationLocalizationService)
    {
        _installationLocalizationService = installationLocalizationService;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Get locale resource value
    /// </summary>
    /// <param name="resourceName">Resource name</param>
    /// <returns>Resource value</returns>
    public virtual string GetResource(string resourceName)
    {
        return _installationLocalizationService.GetResource(resourceName);
    }

    /// <summary>
    /// Get the browser culture
    /// </summary>
    /// <returns>Culture</returns>
    public virtual string GetBrowserCulture()
    {
        return _installationLocalizationService.GetBrowserCulture();
    }

    /// <summary>
    /// Get current language for the installation page
    /// </summary>
    /// <returns>Current language</returns>
    public virtual InstallationLanguage GetCurrentLanguage()
    {
        return _installationLocalizationService.GetCurrentLanguage();
    }

    /// <summary>
    /// Save a language for the installation page
    /// </summary>
    /// <param name="languageCode">Language code</param>
    public virtual void SaveCurrentLanguage(string languageCode)
    {
        _installationLocalizationService.SaveCurrentLanguage(languageCode);
    }

    /// <summary>
    /// Get a list of available languages
    /// </summary>
    /// <returns>Available installation languages</returns>
    public virtual IList<InstallationLanguage> GetAvailableLanguages()
    {
        return _installationLocalizationService.GetAvailableLanguages();
    }

    /// <summary>
    /// Get a dictionary of available data provider types
    /// </summary>
    /// <param name="valuesToExclude">Values to exclude</param>
    /// <param name="useLocalization">Localize</param>
    /// <returns>Key-value pairs of available data providers types</returns>
    public virtual Dictionary<int, string> GetAvailableProviderTypes(int[] valuesToExclude = null, bool useLocalization = true)
    {
        var providerTypes = _installationLocalizationService.GetAvailableProviderTypes(valuesToExclude, useLocalization);

        if (valuesToExclude?.Contains(SqliteDataProviderDefaults.DataProviderTypeId) != true)
            providerTypes[SqliteDataProviderDefaults.DataProviderTypeId] = SqliteDataProviderDefaults.DataProviderName;

        return providerTypes;
    }

    #endregion
}
