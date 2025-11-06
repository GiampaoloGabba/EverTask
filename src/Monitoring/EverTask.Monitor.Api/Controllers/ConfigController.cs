using EverTask.Monitor.Api.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EverTask.Monitor.Api.Controllers;

/// <summary>
/// Provides runtime configuration for frontend clients.
/// </summary>
[ApiController]
[Route("api/config")]
public class ConfigController : ControllerBase
{
    private readonly EverTaskApiOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigController"/> class.
    /// </summary>
    public ConfigController(EverTaskApiOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Get monitoring API configuration.
    /// This endpoint must be accessible without authentication.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetConfig()
    {
        return Ok(new
        {
            apiBasePath           = _options.ApiBasePath,
            uiBasePath            = _options.UIBasePath,
            signalRHubPath        = _options.SignalRHubPath,
            requireAuthentication = _options.EnableAuthentication,
            uiEnabled             = _options.EnableUI
        });
    }
}
