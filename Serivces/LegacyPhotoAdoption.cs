using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Services;

public record LegacyPhotoPlan(int Version, string CloudName, DateTime CreatedAtUtc, List<LegacyPhotoEntry> Entries, int ReviewRequired);
public record LegacyPhotoEntry(Guid UserId, string PublicId, string AssetId, List<string> Urls, string ReferenceHash);

// Operator-only service, deliberately not exposed through a controller.
public class LegacyPhotoAdoption(AppDbContext db, PhotoLifecycle lifecycle, IPhotoProvider provider)
{
    public static string Serialize(LegacyPhotoPlan plan) => JsonSerializer.Serialize(plan);
    public static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static string? PublicId(string url, string cloud)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "res.cloudinary.com" || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var match = Regex.Match(uri.AbsolutePath, "^/" + Regex.Escape(cloud) + @"/image/upload/(?:[^?]*/)?v[0-9]+/(?<id>zullo_profiles/[^?]+)\.(?:jpg|jpeg|png|webp)$", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var id = Uri.UnescapeDataString(match.Groups["id"].Value);
        return id.Length <= 250 && id.Split('/').All(p => p.Length > 0 && p is not ("." or "..") && Regex.IsMatch(p, "^[a-zA-Z0-9_-]+$")) ? id : null;
    }
    private async Task<List<(Guid User, string Url)>> References()
    {
        var result = new List<(Guid, string)>();
        await foreach (var profile in db.Profiles.AsNoTracking().AsAsyncEnumerable()) result.AddRange(profile.PhotoUrls.Select(u => (profile.UserId, u)));
        await foreach (var request in db.AccountRemovalRequests.AsNoTracking().Where(r => !r.ExternalAssetsReviewed).AsAsyncEnumerable()) result.AddRange(request.PhotoUrls.Select(u => (request.FormerUserId, u)));
        await foreach (var photo in db.OwnedPhotos.AsNoTracking().Where(p => p.State != PhotoStates.Deleted).AsAsyncEnumerable())
            result.AddRange(photo.Aliases.Append(photo.Url ?? "").Where(u => u.Length > 0).Select(u => (photo.UserId, u)));
        return result.Distinct().ToList();
    }
    private static string RefHash(IEnumerable<(Guid User, string Url)> refs) => Digest(string.Join("\n", refs.Select(r => $"{r.User:N}|{r.Url}").Order(StringComparer.Ordinal)));
    public async Task<LegacyPhotoPlan> Plan(int take, CancellationToken cancellation = default)
    {
        if (!provider.Enabled) throw new InvalidOperationException("Configure the Cloudinary provider before planning.");
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        var refs = await References();
        var groups = refs.Select(r => new { Ref = r, Id = PublicId(r.Url, provider.CloudName) }).Where(r => r.Id != null).GroupBy(r => r.Id!);
        var entries = new List<LegacyPhotoEntry>();
        var managed = await db.OwnedPhotos.AsNoTracking().Where(p => p.PublicId != null && p.ProviderAssetId != null && p.LegacyApprovedAtUtc == null && p.State != PhotoStates.Review && p.State != PhotoStates.Deleted).ToListAsync(cancellation);
        var reviewRequired = refs.Count(r => PublicId(r.Url, provider.CloudName) == null && !managed.Any(p => p.UserId == r.User && PhotoLifecycle.Matches(p, r.Url)));
        foreach (var group in groups.OrderBy(g => g.Key, StringComparer.Ordinal)) {
            var owners = group.Select(r => r.Ref.User).Distinct().ToList();
            if (owners.Count != 1) { reviewRequired++; continue; }
            if (await db.OwnedPhotos.AnyAsync(p => p.CloudName == provider.CloudName && p.PublicId == group.Key && p.LegacyApprovedAtUtc != null, cancellation)) continue;
            if (entries.Count >= take) break;
            var asset = await provider.InspectLegacy(group.Key, cancellation);
            if (asset == null) { reviewRequired++; continue; }
            var rows = group.Select(r => r.Ref).Distinct().ToList();
            entries.Add(new(owners[0], group.Key, asset.AssetId, rows.Select(r => r.Url).Order(StringComparer.Ordinal).ToList(), RefHash(rows)));
        }
        return new(1, provider.CloudName, DateTime.UtcNow, entries, reviewRequired);
    }
    public async Task Apply(LegacyPhotoPlan plan, string approvedDigest, CancellationToken cancellation = default)
    {
        if (approvedDigest != Digest(Serialize(plan)) || plan.Version != 1 || plan.CloudName != provider.CloudName || !provider.Enabled || plan.Entries.Count > 100)
            throw new InvalidOperationException("Approval digest or provider does not match the reviewed plan.");
        foreach (var entry in plan.Entries) {
            if (!entry.PublicId.StartsWith("zullo_profiles/", StringComparison.Ordinal) || entry.Urls.Count == 0 || entry.Urls.Any(url => PublicId(url, plan.CloudName) != entry.PublicId))
                throw new InvalidOperationException("Invalid legacy plan entry.");
            await using var transaction = await lifecycle.OwnerTransaction(entry.UserId);
            var refs = (await References()).Where(r => PublicId(r.Url, plan.CloudName) == entry.PublicId).ToList();
            if (refs.Any(r => r.User != entry.UserId) || RefHash(refs) != entry.ReferenceHash)
                throw new InvalidOperationException("References changed or multiple owners exist. Generate and review a fresh plan.");
            var live = await provider.InspectLegacy(entry.PublicId, cancellation);
            if (live == null || live.AssetId != entry.AssetId) throw new InvalidOperationException("Provider asset changed. Generate a fresh plan.");
            var records = await db.OwnedPhotos.Where(p => p.UserId == entry.UserId).ToListAsync(cancellation);
            var existing = await db.OwnedPhotos.SingleOrDefaultAsync(p => p.CloudName == plan.CloudName && p.PublicId == entry.PublicId, cancellation);
            if (existing != null && existing.UserId != entry.UserId) throw new InvalidOperationException("Asset is already owned by another account.");
            if (existing?.State == PhotoStates.Deleted) continue;
            var reviews = records.Where(p => p.State == PhotoStates.Review && p.Aliases.Append(p.Url ?? "").Any(entry.Urls.Contains)).ToList();
            var photo = existing ?? reviews.FirstOrDefault() ?? new OwnedPhoto { Id = Guid.NewGuid(), UserId = entry.UserId };
            if (photo.CleanupLeaseUntilUtc > DateTime.UtcNow) throw new InvalidOperationException("Cleanup is in progress; review a fresh plan later.");
            if (existing == null && reviews.Count == 0) db.OwnedPhotos.Add(photo);
            db.OwnedPhotos.RemoveRange(reviews.Where(r => r != photo));
            photo.CloudName = plan.CloudName; photo.PublicId = entry.PublicId; photo.ProviderAssetId = entry.AssetId;
            photo.Url = entry.Urls[0]; photo.Aliases = entry.Urls.Distinct().ToList();
            photo.LegacyApprovedAtUtc = DateTime.UtcNow; photo.LegacyApprovalDigest = approvedDigest;
            photo.UploadFinishedUtc = DateTime.UtcNow; photo.UploadLease = null; photo.LastErrorCode = null;
            var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == entry.UserId, cancellation);
            var open = await db.Users.AnyAsync(u => u.Id == entry.UserId && !u.IsClosed, cancellation);
            photo.State = open && profile != null && profile.PhotoUrls.Any(entry.Urls.Contains) ? PhotoStates.Attached : PhotoStates.Pending;
            photo.NextAttemptUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellation); await transaction.CommitAsync(cancellation);
        }
    }
}
