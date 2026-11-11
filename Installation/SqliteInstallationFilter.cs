using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Nop.Core;
using Nop.Core.Caching;
using Nop.Core.Configuration;
using Nop.Core.Http;
using Nop.Core.Infrastructure;
using Nop.Data;
using Nop.Data.Configuration;
using Nop.Services.Common;
using Nop.Services.Installation;
using Nop.Services.Plugins;
using Nop.Web.Controllers;
using Nop.Web.Framework.Security;
using Nop.Web.Infrastructure.Installation;
using Nop.Web.Models.Install;

namespace Nop.Plugin.Data.Sqlite.Installation;

/// <summary>
/// Represents the filter installing nopCommerce on SQLite when it's selected in the installation wizard
/// </summary>
/// <remarks>
/// InstallController.Index creates the data provider with DataProviderManager.GetDataProvider, which only knows the built-in
/// data providers, so for SQLite the filter runs the installation instead of the action. Apart from the data provider it does
/// what InstallController.Index of nopCommerce 5.00 does: keep them in line when upgrading nopCommerce.
/// </remarks>
public class SqliteInstallationFilter : IAsyncActionFilter
{
    #region Fields

    //the wizard polls InstallController.Progress for the step in progress, which the controller keeps in a private field
    //set by its protected SetProgressMessage method (null if a version doesn't have them: the wizard then shows no steps)
    private static readonly MethodInfo _setProgressMessage =
        typeof(InstallController).GetMethod("SetProgressMessage", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)]);
    private static readonly FieldInfo _installProgressInfoModel =
        typeof(InstallController).GetField("_installProgressInfoModel", BindingFlags.Static | BindingFlags.NonPublic);

    protected readonly AppSettings _appSettings;
    protected readonly IInstallationLocalizationService _locService;
    protected readonly INopFileProvider _fileProvider;

    #endregion

    #region Ctor

    public SqliteInstallationFilter(AppSettings appSettings,
        IInstallationLocalizationService locService,
        INopFileProvider fileProvider)
    {
        _appSettings = appSettings;
        _locService = locService;
        _fileProvider = fileProvider;
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Shows the step in progress in the installation wizard, as InstallController.Index does
    /// </summary>
    /// <param name="controller">Installation controller</param>
    /// <param name="resourceName">Name of the installation resource describing the step</param>
    protected virtual void SetProgressMessage(InstallController controller, string resourceName)
    {
        _setProgressMessage?.Invoke(controller, [_locService.GetResource(resourceName)]);
    }

    /// <summary>
    /// Clears the step in progress once the installation is over, as InstallController.Index does
    /// </summary>
    protected virtual void ClearProgressMessage()
    {
        _installProgressInfoModel?.SetValue(null, null);
    }

    /// <summary>
    /// Check whether the request posts the installation form with SQLite selected
    /// </summary>
    /// <param name="context">Action context</param>
    /// <returns>True if SQLite is being installed; otherwise false</returns>
    protected virtual bool IsSqliteInstallation(ActionExecutingContext context)
    {
        var request = context.HttpContext.Request;

        return context.Controller is InstallController
            && context.ActionDescriptor is ControllerActionDescriptor { ActionName: nameof(InstallController.Index) }
            && HttpMethods.IsPost(request.Method)
            && request.HasFormContentType
            && request.Form[nameof(InstallModel.DataProvider)] == SqliteDataProviderDefaults.DataProviderTypeId.ToString();
    }

    /// <summary>
    /// Check whether the application may write to the directories and files it needs (the action reports the failures)
    /// </summary>
    /// <returns>True if the permissions are fine; otherwise false</returns>
    protected virtual bool CheckPermissions()
    {
        return _fileProvider.GetDirectoriesWrite().All(directory => _fileProvider.CheckPermissions(directory, false, true, true, false))
            && _fileProvider.GetFilesWrite().Where(_fileProvider.FileExists).All(file => _fileProvider.CheckPermissions(file, false, true, true, true));
    }

    /// <summary>
    /// Install nopCommerce on SQLite
    /// </summary>
    /// <param name="controller">Installation controller</param>
    /// <param name="model">Installation model</param>
    /// <param name="services">Request services</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    protected virtual async Task InstallAsync(InstallController controller, InstallModel model, IServiceProvider services)
    {
        var dataProvider = new SqliteNopDataProvider();

        var connectionString = model.ConnectionStringRaw ? model.ConnectionString : dataProvider.BuildConnectionString(model);
        if (string.IsNullOrEmpty(connectionString))
            throw new Exception(_locService.GetResource("ConnectionStringWrongFormat"));

        DataSettingsManager.SaveSettings(new DataConfig
        {
            DataProvider = SqliteDataSettings.ProviderType,
            ConnectionString = connectionString,
            Collation = model.Collation,
            CharacterSet = model.CharacterSet
        }, _fileProvider);

        if (model.CreateDatabaseIfNotExists && !await dataProvider.DatabaseExistsAsync())
        {
            try
            {
                SetProgressMessage(controller, "Progress.CreateDatabase");
                dataProvider.CreateDatabase();
            }
            catch (Exception exception)
            {
                throw new Exception(string.Format(_locService.GetResource("DatabaseCreationError"), exception.Message));
            }
        }
        else if (!await dataProvider.DatabaseExistsAsync())
            throw new Exception(_locService.GetResource("DatabaseNotExists"));

        SetProgressMessage(controller, "Progress.InitializeDatabase");
        dataProvider.InitializeDatabase();

        //from here on the same as InstallController.Index
        var nopHttpClient = services.GetRequiredService<NopHttpClient>();

        var cultureInfo = new CultureInfo(NopCommonDefaults.DefaultLanguageCulture);
        var regionInfo = new RegionInfo(NopCommonDefaults.DefaultLanguageCulture);

        var languagePackInfo = (DownloadUrl: string.Empty, Progress: 0);
        if (model.InstallRegionalResources)
        {
            SetProgressMessage(controller, "Progress.RegionalResources");

            //try to get CultureInfo and RegionInfo
            if (model.Country != null)
            {
                try
                {
                    cultureInfo = new CultureInfo(model.Country[3..]);
                    regionInfo = new RegionInfo(model.Country[3..]);
                }
                catch
                {
                    // ignored
                }
            }

            //get URL to download language pack
            if (cultureInfo.Name != NopCommonDefaults.DefaultLanguageCulture)
            {
                try
                {
                    var languageCode = _locService.GetCurrentLanguage().Code[0..2];
                    var resultString = await nopHttpClient.InstallationCompletedAsync(model.AdminEmail, languageCode, cultureInfo.Name);
                    var result = JsonConvert.DeserializeAnonymousType(resultString,
                        new { Message = string.Empty, LanguagePack = new { Culture = string.Empty, Progress = 0, DownloadLink = string.Empty } });

                    if (result != null && result.LanguagePack.Progress > NopCommonDefaults.LanguagePackMinTranslationProgressToInstall)
                    {
                        languagePackInfo.DownloadUrl = result.LanguagePack.DownloadLink;
                        languagePackInfo.Progress = result.LanguagePack.Progress;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            //upload CLDR
            await services.GetRequiredService<IUploadService>().UploadLocalePatternAsync(cultureInfo);
        }

        if (model.SubscribeNewsletters)
        {
            try
            {
                await nopHttpClient.SubscribeNewsLettersAsync(model.AdminEmail);
            }
            catch
            {
                // ignored
            }
        }

        SetProgressMessage(controller, "Progress.InsertData");
        //now resolve installation service and install nopCommerce
        await services.GetRequiredService<IInstallationService>().InstallAsync(new InstallationSettings
        {
            AdminEmail = model.AdminEmail,
            AdminPassword = model.AdminPassword,
            LanguagePackDownloadLink = languagePackInfo.DownloadUrl,
            LanguagePackProgress = languagePackInfo.Progress,
            RegionInfo = regionInfo,
            CultureInfo = cultureInfo,
            InstallSampleData = model.InstallSampleData
        });

        //prepare plugins to install
        var pluginService = services.GetRequiredService<IPluginService>();
        pluginService.ClearInstalledPluginsList();

        var pluginsIgnoredDuringInstallation = new List<string>();
        if (!string.IsNullOrEmpty(_appSettings.Get<InstallationConfig>().DisabledPlugins))
        {
            pluginsIgnoredDuringInstallation = _appSettings.Get<InstallationConfig>().DisabledPlugins
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(pluginName => pluginName.Trim()).ToList();
        }

        var plugins = (await pluginService.GetPluginDescriptorsAsync<IPlugin>(LoadPluginsMode.All))
            .Where(pluginDescriptor => !pluginsIgnoredDuringInstallation.Contains(pluginDescriptor.SystemName))
            .OrderBy(pluginDescriptor => pluginDescriptor.Group).ThenBy(pluginDescriptor => pluginDescriptor.DisplayOrder)
            .ToList();

        SetProgressMessage(controller, "Progress.PreparePlugins");
        foreach (var plugin in plugins)
            await pluginService.PreparePluginToInstallAsync(plugin.SystemName, checkDependencies: false);
    }

    #endregion

    #region Methods

    /// <summary>
    /// Called asynchronously before the action, after model binding is complete
    /// </summary>
    /// <param name="context">A context for action filters</param>
    /// <param name="next">A delegate invoked to execute the next action filter or the action itself</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public virtual async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!IsSqliteInstallation(context)
            || context.Controller is not InstallController controller
            || context.ActionArguments.Values.OfType<InstallModel>().FirstOrDefault() is not { } model)
        {
            await next();
            return;
        }

        //the posted value isn't a DataProviderType name, and the server fields don't apply to a database file
        foreach (var key in new[] { nameof(InstallModel.DataProvider), nameof(InstallModel.ServerName), nameof(InstallModel.Username), nameof(InstallModel.Password) })
            context.ModelState.Remove(key);

        model.DataProvider = SqliteDataSettings.ProviderType;
        model.InstallRegionalResources = _appSettings.Get<InstallationConfig>().InstallRegionalResources;

        //the action redirects to the home page or shows the errors
        SetProgressMessage(controller, "Progress.CheckPermissions");
        if (DataSettingsManager.IsDatabaseInstalled() || !context.ModelState.IsValid || !CheckPermissions())
        {
            await next();
            return;
        }

        try
        {
            await InstallAsync(controller, model, context.HttpContext.RequestServices);

            context.Result = controller.View(new InstallModel { RestartUrl = controller.Url.RouteUrl(NopRouteNames.General.HOMEPAGE) });
        }
        catch (Exception exception)
        {
            await context.HttpContext.RequestServices.GetRequiredService<IStaticCacheManager>().ClearAsync();

            //clear provider settings if something got wrong
            DataSettingsManager.SaveSettings(new DataConfig(), _fileProvider);

            //the action shows the error
            context.ModelState.AddModelError(string.Empty, string.Format(_locService.GetResource("SetupFailed"), exception.Message));
            await next();
        }
        finally
        {
            ClearProgressMessage();
        }
    }

    #endregion
}
