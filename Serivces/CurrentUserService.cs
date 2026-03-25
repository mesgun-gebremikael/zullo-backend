using System.Security.Claims;

namespace Zullo.Api.Services;

public static class CurrentUserService
{
    // Hämtar inloggad användares Guid från JWT-claim
    public static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;

        var userIdStr = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdStr, out userId);
    }

    // Samma som ovan, men kastar fel om claim saknas eller är ogiltig
    public static Guid GetUserIdOrThrow(ClaimsPrincipal user)
    {
        var userIdStr = user.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            throw new UnauthorizedAccessException("Missing/invalid user id in token.");

        return userId;
    }
}