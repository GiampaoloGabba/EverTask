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
        var response = await Client.PostAsJsonAsync("/monitoring/api/auth/login", loginRequest);

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
        var response = await Client.PostAsJsonAsync("/monitoring/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_401_on_invalid_password()
    {
        // Arrange
        var loginRequest = new LoginRequest("testuser", "wrongpass");

        // Act
        var response = await Client.PostAsJsonAsync("/monitoring/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_400_on_invalid_request_model()
    {
        // Arrange - Send empty/invalid request
        var invalidRequest = new { username = "", password = "" };

        // Act
        var response = await Client.PostAsJsonAsync("/monitoring/api/auth/login", invalidRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Should_validate_valid_token()
    {
        // Arrange - First login to get a valid token
        var loginRequest = new LoginRequest("testuser", "testpass");
        var loginResponse = await Client.PostAsJsonAsync("/monitoring/api/auth/login", loginRequest);
        loginResponse.EnsureSuccessStatusCode();

        var loginResult = await DeserializeResponseAsync<LoginResponse>(loginResponse);
        var token = loginResult!.Token;

        // Act - Validate the token via POST body
        var validateRequest = new TokenValidationRequest(token);
        var validateResponse = await Client.PostAsJsonAsync("/monitoring/api/auth/validate", validateRequest);

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
        var loginResponse = await Client.PostAsJsonAsync("/monitoring/api/auth/login", loginRequest);
        loginResponse.EnsureSuccessStatusCode();

        var loginResult = await DeserializeResponseAsync<LoginResponse>(loginResponse);
        var token = loginResult!.Token;

        // Create a new request with Authorization header and empty body
        var request = new HttpRequestMessage(HttpMethod.Post, "/monitoring/api/auth/validate");
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
        var validateResponse = await Client.PostAsJsonAsync("/monitoring/api/auth/validate", validateRequest);

        // Assert
        validateResponse.StatusCode.ShouldBe(HttpStatusCode.OK); // Endpoint returns 200 with IsValid=false

        var validationResult = await DeserializeResponseAsync<TokenValidationResponse>(validateResponse);
        validationResult.ShouldNotBeNull();
        validationResult.IsValid.ShouldBeFalse();
        validationResult.Username.ShouldBeNull();
    }
}
