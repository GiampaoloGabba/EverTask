using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace EverTask.Monitor.Api.Conventions;

/// <summary>
/// Application model convention that scopes the EverTask monitoring controllers:
/// adds the monitoring route prefix and assigns the ApiExplorer group name, so the
/// controllers stay isolated from the host application's routes and OpenAPI documents.
/// Controllers from other assemblies are left untouched.
/// </summary>
public class RoutePrefixConvention(string prefix, string apiExplorerGroupName) : IApplicationModelConvention
{
    private readonly string _prefix = prefix.Trim('/');

    /// <summary>
    /// Applies the convention to the application model.
    /// </summary>
    public void Apply(ApplicationModel application)
    {
        var monitoringAssembly = typeof(RoutePrefixConvention).Assembly;

        foreach (var controller in application.Controllers)
        {
            // Only touch this package's controllers: host controllers keep their natural
            // routes and stay in the host's own OpenAPI documents
            if (controller.ControllerType.Assembly != monitoringAssembly)
                continue;

            // The group name keeps these controllers out of the host's OpenAPI/Swagger
            // documents (default inclusion filters match on group name) and inside ours
            controller.ApiExplorer.GroupName = apiExplorerGroupName;

            foreach (var selector in controller.Selectors)
            {
                if (selector.AttributeRouteModel != null)
                {
                    selector.AttributeRouteModel = AttributeRouteModel.CombineAttributeRouteModel(
                        new AttributeRouteModel(new RouteAttribute(_prefix)),
                        selector.AttributeRouteModel);
                }
            }
        }
    }
}
