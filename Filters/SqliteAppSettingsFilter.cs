using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Rendering;
using Nop.Web.Areas.Admin.Controllers;
using Nop.Web.Areas.Admin.Models.Settings;

namespace Nop.Plugin.Data.Sqlite.Filters;

/// <summary>
/// Represents the filter keeping SQLite on the "App settings" page (Admin > Configuration) of a store running on SQLite
/// </summary>
/// <remarks>
/// The page lists the built-in data providers only, so saving it would switch the store to the first of them
/// </remarks>
public class SqliteAppSettingsFilter : IAsyncActionFilter, IAsyncResultFilter
{
    /// <summary>
    /// Called asynchronously before the action, after model binding is complete
    /// </summary>
    /// <param name="context">A context for action filters</param>
    /// <param name="next">A delegate invoked to execute the next action filter or the action itself</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public virtual async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        //a store can't be moved to another database by changing this setting anyway
        if (context.Controller is SettingController
            && context.ActionArguments.Values.OfType<AppSettingsModel>().FirstOrDefault()?.DataConfigModel is { } dataConfigModel)
        {
            dataConfigModel.DataProvider = SqliteDataProviderDefaults.DataProviderTypeId;
        }

        await next();
    }

    /// <summary>
    /// Called asynchronously before the action result
    /// </summary>
    /// <param name="context">A context for result filters</param>
    /// <param name="next">A delegate invoked to execute the next result filter or the result itself</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public virtual async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        //show SQLite as the data provider
        if (context.Controller is SettingController
            && context.Result is ViewResult { Model: AppSettingsModel { DataConfigModel: { } dataConfigModel } })
        {
            var value = SqliteDataProviderDefaults.DataProviderTypeId.ToString();
            var items = (dataConfigModel.DataProviderTypeValues ?? Enumerable.Empty<SelectListItem>())
                .Where(item => item.Value != value)
                .Append(new SelectListItem(SqliteDataProviderDefaults.DataProviderName, value))
                .ToList();

            dataConfigModel.DataProvider = SqliteDataProviderDefaults.DataProviderTypeId;
            dataConfigModel.DataProviderTypeValues = new SelectList(items, nameof(SelectListItem.Value), nameof(SelectListItem.Text), value);
        }

        await next();
    }
}
