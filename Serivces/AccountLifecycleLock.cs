using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;

namespace Zullo.Api.Services;

// Shared transaction locks let independent conversations continue in parallel.
// Closure/pause exclusively locks this key before changing account state.
public static class AccountLifecycleLock
{
    public static async Task Shared(AppDbContext db, Guid user)
    {
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({"account:" + user}, 0))");
    }
    public static async Task Exclusive(AppDbContext db, Guid user)
    {
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"account:" + user}, 0))");
    }
}
