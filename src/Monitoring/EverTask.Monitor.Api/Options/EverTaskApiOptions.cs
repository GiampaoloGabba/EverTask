namespace EverTask.Monitor.Api.Options;

/// <summary>
/// Configuration options for the EverTask Monitoring API.
/// This API can be used standalone for custom integrations or with the embedded dashboard UI.
/// </summary>
public class EverTaskApiOptions
{
    /// <summary>
    /// Base path for API and UI (fixed: "/monitoring")
    /// API is always accessible at: /monitoring/api/*
    /// When EnableUI is true:
    ///   - UI is accessible at: /monitoring/*
    /// When EnableUI is false:
    ///   - UI is disabled, only API is available
    /// </summary>
    public string BasePath => "/monitoring";

    /// <summary>
    /// Enable embedded dashboard UI (default: true)
    /// Set to false to use API-only mode for custom integrations
    /// </summary>
    public bool EnableUI { get; set; } = true;

    /// <summary>
    /// Enable Swagger/OpenAPI documentation for monitoring API (default: false)
    /// Creates a separate Swagger document at /swagger/evertask-monitoring/swagger.json
    /// The document is automatically filtered to include only EverTask monitoring endpoints
    /// </summary>
    public bool EnableSwagger { get; set; } = false;

    /// <summary>
    /// API base path (derived from BasePath)
    /// API is always accessible at /monitoring/api regardless of EnableUI setting
    /// </summary>
    public string ApiBasePath => $"{BasePath}/api";

    /// <summary>
    /// UI base path (only used when EnableUI is true)
    /// </summary>
    public string UIBasePath => BasePath;

    /// <summary>
    /// Username for Basic Authentication (default: "admin")
    /// </summary>
    public string Username { get; set; } = "admin";

    /// <summary>
    /// Password for Basic Authentication (default: "admin")
    /// WARNING: Change this in production!
    /// </summary>
    public string Password { get; set; } = "admin";

    /// <summary>
    /// SignalR hub path for real-time monitoring (fixed: "/monitoring/hub")
    /// Must match the path configured when calling MapEverTaskMonitorHub()
    /// </summary>
    public string SignalRHubPath => "/monitoring/hub";

    /// <summary>
    /// Enable Basic Authentication (default: true)
    /// Set to false for development environments
    /// </summary>
    public bool RequireAuthentication { get; set; } = true;

    /// <summary>
    /// Allow anonymous access to read-only endpoints (default: false)
    /// When true, authentication is still required for write operations (future)
    /// </summary>
    public bool AllowAnonymousReadAccess { get; set; } = false;

    /// <summary>
    /// Enable CORS for monitoring API (default: true)
    /// Useful when frontend is served separately or for custom integrations
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// CORS allowed origins (default: allow all)
    /// Only used if EnableCors is true
    /// </summary>
    public string[] CorsAllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// IP address whitelist for monitoring access (default: empty = allow all IPs)
    /// When configured, only requests from these IPs will be allowed
    /// Supports IPv4 and IPv6 addresses
    /// Example: new[] { "192.168.1.100", "10.0.0.0/8", "::1" }
    /// </summary>
    public string[] AllowedIpAddresses { get; set; } = Array.Empty<string>();
}
