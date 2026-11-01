using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Services;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class PhotoManagementTests
{
    internal static readonly byte[] Image = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0 };
    internal class Provider : IPhotoProvider
    {
        public bool Enabled { get; set; } = true;
        public string CloudName => "test-cloud";
        public int Uploads, Deletes;
        public string? Failure;
        public Func<OwnedPhoto, Task>? BeforeUpload;
        public Task<VerifiedPhoto> Upload(OwnedPhoto photo, byte[] bytes, CancellationToken cancellation) => Run(photo);
        private async Task<VerifiedPhoto> Run(OwnedPhoto photo) {
            Interlocked.Increment(ref Uploads);
            if (BeforeUpload != null) await BeforeUpload(photo);
            if (Failure != null) throw new PhotoProviderException(Failure);
            return new("asset-" + photo.Id, photo.PublicId!, $"https://res.cloudinary.com/{CloudName}/image/upload/v1/{photo.PublicId}.png");
        }
        public Task Delete(OwnedPhoto photo, CancellationToken cancellation) {
            Interlocked.Increment(ref Deletes);
            if (Failure != null) throw new PhotoProviderException(Failure);
            return Task.CompletedTask;
        }
        public Task<VerifiedPhoto?> InspectLegacy(string id, CancellationToken cancellation) => Task.FromResult<VerifiedPhoto?>(new("legacy-asset", id, $"https://res.cloudinary.com/{CloudName}/image/upload/v1/{id}.jpg"));
    }
    static Task<TestApp> Start(Provider provider) => TestApp.Start(configure: s => s.AddSingleton<IPhotoProvider>(provider));
    static Task<HttpResponseMessage> Upload(HttpClient client, Guid id, byte[]? image = null) {
        var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(image ?? Image), "file", "private-local-name.png");
        return client.PutAsync($"/me/photos/{id}", form);
    }
    static PhotoCleanup Cleanup(AppDbContext db, Provider provider) => new(db, new PhotoLifecycle(db, provider), provider, NullLogger<PhotoCleanup>.Instance);
    static OwnedPhoto Draft(Guid user, Provider p, string state = PhotoStates.Ready) {
        var id = Guid.NewGuid();
        return new OwnedPhoto { Id = id, UserId = user, CloudName = p.CloudName, PublicId = $"zullo_owned/{user:N}/{id:N}",
            ProviderAssetId = "asset-" + id, Url = $"https://res.cloudinary.com/{p.CloudName}/image/upload/v1/zullo_owned/{user:N}/{id:N}.png",
            FileHash = Convert.ToHexString(SHA256.HashData(Image)), State = state, UploadFinishedUtc = DateTime.UtcNow, NextAttemptUtc = DateTime.UtcNow.AddHours(24) };
    }
    [Fact]
    public async Task AuthenticatedUploadIsIdempotentAndOtherAccountsCannotReadOrAttachIt()
    {
        var provider = new Provider(); await using var app = await Start(provider);
        using var a = app.Client(app.A); using var b = app.Client(app.B); using var anonymous = app.Client(app.A, "missing");
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(anonymous, id)).StatusCode);
        var uploaded = await (await Upload(a, id)).Content.ReadFromJsonAsync<PhotoView>();
        Assert.Equal(PhotoStates.Ready, uploaded!.State); Assert.NotNull(uploaded.Url);
        Assert.Equal(HttpStatusCode.OK, (await Upload(a, id)).StatusCode); Assert.Equal(1, provider.Uploads);
        var changed = Image.ToArray(); changed[^1] = 1;
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(a, id, changed)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/me/photos/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(b, id)).StatusCode);
        var stolen = await b.PostAsJsonAsync("/me/profile", new { displayName = "B", age = 25, gender = "Man", photoUrls = new[] { uploaded.Url } });
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);
        await using var db = app.Db(); Assert.False(await db.Profiles.AnyAsync(p => p.UserId == app.B));
    }
    [Fact]
    public async Task DisabledProviderAndInvalidImageNeverStartAnExternalUpload()
    {
        var provider = new Provider { Enabled = false }; await using var app = await Start(provider); using var a = app.Client(app.A);
        Assert.False((await a.GetFromJsonAsync<System.Text.Json.JsonElement>("/me/photos/availability")).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Upload(a, Guid.NewGuid())).StatusCode);
        provider.Enabled = true;
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(a, Guid.NewGuid(), new byte[20])).StatusCode);
        Assert.Equal(0, provider.Uploads); await using var db = app.Db(); Assert.Empty(await db.OwnedPhotos.ToListAsync());
    }
    [Fact]
    public async Task DeletedTombstonesStillCountTowardDailyUploadQuota()
    {
        var provider = new Provider(); await using var app = await Start(provider); using var a = app.Client(app.A);
        await using (var db = app.Db()) {
            for (var i = 0; i < 50; i++) { var photo = Draft(app.A, provider, PhotoStates.Deleted); photo.FileHash = ""; photo.UploadAttempts = 1; db.OwnedPhotos.Add(photo); }
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Upload(a, Guid.NewGuid())).StatusCode); Assert.Equal(0, provider.Uploads);
    }
    [Fact]
    public async Task ProfileCommitAttachesImagesAndOnlySavedRemovalQueuesDeletion()
    {
        var provider = new Provider(); await using var app = await Start(provider); using var a = app.Client(app.A);
        var id = Guid.NewGuid(); var image = await (await Upload(a, id)).Content.ReadFromJsonAsync<PhotoView>();
        await using (var db = app.Db()) {
            Assert.Equal(PhotoStates.Ready, (await db.OwnedPhotos.FindAsync(id))!.State);
            await Cleanup(db, provider).RunOnce(); Assert.Equal(0, provider.Deletes); // Draft not expired: edit may be cancelled.
        }
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/me/profile", new { displayName = "A", age = 25, gender = "Man", photoUrls = new[] { image!.Url } })).StatusCode);
        await using (var db = app.Db()) { Assert.Equal(PhotoStates.Attached, (await db.OwnedPhotos.FindAsync(id))!.State); }
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync("/me/profile", new { photoUrls = new[] { image!.Url, image.Url } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PatchAsJsonAsync("/me/profile", new { photoUrls = Array.Empty<string>() })).StatusCode);
        await using (var db = app.Db()) {
            Assert.Equal(PhotoStates.Pending, (await db.OwnedPhotos.FindAsync(id))!.State);
            await Cleanup(db, provider).RunOnce();
            var photo = await db.OwnedPhotos.FindAsync(id); Assert.Equal(PhotoStates.Deleted, photo!.State); Assert.Null(photo.Url); Assert.Empty(photo.FileHash);
            await Cleanup(db, provider).RunOnce(); Assert.Equal(1, provider.Deletes);
        }
    }
    [Fact]
    public async Task ProviderFailureKeepsDurableJobAndBackoffSurvivesRepeatedRuns()
    {
        var provider = new Provider { Failure = "provider_http_503" }; await using var app = await Start(provider); using var a = app.Client(app.A);
        var id = Guid.NewGuid(); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Upload(a, id)).StatusCode);
        await using var db = app.Db(); var photo = await db.OwnedPhotos.FindAsync(id);
        Assert.Equal(PhotoStates.Pending, photo!.State); Assert.True(photo.NextAttemptUtc > DateTime.UtcNow.AddMinutes(100));
        photo.NextAttemptUtc = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        var cleanup = Cleanup(db, provider); await cleanup.RunOnce();
        photo = await db.OwnedPhotos.FindAsync(id); var retry = photo!.NextAttemptUtc;
        Assert.Equal(1, provider.Deletes); Assert.Equal(PhotoStates.Pending, photo.State); Assert.True(retry > DateTime.UtcNow);
        await cleanup.RunOnce(); Assert.Equal(1, provider.Deletes); Assert.Equal(retry, (await db.OwnedPhotos.FindAsync(id))!.NextAttemptUtc);
        provider.Failure = null; photo = await db.OwnedPhotos.FindAsync(id); photo!.NextAttemptUtc = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        await cleanup.RunOnce(); Assert.Equal(PhotoStates.Deleted, (await db.OwnedPhotos.FindAsync(id))!.State);
    }
    [Fact]
    public async Task ExpiredDraftIsNotPublishedAndWorkerProtectsAReferencedImage()
    {
        var provider = new Provider(); await using var app = await Start(provider); using var a = app.Client(app.A);
        var expired = Draft(app.A, provider); expired.NextAttemptUtc = DateTime.UtcNow.AddDays(-1);
        var referenced = Draft(app.A, provider); referenced.NextAttemptUtc = DateTime.UtcNow.AddDays(-1);
        await using (var db = app.Db()) {
            db.OwnedPhotos.AddRange(expired, referenced); db.Profiles.Add(new Profile { UserId = app.A, DisplayName = "A", Age = 25, Gender = "Man", PhotoUrls = new() { referenced.Url! } }); await db.SaveChangesAsync();
        }
        Assert.Null((await a.GetFromJsonAsync<PhotoView>($"/me/photos/{expired.Id}"))!.Url);
        Assert.Equal(HttpStatusCode.Gone, (await Upload(a, expired.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync("/me/profile", new { photoUrls = new[] { expired.Url } })).StatusCode);
        await using var check = app.Db(); await Cleanup(check, provider).RunOnce();
        Assert.Equal(PhotoStates.Deleted, (await check.OwnedPhotos.FindAsync(expired.Id))!.State);
        Assert.Equal(PhotoStates.Attached, (await check.OwnedPhotos.FindAsync(referenced.Id))!.State); Assert.Equal(1, provider.Deletes);
    }
    [Fact]
    public async Task ClosingAccountDuringUploadCannotResurrectPhotoAndEventuallyCompletesErasure()
    {
        var provider = new Provider(); await using var app = await Start(provider); using var a = app.Client(app.A);
        await using (var db = app.Db()) { (await db.Users.FindAsync(app.A))!.PasswordHash = BCrypt.Net.BCrypt.HashPassword("photo-password"); await db.SaveChangesAsync(); }
        provider.BeforeUpload = async _ => {
            var response = await a.PostAsJsonAsync("/me/account/removal", new { password = "photo-password", confirm = true });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        };
        var id = Guid.NewGuid(); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Upload(a, id)).StatusCode);
        await using var check = app.Db();
        Assert.Equal(PhotoStates.Pending, (await check.OwnedPhotos.FindAsync(id))!.State);
        Assert.False((await check.AccountRemovalRequests.SingleAsync()).ExternalAssetsReviewed);
        await Cleanup(check, provider).RunOnce();
        Assert.Equal(PhotoStates.Deleted, (await check.OwnedPhotos.FindAsync(id))!.State);
        Assert.True((await check.AccountRemovalRequests.SingleAsync()).ExternalAssetsReviewed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync($"/me/photos/{id}")).StatusCode);
    }
    [Fact]
    public async Task AmbiguousOwnershipIsQuarantinedInsteadOfRepeatedDeletion()
    {
        var provider = new Provider { Failure = "ownership_mismatch" }; await using var app = await Start(provider);
        var photo = Draft(app.A, provider, PhotoStates.Pending); photo.NextAttemptUtc = DateTime.UtcNow;
        await using var db = app.Db(); db.OwnedPhotos.Add(photo); await db.SaveChangesAsync();
        await Cleanup(db, provider).RunOnce(); Assert.Equal(PhotoStates.Review, (await db.OwnedPhotos.FindAsync(photo.Id))!.State);
        await Cleanup(db, provider).RunOnce(); Assert.Equal(1, provider.Deletes);
    }
    [Fact]
    public async Task LegacyPlanIsReadOnlyRequiresExactApprovalAndResolvesClosedAccountWithoutRecreatingReferences()
    {
        var provider = new Provider(); await using var app = await Start(provider);
        var url = "https://res.cloudinary.com/test-cloud/image/upload/v42/zullo_profiles/legacy.jpg";
        await using var db = app.Db();
        (await db.Users.FindAsync(app.A))!.IsClosed = true;
        db.AccountRemovalRequests.Add(new AccountRemovalRequest { FormerUserId = app.A, PhotoUrls = new() { url } }); await db.SaveChangesAsync();
        var lifecycle = new PhotoLifecycle(db, provider); var cleanup = Cleanup(db, provider); await cleanup.RunOnce();
        Assert.Equal(0, provider.Deletes); Assert.Equal(PhotoStates.Review, (await db.OwnedPhotos.SingleAsync()).State);
        var adoption = new LegacyPhotoAdoption(db, lifecycle, provider); var plan = await adoption.Plan(50);
        Assert.Single(plan.Entries); Assert.Equal(PhotoStates.Review, (await db.OwnedPhotos.SingleAsync()).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adoption.Apply(plan, "wrong-digest"));
        await adoption.Apply(plan, LegacyPhotoAdoption.Digest(LegacyPhotoAdoption.Serialize(plan)));
        await cleanup.RunOnce(); await cleanup.RunOnce();
        Assert.Equal(1, provider.Deletes); Assert.Single(await db.OwnedPhotos.ToListAsync()); Assert.Equal(PhotoStates.Deleted, (await db.OwnedPhotos.SingleAsync()).State);
        var request = await db.AccountRemovalRequests.SingleAsync(); Assert.True(request.ExternalAssetsReviewed); Assert.Empty(request.PhotoUrls);
    }
    [Fact]
    public async Task SharedLegacyAssetAndForeignUrlsAreNeverAutomaticallyAdopted()
    {
        var provider = new Provider(); await using var app = await Start(provider); await using var db = app.Db();
        var shared = "https://res.cloudinary.com/test-cloud/image/upload/v1/zullo_profiles/shared.jpg";
        db.Profiles.AddRange(new Profile { UserId = app.A, PhotoUrls = new() { shared, "https://other.test/unknown.jpg" } }, new Profile { UserId = app.B, PhotoUrls = new() { shared } }); await db.SaveChangesAsync();
        var adoption = new LegacyPhotoAdoption(db, new PhotoLifecycle(db, provider), provider); var plan = await adoption.Plan(50);
        Assert.Empty(plan.Entries); Assert.Equal(2, plan.ReviewRequired); Assert.Empty(await db.OwnedPhotos.ToListAsync()); Assert.Equal(0, provider.Deletes);
    }
    [Fact]
    public async Task ChangedLegacyReferencesInvalidatePreviouslyReviewedPlan()
    {
        var provider = new Provider(); await using var app = await Start(provider); await using var db = app.Db();
        var url = "https://res.cloudinary.com/test-cloud/image/upload/v1/zullo_profiles/changed.jpg";
        db.Profiles.Add(new Profile { UserId = app.A, PhotoUrls = new() { url } }); await db.SaveChangesAsync();
        var adoption = new LegacyPhotoAdoption(db, new PhotoLifecycle(db, provider), provider); var plan = await adoption.Plan(50);
        db.Profiles.Add(new Profile { UserId = app.B, PhotoUrls = new() { url } }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => adoption.Apply(plan, LegacyPhotoAdoption.Digest(LegacyPhotoAdoption.Serialize(plan)))); Assert.Equal(0, provider.Deletes);
    }
}
