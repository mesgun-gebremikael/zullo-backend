using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;

namespace Zullo.Api.Services;

public class UserRelationService
{
    private readonly AppDbContext _db;

    public UserRelationService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsBlockedAsync(Guid meId, Guid otherUserId)
    {
        // Sant om jag blockat den andra, eller den andra blockat mig
        return await _db.Blocks.AnyAsync(b =>
            (b.FromUserId == meId && b.BlockedUserId == otherUserId) ||
            (b.FromUserId == otherUserId && b.BlockedUserId == meId));
    }

    public async Task<List<Guid>> GetBlockedUserIdsAsync(Guid meId)
    {
        // Hämtar alla användare som jag blockat eller som blockat mig
        return await _db.Blocks.AsNoTracking()
            .Where(b => b.FromUserId == meId || b.BlockedUserId == meId)
            .Select(b => b.FromUserId == meId ? b.BlockedUserId : b.FromUserId)
            .ToListAsync();
    }

    public async Task<bool> IsMatchedAsync(Guid userAId, Guid userBId)
    {
        // Match sparas i fast ordning: lägsta Guid först
        var firstUserId = userAId.CompareTo(userBId) < 0 ? userAId : userBId;
        var secondUserId = userAId.CompareTo(userBId) < 0 ? userBId : userAId;

        return await _db.Matches.AnyAsync(m =>
            m.UserAId == firstUserId && m.UserBId == secondUserId);
    }
}
