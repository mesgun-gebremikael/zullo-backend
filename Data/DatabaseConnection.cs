using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace Zullo.Api.Data;

public static class DatabaseConnection
{
    public static string Resolve(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("ConnectionStrings:Default is required.");
        return Normalize(value);
    }

    public static string Normalize(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return value; // Preserve existing local Npgsql connection settings.
        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.Fragment.Length > 0)
                throw new FormatException();
            var credentials = uri.UserInfo.Split(':', 2);
            if (credentials.Length != 2 || credentials[0].Length == 0 || uri.AbsolutePath.Length <= 1)
                throw new FormatException();
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host.Trim('[', ']'), Port = uri.Port > 0 ? uri.Port : 5432,
                Username = Uri.UnescapeDataString(credentials[0]), Password = Uri.UnescapeDataString(credentials[1]),
                Database = Uri.UnescapeDataString(uri.AbsolutePath[1..]),
                // Render's private endpoint uses self-signed TLS certificates.
                SslMode = SslMode.Require,
                Timeout = 10, CommandTimeout = 30
            };
            foreach (var parameter in QueryHelpers.ParseQuery(uri.Query))
            {
                if (parameter.Key != "sslmode" || parameter.Value.Count != 1 ||
                    !Enum.TryParse<SslMode>(parameter.Value[0]?.Replace("-", ""), true, out var mode) ||
                    !Enum.IsDefined(mode) || parameter.Value[0]!.Any(char.IsDigit)) throw new FormatException();
                builder.SslMode = mode;
            }
            return builder.ConnectionString;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or UriFormatException)
        {
            // Never include a URL/password in configuration errors.
            throw new InvalidOperationException("Invalid PostgreSQL URL in ConnectionStrings:Default.");
        }
    }
}
