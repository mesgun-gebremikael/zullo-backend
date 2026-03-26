using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;

namespace Zullo.Api.Services;

public class LoginAttemptService
{
    private readonly AppDbContext _db;

    public LoginAttemptService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsTryingTooFastAsync(string normalizedEmail)
    {
        var twoSecondsAgo = DateTime.UtcNow.AddSeconds(-2);

        var recentUser = await _db.Users.AsNoTracking()
            .Where(u => u.Email == normalizedEmail)
            .Select(u => new { u.Id })
            .FirstOrDefaultAsync();

        // Om user inte finns än kan vi inte läsa riktiga försök i DB här.
        // Light-version: bara returnera false nu.
        if (recentUser == null)
            return false;

        return false;
    }
}