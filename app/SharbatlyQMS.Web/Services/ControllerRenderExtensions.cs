using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Lets controllers render a Razor partial to a string -- used by the AJAX
/// endpoints that need to return server-rendered HTML fragments (sample
/// rows, drawer bodies) inside a JSON payload. Mirrors the long-standing
/// MVC `PartialView` flow but writes to a StringWriter.
/// </summary>
public static class ControllerRenderExtensions
{
    public static async Task<string> RenderPartialToStringAsync(this Controller controller, string viewName, object model)
    {
        var sp = controller.HttpContext.RequestServices;
        var engine = sp.GetRequiredService<ICompositeViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var viewResult = engine.FindView(controller.ControllerContext, viewName, isMainPage: false);
        if (!viewResult.Success)
            viewResult = engine.GetView(executingFilePath: null, viewPath: viewName, isMainPage: false);
        if (!viewResult.Success || viewResult.View == null)
            throw new InvalidOperationException($"View '{viewName}' not found. Searched: {string.Join(", ", viewResult.SearchedLocations)}");

        await using var sw = new StringWriter();
        var viewData = new ViewDataDictionary(controller.ViewData) { Model = model };
        var tempData = new TempDataDictionary(controller.HttpContext, tempDataProvider);
        var viewContext = new ViewContext(controller.ControllerContext, viewResult.View, viewData, tempData, sw,
            new HtmlHelperOptions());
        await viewResult.View.RenderAsync(viewContext);
        return sw.ToString();
    }
}
