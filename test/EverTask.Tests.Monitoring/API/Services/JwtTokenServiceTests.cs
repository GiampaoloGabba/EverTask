using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EverTask.Monitor.Api.Options;
using EverTask.Monitor.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EverTask.Tests.Monitoring.API.Services;

public class JwtTokenServiceTests
{
    private readonly Mock<ILogger<JwtTokenService>> _loggerMock;

    public JwtTokenServiceTests()
    {
        _loggerMock = new Mock<ILogger<JwtTokenService>>();
    }

    [Fact]
    public void Should_generate_valid_jwt_token()
    {
        // Arrange
        var options = CreateOptions();
        var service = new JwtTokenService(options, _loggerMock.Object);
        var username = "testuser";

        // Act
        var response = service.GenerateToken(username);

        // Assert
        response.ShouldNotBeNull();
        response.Token.ShouldNotBeNullOrEmpty();
        response.Username.ShouldBe(username);
        response.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Should_include_correct_claims_in_token()
    {
        // Arrange
        var options = CreateOptions();
        var service = new JwtTokenService(options, _loggerMock.Object);
        var username = "testuser";

        // Act
        var response = service.GenerateToken(username);

        // Assert
        var tokenHandler = new JwtSecurityTokenHandler();
        var jwtToken = tokenHandler.ReadJwtToken(response.Token);

        var subClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub);
        subClaim.ShouldNotBeNull();
        subClaim.Value.ShouldBe(username);

        var jtiClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti);
        jtiClaim.ShouldNotBeNull();
        Guid.TryParse(jtiClaim.Value, out _).ShouldBeTrue();

        var iatClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iat);
        iatClaim.ShouldNotBeNull();
    }

    [Fact]
    public void Should_validate_valid_token()
    {
        // Arrange
        var options = CreateOptions();
        var service = new JwtTokenService(options, _loggerMock.Object);
        var username = "testuser";
        var loginResponse = service.GenerateToken(username);

        // Ensure token is actually generated
        loginResponse.Token.ShouldNotBeNullOrEmpty();

        // Small delay to ensure token is not validated at exact creation time
        Thread.Sleep(100);

        // Act
        var validationResponse = service.ValidateToken(loginResponse.Token);

        // Assert
        validationResponse.ShouldNotBeNull();
        validationResponse.IsValid.ShouldBeTrue();
        validationResponse.Username.ShouldBe(username);
        validationResponse.ExpiresAt.ShouldNotBeNull();
    }

    [Fact]
    public void Should_reject_expired_token()
    {
        // Arrange - Create a token manually that's expired
        var secret = "this-is-a-test-secret-key-with-at-least-32-bytes!";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // Create expired token (expired 1 hour ago)
        var token = new JwtSecurityToken(
            issuer: "TestIssuer",
            audience: "TestAudience",
            claims: new[] { new Claim(JwtRegisteredClaimNames.Sub, "testuser") },
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1), // Expired 1 hour ago
            signingCredentials: credentials
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        var apiOptions = new EverTaskApiOptions
        {
            JwtSecret = secret,
            JwtIssuer = "TestIssuer",
            JwtAudience = "TestAudience"
        };
        var options = Options.Create(apiOptions);
        var service = new JwtTokenService(options, _loggerMock.Object);

        // Act
        var validationResponse = service.ValidateToken(tokenString);

        // Assert
        validationResponse.ShouldNotBeNull();
        validationResponse.IsValid.ShouldBeFalse();
        validationResponse.Username.ShouldBeNull();
    }

    [Fact]
    public void Should_reject_token_with_invalid_signature()
    {
        // Arrange
        var options = CreateOptions();
        var service = new JwtTokenService(options, _loggerMock.Object);

        // Create a token with different secret
        var differentSecret = "different-secret-key-with-32-bytes-min!";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(differentSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "TestIssuer",
            audience: "TestAudience",
            claims: new[] { new Claim(JwtRegisteredClaimNames.Sub, "testuser") },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        // Act
        var validationResponse = service.ValidateToken(tokenString);

        // Assert
        validationResponse.ShouldNotBeNull();
        validationResponse.IsValid.ShouldBeFalse();
        validationResponse.Username.ShouldBeNull();
    }

    [Fact]
    public void Should_reject_malformed_token()
    {
        // Arrange
        var options = CreateOptions();
        var service = new JwtTokenService(options, _loggerMock.Object);
        var malformedToken = "this.is.not.a.valid.jwt.token";

        // Act
        var validationResponse = service.ValidateToken(malformedToken);

        // Assert
        validationResponse.ShouldNotBeNull();
        validationResponse.IsValid.ShouldBeFalse();
        validationResponse.Username.ShouldBeNull();
    }

    [Fact]
    public void Should_generate_random_secret_when_not_configured()
    {
        // Arrange
        var apiOptions = new EverTaskApiOptions
        {
            JwtSecret = null, // Not configured
            JwtIssuer = "TestIssuer",
            JwtAudience = "TestAudience"
        };
        var options = Options.Create(apiOptions);

        // Act
        var service = new JwtTokenService(options, _loggerMock.Object);

        // Assert - Should log warning about missing secret
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("JWT secret not configured")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        // Service should still work with auto-generated secret
        var response = service.GenerateToken("testuser");
        response.Token.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Should_warn_on_weak_secret()
    {
        // Arrange - Create options with short secret (less than 32 bytes)
        var apiOptions = new EverTaskApiOptions
        {
            JwtSecret = "short-secret", // Less than 32 bytes
            JwtIssuer = "TestIssuer",
            JwtAudience = "TestAudience"
        };
        var options = Options.Create(apiOptions);

        // Act
        var service = new JwtTokenService(options, _loggerMock.Object);

        // Assert - Should log warning about weak secret
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("shorter than recommended minimum")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Should_set_correct_expiration_time()
    {
        // Arrange
        var expirationHours = 12;
        var apiOptions = new EverTaskApiOptions
        {
            JwtSecret = "this-is-a-test-secret-key-with-at-least-32-bytes!",
            JwtIssuer = "TestIssuer",
            JwtAudience = "TestAudience",
            JwtExpirationHours = expirationHours
        };
        var options = Options.Create(apiOptions);
        var service = new JwtTokenService(options, _loggerMock.Object);

        // Act
        var before = DateTimeOffset.UtcNow;
        var response = service.GenerateToken("testuser");
        var after = DateTimeOffset.UtcNow;

        // Assert - Expiration should be approximately expirationHours from now
        var expectedExpiration = before.AddHours(expirationHours);
        var expirationDiff = Math.Abs((response.ExpiresAt - expectedExpiration).TotalMinutes);
        expirationDiff.ShouldBeLessThan(1); // Within 1 minute tolerance
    }

    private static IOptions<EverTaskApiOptions> CreateOptions()
    {
        var apiOptions = new EverTaskApiOptions
        {
            JwtSecret = "this-is-a-test-secret-key-with-at-least-32-bytes!",
            JwtIssuer = "TestIssuer",
            JwtAudience = "TestAudience",
            JwtExpirationHours = 8
        };
        return Options.Create(apiOptions);
    }
}
