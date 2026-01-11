namespace EverTask.Tests.Monitoring.TestHelpers;

/// <summary>
/// Base class for monitoring integration tests
/// </summary>
public abstract class MonitoringTestBase : IAsyncLifetime
{
    protected MonitoringTestWebAppFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;
    protected ITaskStorage Storage { get; private set; } = null!;

    protected virtual bool RequireAuthentication => false;
    protected virtual bool EnableWorker => false;
    protected virtual Action<Monitor.Api.Options.EverTaskApiOptions>? ConfigureOptions => null;

    public virtual async Task InitializeAsync()
    {
        Factory = new MonitoringTestWebAppFactory(RequireAuthentication, EnableWorker, configureOptions: ConfigureOptions);
        Client = Factory.CreateClient();
        Storage = Factory.Services.GetRequiredService<ITaskStorage>();
        await Task.CompletedTask;
    }

    public virtual async Task DisposeAsync()
    {
        Client?.Dispose();
        await Factory.DisposeAsync();
    }

    /// <summary>
    /// Add Basic Auth header to request
    /// </summary>
    protected void AddBasicAuthHeader(string username = "testuser", string password = "testpass")
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    /// <summary>
    /// Create a SignalR test client
    /// </summary>
    protected SignalRTestClient CreateSignalRClient(string? hubPath = null)
    {
        var baseUrl = Client.BaseAddress!.ToString().TrimEnd('/');
        var path = hubPath ?? "/evertask-monitoring/hub";
        var url = $"{baseUrl}{path}";

        return new SignalRTestClient(url, builder =>
        {
            builder.WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
            });
        });
    }

    /// <summary>
    /// Deserialize JSON response
    /// </summary>
    protected async Task<T?> DeserializeResponseAsync<T>(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        });
    }
}
