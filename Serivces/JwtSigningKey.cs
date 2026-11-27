using System.Text;

namespace Zullo.Api.Services;

public static class JwtSigningKey
{
    // Public example from appsettings.json; never a deployment credential.
    private const string DevelopmentExample = "SUPER_SECRET_DEV_KEY_CHANGE_THIS_123456789";

    public static string Require(string? value, bool development)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) < 32)
            throw new InvalidOperationException("JWT_SIGNING_KEY_REQUIRED: Configure Jwt:Key with at least 32 UTF-8 bytes from a cryptographically random source.");
        if (!development && string.Equals(value.Trim(), DevelopmentExample, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("JWT_DEVELOPMENT_KEY_FORBIDDEN: Configure a private Jwt:Key before running outside Development.");
        return value;
    }
}
