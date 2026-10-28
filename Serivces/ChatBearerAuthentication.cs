using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Zullo.Api.Services;

public static class ChatBearerAuthentication
{
    // Browser WebSocket APIs cannot set an Authorization header. Never accept
    // query credentials on ordinary API routes or override a supplied header.
    public static Task ReadHubToken(MessageReceivedContext context)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/hubs/chat") &&
            string.IsNullOrEmpty(request.Headers.Authorization))
        {
            var token = request.Query["access_token"];
            if (token.Count == 1 && !string.IsNullOrWhiteSpace(token[0]))
                context.Token = token[0];
        }
        return Task.CompletedTask;
    }
}
