using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Services;

public class PhotoOperationException(int status, string code, string message) : Exception(message)
{ public int Status { get; } = status; public string Code { get; } = code; }
public record PhotoView(Guid Id, string State, string? Url);

public class PhotoLifecycle(AppDbContext db, IPhotoProvider provider)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public static bool ValidImage(byte[] bytes) => bytes.Length >= 12 && (
        (bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) ||
        bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
        (bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)));
    internal async Task<IDbContextTransaction> OwnerTransaction(Guid user)
    {
        db.ChangeTracker.Clear();
        var transaction = await db.Database.BeginTransactionAsync();
        await AccountLifecycleLock.Shared(db, user);
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"User\" WHERE \"Id\" = {user} FOR UPDATE");
        return transaction;
    }
    internal async Task<OwnedPhoto?> Locked(Guid id)
    {
        if (db.Database.IsNpgsql()) return await db.OwnedPhotos.FromSqlInterpolated($"SELECT * FROM \"OwnedPhotos\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync();
        return await db.OwnedPhotos.SingleOrDefaultAsync(p => p.Id == id);
    }
    private static PhotoView View(OwnedPhoto photo) => new(photo.Id, photo.State,
        photo.State is PhotoStates.Ready or PhotoStates.Attached ? photo.Url : null);
    public async Task<PhotoView?> Read(Guid user, Guid id)
    {
        var photo = await db.OwnedPhotos.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.UserId == user);
        if (photo == null) return null;
        return photo.State == PhotoStates.Ready && photo.NextAttemptUtc <= DateTime.UtcNow
            ? new PhotoView(id, PhotoStates.Pending, null) : View(photo);
    }
    public async Task<PhotoView> Upload(Guid user, Guid id, byte[] bytes, CancellationToken cancellation)
    {
        if (!provider.Enabled) throw new PhotoOperationException(503, "photos_unavailable", "Bilduppladdning är inte tillgänglig just nu. Dina sparade bilder påverkas inte.");
        if (id == Guid.Empty || bytes.Length > MaxBytes || !ValidImage(bytes))
            throw new PhotoOperationException(400, "invalid_image", "Välj en JPG-, PNG- eller WebP-bild på högst 10 MB.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        OwnedPhoto upload;
        var lease = Guid.NewGuid();
        await using (var transaction = await OwnerTransaction(user)) {
            if (!await db.Users.AnyAsync(u => u.Id == user && !u.IsClosed)) throw new PhotoOperationException(401, "account_closed", "Logga in igen.");
            var existing = await Locked(id);
            if (existing != null) {
                if (existing.UserId != user) throw new PhotoOperationException(404, "not_found", "Bilden hittades inte.");
                if (existing.FileHash != hash) throw new PhotoOperationException(409, "different_image", "Välj bilden igen för en ny uppladdning.");
                if (existing.State == PhotoStates.Ready && existing.NextAttemptUtc <= DateTime.UtcNow)
                    throw new PhotoOperationException(410, "upload_expired", "Uppladdningen har avslutats. Välj bilden igen.");
                if (existing.State is PhotoStates.Ready or PhotoStates.Attached) return View(existing);
                if (existing.State != PhotoStates.Uploading) throw new PhotoOperationException(410, "upload_expired", "Uppladdningen har avslutats. Välj bilden igen.");
                if (existing.UploadLeaseUntilUtc > DateTime.UtcNow) return View(existing);
                upload = existing;
            } else {
                var cutoff = DateTime.UtcNow.AddDays(-1);
                if (await db.OwnedPhotos.CountAsync(p => p.UserId == user && p.State != PhotoStates.Deleted && p.State != PhotoStates.Review) >= 12 ||
                    await db.OwnedPhotos.CountAsync(p => p.UserId == user && p.CreatedAtUtc > cutoff && p.UploadAttempts > 0) >= 50)
                    throw new PhotoOperationException(429, "photo_limit", "Du har gjort många uppladdningar. Vänta en stund innan du försöker igen.");
                upload = new OwnedPhoto { Id = id, UserId = user, FileHash = hash, CloudName = provider.CloudName,
                    PublicId = $"zullo_owned/{user:N}/{id:N}" };
                db.OwnedPhotos.Add(upload);
            }
            upload.UploadAttempts++; upload.UploadLease = lease; upload.UploadLeaseUntilUtc = DateTime.UtcNow.AddMinutes(3);
            // A lost upload response may still finish at the provider. Delay orphan cleanup
            // beyond the provider's signature window, and reconcile before retrying upload.
            upload.NextAttemptUtc = DateTime.UtcNow.AddHours(2);
            try { await db.SaveChangesAsync(cancellation); }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }) {
                throw new PhotoOperationException(409, "upload_conflict", "Välj bilden igen för en ny uppladdning.");
            }
            await transaction.CommitAsync(cancellation);
        }
        VerifiedPhoto? verified = null;
        string? error = null;
        try { verified = await provider.Upload(upload, bytes, cancellation); }
        catch (PhotoProviderException ex) { error = ex.Code; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { error = "upload_unconfirmed"; }
        catch (Exception) { error = "unexpected_provider_failure"; }
        await using (var transaction = await OwnerTransaction(user)) {
            var current = await Locked(id) ?? throw new InvalidOperationException("Reserved photo disappeared.");
            var closed = !await db.Users.AnyAsync(u => u.Id == user && !u.IsClosed);
            if (current.UploadLease != lease && !closed && current.State != PhotoStates.Deleted) return View(current);
            if (current.State == PhotoStates.Deleted && verified != null) current.State = PhotoStates.Pending;
            if (current.UploadLease == lease && current.State == PhotoStates.Uploading && verified != null && !closed) {
                current.ProviderAssetId = verified.AssetId; current.Url = verified.Url;
                current.State = PhotoStates.Ready; current.UploadFinishedUtc = DateTime.UtcNow;
                current.UploadLease = null; current.UploadLeaseUntilUtc = null; current.NextAttemptUtc = DateTime.UtcNow.AddHours(24);
                current.LastErrorCode = null;
            } else {
                // Never resurrect a photo that closure or a newer operation has retired.
                if (verified != null) {
                    current.ProviderAssetId = verified.AssetId; current.UploadFinishedUtc = DateTime.UtcNow;
                    if (current.UploadAttempts == 1) current.NextAttemptUtc = DateTime.UtcNow;
                }
                if (error == "ownership_mismatch") { current.State = PhotoStates.Review; current.LastErrorCode = error; }
                else { Queue(current); current.LastErrorCode = error ?? "upload_retired"; }
                current.UploadLease = null;
            }
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            if (current.State != PhotoStates.Ready) throw new PhotoOperationException(503, error ?? "upload_retired", "Uppladdningen kunde inte bekräftas. Försök igen. En osparad bild städas bort automatiskt.");
            return View(current);
        }
    }
    internal static bool Matches(OwnedPhoto photo, string url) => photo.Url == url || photo.Aliases.Contains(url);
    internal static void Queue(OwnedPhoto photo)
    {
        if (photo.State is PhotoStates.Deleted or PhotoStates.Review or PhotoStates.Pending) return;
        var unknownUpload = photo.UploadFinishedUtc == null;
        photo.State = PhotoStates.Pending;
        photo.NextAttemptUtc = unknownUpload ? new[] { photo.NextAttemptUtc, DateTime.UtcNow }.Max() : DateTime.UtcNow;
    }
    private async Task Review(Guid user, IEnumerable<string> urls, List<OwnedPhoto> photos)
    {
        foreach (var url in urls.Distinct()) {
            if (photos.Any(p => Matches(p, url))) continue;
            var review = new OwnedPhoto { Id = Guid.NewGuid(), UserId = user, State = PhotoStates.Review, Url = url, LastErrorCode = "legacy_ownership_review" };
            db.OwnedPhotos.Add(review); photos.Add(review);
        }
        await Task.CompletedTask;
    }
    public async Task ImportLegacyReviews(Guid user, IEnumerable<string> urls)
    {
        var photos = await db.OwnedPhotos.Where(p => p.UserId == user).ToListAsync();
        await Review(user, urls, photos);
    }
    // Caller holds the owner's lifecycle and row locks, in the profile transaction.
    public async Task<string?> ChangeProfile(Guid user, IReadOnlyCollection<string> previous, IReadOnlyCollection<string> desired)
    {
        if (desired.Count != desired.Distinct(StringComparer.Ordinal).Count()) return "Samma bild får inte läggas till flera gånger.";
        var photos = await db.OwnedPhotos.Where(p => p.UserId == user).ToListAsync();
        foreach (var url in desired.Except(previous)) {
            var photo = photos.SingleOrDefault(p => Matches(p, url));
            if (photo == null || (photo.State != PhotoStates.Attached && !(photo.State == PhotoStates.Ready && photo.NextAttemptUtc > DateTime.UtcNow)))
                return "Ladda upp nya bilder genom appen innan du sparar profilen.";
        }
        foreach (var photo in photos) {
            if (desired.Any(url => Matches(photo, url)) && photo.State is PhotoStates.Ready or PhotoStates.Attached) {
                photo.State = PhotoStates.Attached;
            } else if (photo.State == PhotoStates.Attached) Queue(photo);
        }
        await Review(user, previous.Except(desired), photos);
        return null;
    }
    // Account closure caller has the exclusive lifecycle lock and the owner's row lock.
    public async Task<List<string>> Close(Guid user, IEnumerable<string> profileUrls)
    {
        var photos = await db.OwnedPhotos.Where(p => p.UserId == user).ToListAsync();
        await Review(user, profileUrls, photos);
        foreach (var photo in photos) Queue(photo);
        return photos.Where(p => p.State == PhotoStates.Review).SelectMany(p => p.Aliases.Append(p.Url ?? "")).Where(u => u.Length > 0).Distinct().ToList();
    }
}
