using System.ComponentModel.DataAnnotations;

namespace EverTask.Monitor.Api.DTOs.Auth;

/// <summary>
/// Login request containing user credentials.
/// </summary>
/// <param name="Username">The username for authentication.</param>
/// <param name="Password">The password for authentication.</param>
public record LoginRequest(
    [Required(ErrorMessage = "Username is required")]
    string Username,

    [Required(ErrorMessage = "Password is required")]
    string Password
);
