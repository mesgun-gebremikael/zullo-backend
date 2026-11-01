using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zullo.Api.Data;

namespace Zullo.Api.Services;

public static class RelationTransaction
{
    // Same key as message delivery: serialize changes and sends for this pair.
    public static async Task<IDbContextTransaction> Begin(AppDbContext db, Guid a, Guid b)
    {
        var transaction = await db.Database.BeginTransactionAsync();
        if (db.Database.IsNpgsql())
        {
            // Serialize lifecycle changes without serializing separate conversations.
            foreach (var user in new[] { a, b }.Distinct().OrderBy(id => id))
                await AccountLifecycleLock.Shared(db, user);
            var pair = string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? $"{a}:{b}" : $"{b}:{a}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({pair}, 0))");
        }
        return transaction;
    }
}
