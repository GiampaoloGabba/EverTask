using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Routing;

namespace EverTask.Monitor.Api.Conventions;

/// <summary>
/// Application model convention that adds a route prefix to all controllers.
/// </summary>
public class RoutePrefixConvention : IApplicationModelConvention
{
    private readonly string _prefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutePrefixConvention"/> class.
    /// </summary>
    /// <param name="prefix">The route prefix to add to all controllers.</param>
    public RoutePrefixConvention(string prefix)
    {
        _prefix = prefix.Trim('/');
    }

    /// <summary>
    /// Applies the convention to the application model.
    /// </summary>
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            // Add prefix to all controller routes
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
