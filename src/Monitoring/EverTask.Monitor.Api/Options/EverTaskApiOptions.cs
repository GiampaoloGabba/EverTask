namespace EverTask.Monitor.Api.Options;

/// <summary>
/// Configuration options for the EverTask Monitoring API.
/// This API can be used standalone for custom integrations or with the embedded dashboard UI.
/// </summary>
public class EverTaskApiOptions
{
    /// <summary>
    /// Name of the CORS policy built when <see cref="EnableCors"/> is true. Applied only to
    /// requests under <see cref="BasePath"/>; the host's CORS setup is untouched.
    /// </summary>
    public const string CorsPolicyName = "EverTaskMonitoringApi";

    /// <summary>
    /// Name of the rate-limit policy registered for the login endpoint. Namespaced so it cannot
    /// collide with a host-defined policy; enforced only when the host pipeline calls
    /// <c>UseRateLimiter()</c>.
    /// </summary>
    public const string LoginRateLimitPolicyName = "evertask-monitoring-login";

    /// <summary>
    /// Base path for API and UI (fixed: "/evertask-monitoring")
    /// API is always accessible at: /evertask-monitoring/api/*
    /// When EnableUI is true:
    ///   - UI is accessible at: /evertask-monitoring/*
    /// When EnableUI is false:
    ///   - UI is disabled, only API is available
    /// </summary>
    public string BasePath => "/evertask-monitoring";

    /// <summary>
    /// Enable embedded dashboard UI (default: true)
    /// Set to false to use API-only mode for custom integrations
    /// </summary>
    public bool EnableUI { get; set; } = true;

    /// <summary>
    /// No longer used. The Swashbuckle integration was removed in 3.12; setting this has no effect.
    /// Use <see cref="EnableOpenApiDocument"/> (net9.0+) and optionally the EverTask.Monitor.Api.Scalar
    /// package for an interactive API reference.
    /// </summary>
    [Obsolete("The Swashbuckle integration was removed in 3.12 and this setting is a no-op. Use EnableOpenApiDocument (net9.0+) and optionally the EverTask.Monitor.Api.Scalar package.")]
    public bool EnableSwagger { get; set; } = false;

    /// <summary>
    /// Serve an OpenAPI document for the monitoring API (default: false)
    /// The document is generated with the built-in ASP.NET Core OpenAPI support (net9.0+; no-op on net8.0)
    /// and served at /evertask-monitoring/openapi/evertask-monitoring.json — fully isolated from any
    /// OpenAPI/Swagger/Scalar setup the host application has.
    /// Enabled automatically by the EverTask.Monitor.Api.Scalar package.
    /// </summary>
    public bool EnableOpenApiDocument { get; set; } = false;

    /// <summary>
    /// Name of the OpenAPI document and the ApiExplorer group of the monitoring controllers
    /// (fixed: "evertask-monitoring"). Because the controllers carry this group name, the host's own
    /// OpenAPI/Swagger documents exclude them by default.
    /// </summary>
    public string OpenApiDocumentName => "evertask-monitoring";

    /// <summary>
    /// Route of the monitoring OpenAPI document (derived: "/evertask-monitoring/openapi/evertask-monitoring.json")
    /// </summary>
    public string OpenApiDocumentPath => $"{BasePath}/openapi/{OpenApiDocumentName}.json";

    /// <summary>
    /// API base path (derived from BasePath)
    /// API is always accessible at /evertask-monitoring/api regardless of EnableUI setting
    /// </summary>
    public string ApiBasePath => $"{BasePath}/api";

    /// <summary>
    /// UI base path (only used when EnableUI is true)
    /// </summary>
    public string UIBasePath => BasePath;

    /// <summary>
    /// Username for JWT authentication (default: "admin")
    /// Used to validate login credentials via /api/auth/login endpoint
    /// </summary>
    public string Username { get; set; } = "admin";

    /// <summary>
    /// Password for JWT authentication (default: "admin")
    /// Used to validate login credentials via /api/auth/login endpoint
    /// WARNING: Change this in production!
    /// </summary>
    public string Password { get; set; } = "admin";

    /// <summary>
    /// SignalR hub path for real-time monitoring (fixed: "/evertask-monitoring/hub")
    /// Must match the path configured when calling MapEverTaskMonitorHub()
    /// </summary>
    public string SignalRHubPath => "/evertask-monitoring/hub";

    /// <summary>
    /// Enable JWT authentication for monitoring API (default: true)
    /// When true, clients must authenticate via /api/auth/login to obtain a JWT token
    /// When false, API is open without authentication (use only in development environments)
    /// Note: /api/config and /api/auth/* endpoints are always accessible without authentication
    /// JWT is the only supported authentication method
    /// </summary>
    public bool EnableAuthentication { get; set; } = true;

    /// <summary>
    /// Secret key for signing JWT tokens
    /// IMPORTANT: Use a strong, randomly generated secret in production (min 256 bits / 32 bytes)
    /// If not provided, a random secret will be generated (not recommended for multi-instance deployments)
    /// </summary>
    public string? JwtSecret { get; set; }

    /// <summary>
    /// JWT token issuer (default: "EverTask.Monitor.Api")
    /// Typically the name of your application or service
    /// </summary>
    public string JwtIssuer { get; set; } = "EverTask.Monitor.Api";

    /// <summary>
    /// JWT token audience (default: "EverTask.Monitor.Api")
    /// Typically the name of your application or the expected consumers
    /// </summary>
    public string JwtAudience { get; set; } = "EverTask.Monitor.Api";

    /// <summary>
    /// JWT token expiration time in hours (default: 8 hours)
    /// Tokens will automatically expire after this duration
    /// </summary>
    public int JwtExpirationHours { get; set; } = 8;

    /// <summary>
    /// Enable CORS for monitoring API (default: true)
    /// Useful when frontend is served separately or for custom integrations
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// CORS allowed origins (default: allow all)
    /// Only used if EnableCors is true
    /// </summary>
    public string[] CorsAllowedOrigins { get; set; } = [];

    /// <summary>
    /// IP address whitelist for monitoring access (default: empty = allow all IPs)
    /// When configured, only requests from these IPs will be allowed
    /// Supports IPv4 and IPv6 addresses
    /// Example: new[] { "192.168.1.100", "10.0.0.0/8", "::1" }
    /// </summary>
    public string[] AllowedIpAddresses { get; set; } = [];

    /// <summary>
    /// Static token for magic link authentication. If set, enables magic link access via /api/auth/magic endpoint.
    /// Use a long random string (32+ characters recommended) for security.
    /// Example: "your-very-long-secret-token-abc123xyz"
    /// When configured, users can access the dashboard via: /evertask-monitoring/magic?token=YOUR_TOKEN
    /// </summary>
    public string? MagicLinkToken { get; set; }

    /// <summary>
    /// Debounce time in milliseconds for SignalR event-driven cache invalidation in the frontend dashboard.
    /// When multiple task events occur in rapid succession, the dashboard will wait this duration
    /// before refreshing data to prevent excessive API calls during task bursts.
    /// Default: 1000 (1 second).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This setting controls client-side behavior when using SignalR-based auto-refresh in the embedded dashboard.
    /// Higher values reduce API load during task bursts but introduce slight UI update delays.
    /// Lower values provide more responsive UI updates but may increase network traffic.
    /// </para>
    /// <para>
    /// Recommended values:
    /// - 300ms: Very responsive, suitable for low-volume environments
    /// - 500ms: Balanced (moderate responsiveness, good efficiency)
    /// - 1000ms: Conservative (default), best for high-volume task processing
    /// </para>
    /// </remarks>
    public int EventDebounceMs { get; set; } = 1000;
}
