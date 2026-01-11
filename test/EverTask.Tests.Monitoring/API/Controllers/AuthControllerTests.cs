using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using EverTask.Monitor.Api.DTOs.Auth;
using EverTask.Tests.Monitoring.TestHelpers;

namespace EverTask.Tests.Monitoring.API.Controllers;

public class AuthControllerTests : MonitoringTestBase
{
    protected override bool RequireAuthentication => true; // Enable authentication for auth tests

    [Fact]
    public async Task Should_return_token_on_valid_login()
    {
        // Arrange
        var loginRequest = new LoginRequest("testuser", "testpass");

        // Act
        var response = await Client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var loginResponse = await DeserializeResponseAsync<LoginResponse>(response);
        loginResponse.ShouldNotBeNull();
        loginResponse.Token.ShouldNotBeNullOrEmpty();
        loginResponse.Username.ShouldBe("testuser");
        loginResponse.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Should_return_401_on_invalid_username()
    {
        // Arrange
        var loginRequest = new LoginRequest("wronguser", "testpass");

        // Act
        var response = await Client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_401_on_invalid_password()
    {
        // Arrange
        var loginRequest = new LoginRequest("testuser", "wrongpass");

        // Act
        var response = await Client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_400_on_invalid_request_model()
    {
        // Arrange - Send empty/invalid request
        var invalidRequest = new { username = "", password = "" };

        // Act
        var response = await Client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", invalidRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Should_validate_valid_token()
    {
        // Arrange - First login to get a valid token
        var loginRequest = new LoginRequest("testuser", "testpass");
        var loginResponse = await Client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);
        loginResponse.EnsureSuccessStatusCode();

        var loginResult = await DeserializeResponseAsync<LoginResponse>(loginResponse);
        var token = loginResult!.Token;

        // Act - Validate the token via POST body
        var validateRequest = new TokenValidationRequest(token);
        var validateResponse = await Client.PostAsJsonAsync("/evertask-monitoring/api/auth/validate", validateRequest);

        // Assert
        validateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var validationResult = await DeserializeResponseAsync<TokenValidationResponse>(validateResponse);
        validationResult.ShouldNotBeNull();
        validationResult.IsValid.ShouldBeTrue();
        validationResult.Username.ShouldBe("testuser");
        validationResult.ExpiresAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_validate_token_from_authorization_header()
    {
        // Arrange - First login to get a valid token
        var loginRequest = new LoginRequest("testuser", "testpass");
        var loginResponse = await Client.PostAsJsonAsync("/evertask-monitoring/api/auth/login", loginRequest);
        loginResponse.EnsureSuccessStatusCode();

        var loginResult = await DeserializeResponseAsync<LoginResponse>(loginResponse);
        var token = loginResult!.Token;

        // Create a new request with Authorization header and empty body
        var request = new HttpRequestMessage(HttpMethod.Post, "/evertask-monitoring/api/auth/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new TokenValidationRequest(null)); // Empty token in body, will use Authorization header

        // Act
        var validateResponse = await Client.SendAsync(request);

        // Assert
        validateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var validationResult = await DeserializeResponseAsync<TokenValidationResponse>(validateResponse);
        validationResult.ShouldNotBeNull();
        validationResult.IsValid.ShouldBeTrue();
        validationResult.Username.ShouldBe("testuser");
        validationResult.ExpiresAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_return_validation_error_for_invalid_token()
    {
        // Arrange - Use a malformed token
        var invalidToken = "this.is.not.a.valid.jwt.token";
        var validateRequest = new TokenValidationRequest(invalidToken);

        // Act
        var validateResponse = await Client.PostAsJsonAsync("/evertask-monitoring/api/auth/validate", validateRequest);

        // Assert
        validateResponse.StatusCode.ShouldBe(HttpStatusCode.OK); // Endpoint returns 200 with IsValid=false

        var validationResult = await DeserializeResponseAsync<TokenValidationResponse>(validateResponse);
        validationResult.ShouldNotBeNull();
        validationResult.IsValid.ShouldBeFalse();
        validationResult.Username.ShouldBeNull();
    }

    [Fact]
    public async Task Should_return_404_when_magic_link_not_configured()
    {
        // Act - Try magic link without it being configured
        var response = await Client.GetAsync("/evertask-monitoring/api/auth/magic?token=anytoken");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// Tests for magic link authentication
/// </summary>
public class MagicLinkAuthControllerTests : MonitoringTestBase
{
    private const string TestMagicToken = "test-magic-link-token-abc123";

    protected override bool RequireAuthentication => true;
    protected override Action<Monitor.Api.Options.EverTaskApiOptions>? ConfigureOptions =>
        options => options.MagicLinkToken = TestMagicToken;

    [Fact]
    public async Task Should_return_token_on_valid_magic_link()
    {
        // Act
        var response = await Client.GetAsync($"/evertask-monitoring/api/auth/magic?token={TestMagicToken}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var loginResponse = await DeserializeResponseAsync<LoginResponse>(response);
        loginResponse.ShouldNotBeNull();
        loginResponse.Token.ShouldNotBeNullOrEmpty();
        loginResponse.Username.ShouldBe("testuser");
        loginResponse.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Should_return_401_on_invalid_magic_link_token()
    {
        // Act
        var response = await Client.GetAsync("/evertask-monitoring/api/auth/magic?token=wrong-token");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_401_on_missing_magic_link_token()
    {
        // Act
        var response = await Client.GetAsync("/evertask-monitoring/api/auth/magic");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_401_on_empty_magic_link_token()
    {
        // Act
        var response = await Client.GetAsync("/evertask-monitoring/api/auth/magic?token=");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_allow_api_access_with_jwt_from_magic_link()
    {
        // Arrange - Get JWT via magic link
        var magicResponse = await Client.GetAsync($"/evertask-monitoring/api/auth/magic?token={TestMagicToken}");
        magicResponse.EnsureSuccessStatusCode();

        var loginResult = await DeserializeResponseAsync<LoginResponse>(magicResponse);
        var jwtToken = loginResult!.Token;

        // Act - Use JWT to access protected API
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
        var apiResponse = await Client.GetAsync("/evertask-monitoring/api/dashboard/overview");

        // Assert
        apiResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
