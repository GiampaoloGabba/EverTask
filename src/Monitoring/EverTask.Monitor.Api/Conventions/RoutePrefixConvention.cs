using EverTask.Monitor.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace EverTask.Monitor.Api.Conventions;

/// <summary>
/// Application model convention that scopes the EverTask monitoring controllers: adds the
/// monitoring route prefix, assigns the ApiExplorer group name and attaches the monitoring
/// JSON contract, so the controllers stay isolated from the host application's routes, OpenAPI
/// documents and MVC JsonOptions. Controllers from other assemblies are left untouched.
/// </summary>
public class RoutePrefixConvention(string prefix, string apiExplorerGroupName) : IApplicationModelConvention
{
    private static readonly MonitoringJsonResultFilter JsonFilter = new();

    /// <summary>
    /// Resolved per request from DI, because the gate needs the options and the token service.
    /// </summary>
    private static readonly ServiceFilterAttribute ManagementGate = new(typeof(ManagementAuthorizationFilter));

    /// <summary>
    /// The IP whitelist and the JWT check, inside routing: the middleware that also carries them cannot see
    /// the path a host's <c>UsePathBase</c> produced, and under one every read answered anonymously.
    /// </summary>
    private static readonly ServiceFilterAttribute AccessGate =
        new(typeof(MonitoringAccessFilter)) { Order = MonitoringAccessFilter.FilterOrder };

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

            // Monitoring JSON contract without touching the host's shared MVC JsonOptions
            controller.Filters.Add(JsonFilter);
            controller.Filters.Add(AccessGate);

            foreach (var selector in controller.Selectors)
            {
                if (selector.AttributeRouteModel != null)
                {
                    selector.AttributeRouteModel = AttributeRouteModel.CombineAttributeRouteModel(
                        new AttributeRouteModel(new RouteAttribute(_prefix)),
                        selector.AttributeRouteModel);
                }
            }

            // Keyed on the ROUTE and not on the controller type: the write surface is a prefix, so a
            // controller added under it inherits the gate instead of having to remember an attribute.
            if (IsRoutedUnderManagement(controller))
                controller.Filters.Add(ManagementGate);
        }
    }

    private bool IsRoutedUnderManagement(ControllerModel controller)
    {
        var managementPrefix = $"{_prefix}/api/management";

        return controller.Selectors.Any(selector =>
            selector.AttributeRouteModel?.Template?.StartsWith(managementPrefix, StringComparison.OrdinalIgnoreCase)
            == true);
    }
}
