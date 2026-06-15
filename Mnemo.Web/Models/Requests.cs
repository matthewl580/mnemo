namespace Mnemo.Web.Models;

public sealed class RegisterRequest
{
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class LoginRequest
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class AuthResponse
{
    public string Token { get; set; }
    public string UserName { get; set; }
    public string Email { get; set; }

    public AuthResponse(string token, string userName, string email)
    {
        Token = token;
        UserName = userName;
        Email = email;
    }
}
