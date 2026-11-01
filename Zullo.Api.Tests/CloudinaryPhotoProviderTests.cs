using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zullo.Api.Models;
using Zullo.Api.Services;

namespace Zullo.Api.Tests;

public class CloudinaryPhotoProviderTests
{
    const string Secret = "test-only-secret-123456789";
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => action(request);
    }
    static CloudinaryPhotoProvider Provider(Func<HttpRequestMessage, Task<HttpResponseMessage>> action, bool enabled = true) => new(new HttpClient(new Handler(action)),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Photos:Enabled"] = enabled.ToString(), ["Cloudinary:CloudName"] = "test-cloud", ["Cloudinary:ApiKey"] = "test-key", ["Cloudinary:ApiSecret"] = Secret }).Build());
    static OwnedPhoto Photo() {
        var photo = new OwnedPhoto { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), CloudName = "test-cloud", UploadAttempts = 1 };
        photo.PublicId = $"zullo_owned/{photo.UserId:N}/{photo.Id:N}"; return photo;
    }
    static HttpResponseMessage Resource(OwnedPhoto photo, string? owner = null, string asset = "stable-asset", int width = 100) => Json(new {
        public_id = photo.PublicId, asset_id = asset, resource_type = "image", type = "upload", version = 42, format = "png", width, height = 200,
        secure_url = "https://untrusted-response.test/ignored", context = new { custom = new { owner_id = owner ?? photo.UserId.ToString("N"), upload_id = photo.Id.ToString("N") } }
    });
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    static string Signature(Dictionary<string, string> fields) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("&", fields.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}")) + Secret))).ToLowerInvariant();
    [Fact]
    public async Task UploadIsSignedOwnerBoundNonOverwritingAndUsesSanitizedCanonicalUrl()
    {
        var photo = Photo(); var calls = 0;
        var provider = Provider(async request => {
            calls++; Assert.Equal("api.cloudinary.com", request.RequestUri!.Host); Assert.Equal("https", request.RequestUri.Scheme); Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/test-cloud/image/upload", request.RequestUri.AbsolutePath);
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
            var fields = new Dictionary<string, string>(); foreach (var part in parts.Where(p => p.Key is not ("file" or "signature" or "api_key"))) fields[part.Key] = await part.Value.ReadAsStringAsync();
            Assert.Equal(photo.PublicId, fields["public_id"]); Assert.Equal("false", fields["overwrite"]);
            Assert.Equal($"owner_id={photo.UserId:N}|upload_id={photo.Id:N}", fields["context"]);
            Assert.Contains("fl_strip_profile", fields["transformation"]); Assert.Equal(Signature(fields), await parts["signature"].ReadAsStringAsync());
            Assert.Equal("test-key", await parts["api_key"].ReadAsStringAsync());
            return Resource(photo);
        });
        var result = await provider.Upload(photo, PhotoManagementTests.Image, default);
        Assert.Equal($"https://res.cloudinary.com/test-cloud/image/upload/v42/{photo.PublicId}.png", result.Url); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task LostUploadResponseIsReconciledWithoutASecondUpload()
    {
        var photo = Photo(); photo.UploadAttempts = 2;
        var provider = Provider(request => { Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("Basic", request.Headers.Authorization!.Scheme); return Task.FromResult(Resource(photo)); });
        Assert.Equal("stable-asset", (await provider.Upload(photo, PhotoManagementTests.Image, default)).AssetId);
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("asset")]
    [InlineData("namespace")]
    public async Task DeleteNeverDestroysAMismatchedResource(string mismatch)
    {
        var photo = Photo(); photo.ProviderAssetId = "stable-asset"; var calls = 0;
        if (mismatch == "namespace") photo.PublicId = "someone_else/photo";
        var provider = Provider(request => {
            calls++; Assert.Equal(HttpMethod.Get, request.Method); // POST must never happen.
            return Task.FromResult(Resource(photo, mismatch == "owner" ? Guid.NewGuid().ToString("N") : null, mismatch == "asset" ? "different-asset" : "stable-asset"));
        });
        var error = await Assert.ThrowsAsync<PhotoProviderException>(() => provider.Delete(photo, default));
        Assert.Equal("ownership_mismatch", error.Code); Assert.Equal(mismatch == "namespace" ? 0 : 1, calls);
    }
    [Fact]
    public async Task DeletionVerifiesOwnershipDeletesByImmutableIdAndConfirmsAbsence()
    {
        var photo = Photo(); photo.ProviderAssetId = "stable-asset"; var calls = 0;
        var provider = Provider(async request => {
            calls++;
            if (calls == 1) { Assert.Equal(HttpMethod.Get, request.Method); return Resource(photo); }
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            if (calls == 2) {
                Assert.Equal(HttpMethod.Delete, request.Method); Assert.EndsWith("/resources", request.RequestUri!.AbsolutePath);
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                Assert.Equal("stable-asset", body.GetProperty("asset_ids")[0].GetString());
                Assert.True(body.GetProperty("invalidate").GetBoolean());
                Assert.False(body.TryGetProperty("public_ids", out _));
                return Json(new { deleted = new Dictionary<string, string> { ["stable-asset"] = "deleted" } });
            }
            Assert.Equal(HttpMethod.Get, request.Method); Assert.EndsWith("/resources/stable-asset", request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.NotFound);
        });
        await provider.Delete(photo, default); Assert.Equal(3, calls);
    }
    [Fact]
    public async Task SuccessfulHttpDeletionWithoutConfirmedAbsenceMustRetry()
    {
        var photo = Photo(); var provider = Provider(request => Task.FromResult(request.Method == HttpMethod.Delete ? Json(new { deleted = new { } }) : Resource(photo)));
        Assert.Equal("delete_not_confirmed", (await Assert.ThrowsAsync<PhotoProviderException>(() => provider.Delete(photo, default))).Code);
    }
    [Fact]
    public async Task MissingAssetIsAnIdempotentSuccessWithoutDestroyCall()
    {
        var calls = 0; var provider = Provider(request => { calls++; Assert.Equal(HttpMethod.Get, request.Method); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); });
        await provider.Delete(Photo(), default); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task LegacyDestructionRequiresExplicitApprovalAndStableAssetIdentity()
    {
        var photo = Photo(); photo.PublicId = "zullo_profiles/old";
        var provider = Provider(request => { Assert.Equal(HttpMethod.Get, request.Method); return Task.FromResult(Resource(photo, asset: "changed")); });
        await Assert.ThrowsAsync<PhotoProviderException>(() => provider.Delete(photo, default));
        photo.LegacyApprovedAtUtc = DateTime.UtcNow; photo.LegacyApprovalDigest = "review-digest"; photo.ProviderAssetId = "original";
        Assert.Equal("ownership_mismatch", (await Assert.ThrowsAsync<PhotoProviderException>(() => provider.Delete(photo, default))).Code);
    }
    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task MalformedProviderResponseCannotProduceAnImageUrl(string body)
    {
        var provider = Provider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        Assert.Equal("invalid_provider_response", (await Assert.ThrowsAsync<PhotoProviderException>(() => provider.Upload(Photo(), PhotoManagementTests.Image, default))).Code);
    }
    [Fact]
    public async Task OversizeOutputAndDisabledConfigurationAreRejected()
    {
        var photo = Photo(); var provider = Provider(_ => Task.FromResult(Resource(photo, width: 9000)));
        Assert.Equal("invalid_image_dimensions", (await Assert.ThrowsAsync<PhotoProviderException>(() => provider.Upload(photo, PhotoManagementTests.Image, default))).Code);
        var disabled = Provider(_ => throw new Exception("No network allowed"), false);
        Assert.False(disabled.Enabled); Assert.Equal("not_configured", (await Assert.ThrowsAsync<PhotoProviderException>(() => disabled.Upload(photo, PhotoManagementTests.Image, default))).Code);
    }
}
