using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Services;

public class LikeLimitService
{
    private readonly AppDbContext _db;

    public LikeLimitService(AppDbContext db)
    {
        _db = db;
    }

    // Ser till att fönstret (12h) är uppdaterat
    public async Task EnsureWindowUpToDateAsync(User user)
    {
        var now = DateTime.UtcNow;

        if (now >= user.LikesResetAtUtc)
        {
            user.LikesRemaining = 50;
            user.LikesResetAtUtc = now.AddHours(12);
            await _db.SaveChangesAsync();
        }
    }

    // Försöker använda en like. Returnerar true/false.
    public async Task<bool> TryConsumeLikeAsync(Guid userId)
    {
        var user = await _db.Users.FirstAsync(u => u.Id == userId);

        await EnsureWindowUpToDateAsync(user);

        if (user.LikesRemaining <= 0)
            return false;

        user.LikesRemaining -= 1;
        await _db.SaveChangesAsync();
        return true;
    }
}
