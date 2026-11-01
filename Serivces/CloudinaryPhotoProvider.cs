using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zullo.Api.Models;

namespace Zullo.Api.Services;

public record VerifiedPhoto(string AssetId, string PublicId, string Url);
public class PhotoProviderException(string code) : Exception(code) { public string Code { get; } = code; }
public interface IPhotoProvider
{
    bool Enabled { get; }
    string CloudName { get; }
    Task<VerifiedPhoto> Upload(OwnedPhoto photo, byte[] bytes, CancellationToken cancellation);
    Task Delete(OwnedPhoto photo, CancellationToken cancellation);
    Task<VerifiedPhoto?> InspectLegacy(string publicId, CancellationToken cancellation);
}

// All provider calls go to one fixed HTTPS host. No client URL is ever fetched.
public class CloudinaryPhotoProvider(HttpClient client, IConfiguration configuration) : IPhotoProvider
{
    public string CloudName => configuration["Cloudinary:CloudName"] ?? "dzdugvbbr";
    private string Key => configuration["Cloudinary:ApiKey"] ?? "";
    private string Secret => configuration["Cloudinary:ApiSecret"] ?? "";
    public bool Enabled => configuration.GetValue<bool>("Photos:Enabled") &&
        Regex.IsMatch(CloudName, "^[a-z0-9_-]{1,100}$") && Key.Length > 0 && Secret.Length >= 16;
    private Uri Endpoint(string path) => new($"https://api.cloudinary.com/v1_1/{CloudName}/{path}");
    private void Configured(OwnedPhoto? photo = null)
    {
        if (!Enabled) throw new PhotoProviderException("not_configured");
        if (photo != null && photo.CloudName != CloudName) throw new PhotoProviderException("cloud_mismatch");
    }
    internal static string Sign(IDictionary<string, string> fields, string secret) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("&", fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")) + secret))).ToLowerInvariant();
    private void Authenticate(Dictionary<string, string> fields)
    {
        fields["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Sign(fields, Secret);
        fields["signature"] = signature; fields["api_key"] = Key;
    }
    private async Task<JsonDocument> Body(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (!response.IsSuccessStatusCode) throw new PhotoProviderException($"provider_http_{(int)response.StatusCode}");
        try {
            await response.Content.LoadIntoBufferAsync(128 * 1024);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation), new JsonDocumentOptions { MaxDepth = 24 });
        } catch (Exception ex) when (ex is JsonException or HttpRequestException) { throw new PhotoProviderException("invalid_provider_response"); }
    }
    private VerifiedPhoto Parse(JsonElement root, string publicId, OwnedPhoto? owner = null)
    {
        try {
            if (root.GetProperty("public_id").GetString() != publicId || root.GetProperty("resource_type").GetString() != "image" || root.GetProperty("type").GetString() != "upload")
                throw new PhotoProviderException("ownership_mismatch");
            var asset = root.GetProperty("asset_id").GetString()!;
            if (string.IsNullOrWhiteSpace(asset) || asset.Length > 100) throw new PhotoProviderException("invalid_provider_response");
            if (owner != null) {
                if (owner.ProviderAssetId != null && owner.ProviderAssetId != asset) throw new PhotoProviderException("ownership_mismatch");
                if (owner.LegacyApprovedAtUtc == null) {
                    var context = root.GetProperty("context").GetProperty("custom");
                    if (context.GetProperty("owner_id").GetString() != owner.UserId.ToString("N") || context.GetProperty("upload_id").GetString() != owner.Id.ToString("N"))
                        throw new PhotoProviderException("ownership_mismatch");
                }
            }
            var version = root.GetProperty("version").GetInt64();
            var format = root.GetProperty("format").GetString();
            if (version <= 0 || format is not ("jpg" or "jpeg" or "png" or "webp")) throw new PhotoProviderException("invalid_provider_response");
            if (owner != null && owner.LegacyApprovedAtUtc == null) {
                var width = root.GetProperty("width").GetInt32(); var height = root.GetProperty("height").GetInt32();
                if (width < 1 || height < 1 || width > 1920 || height > 1920) throw new PhotoProviderException("invalid_image_dimensions");
            }
            var url = $"https://res.cloudinary.com/{CloudName}/image/upload/v{version}/{publicId}.{format}";
            return new VerifiedPhoto(asset, publicId, url);
        } catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { throw new PhotoProviderException("invalid_provider_response"); }
    }
    private async Task<VerifiedPhoto?> Inspect(string publicId, OwnedPhoto? owner, CancellationToken cancellation)
    {
        Configured(owner);
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint($"resources/image/upload/{Uri.EscapeDataString(publicId)}?context=true"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Key}:{Secret}")));
        using var response = await client.SendAsync(request, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var json = await Body(response, cancellation);
        return Parse(json.RootElement, publicId, owner);
    }
    public Task<VerifiedPhoto?> InspectLegacy(string publicId, CancellationToken cancellation)
    {
        if (!publicId.StartsWith("zullo_profiles/", StringComparison.Ordinal) || publicId.Length > 250)
            throw new PhotoProviderException("invalid_legacy_namespace");
        return Inspect(publicId, null, cancellation);
    }
    public async Task<VerifiedPhoto> Upload(OwnedPhoto photo, byte[] bytes, CancellationToken cancellation)
    {
        Configured(photo);
        var publicId = $"zullo_owned/{photo.UserId:N}/{photo.Id:N}";
        if (photo.PublicId != publicId) throw new PhotoProviderException("ownership_mismatch");
        // Reconcile a lost earlier response before sending a second upload.
        if (photo.UploadAttempts > 1) {
            var existing = await Inspect(publicId, photo, cancellation);
            if (existing != null) return existing;
        }
        var fields = new Dictionary<string, string> {
            ["public_id"] = publicId, ["overwrite"] = "false",
            ["context"] = $"owner_id={photo.UserId:N}|upload_id={photo.Id:N}",
            ["transformation"] = "c_limit,h_1920,w_1920/fl_strip_profile"
        };
        Authenticate(fields);
        using var form = new MultipartFormDataContent();
        foreach (var pair in fields) form.Add(new StringContent(pair.Value), pair.Key);
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "photo");
        using var response = await client.PostAsync(Endpoint("image/upload"), form, cancellation);
        // An overlapping provider operation may already own this exact public ID.
        if (response.StatusCode == HttpStatusCode.Conflict) return await Inspect(publicId, photo, cancellation) ?? throw new PhotoProviderException("upload_not_confirmed");
        using var json = await Body(response, cancellation);
        var result = Parse(json.RootElement, publicId, photo);
        return result;
    }
    public async Task Delete(OwnedPhoto photo, CancellationToken cancellation)
    {
        Configured(photo);
        if (photo.PublicId == null || (photo.LegacyApprovedAtUtc == null && photo.PublicId != $"zullo_owned/{photo.UserId:N}/{photo.Id:N}"))
            throw new PhotoProviderException("ownership_mismatch");
        if (photo.LegacyApprovedAtUtc != null && (photo.LegacyApprovalDigest == null || photo.ProviderAssetId == null || !photo.PublicId.StartsWith("zullo_profiles/", StringComparison.Ordinal)))
            throw new PhotoProviderException("ownership_mismatch");
        var resource = await Inspect(photo.PublicId, photo, cancellation);
        if (resource == null) return; // Idempotent already-deleted result.
        // Address the immutable asset ID, never a public ID that could be reused
        // between verification and deletion by an external provider operation.
        using var request = new HttpRequestMessage(HttpMethod.Delete, Endpoint("resources")) {
            Content = JsonContent.Create(new { asset_ids = new[] { resource.AssetId }, invalidate = true })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Key}:{Secret}")));
        using var response = await client.SendAsync(request, cancellation);
        using var json = await Body(response, cancellation);
        // Do not assume that HTTP 200 means erasure succeeded. Confirm absence
        // using that same immutable ID; a lost response is safe to retry.
        using var confirmation = new HttpRequestMessage(HttpMethod.Get, Endpoint($"resources/{Uri.EscapeDataString(resource.AssetId)}"));
        confirmation.Headers.Authorization = request.Headers.Authorization;
        using var checkedResponse = await client.SendAsync(confirmation, cancellation);
        if (checkedResponse.StatusCode != HttpStatusCode.NotFound) throw new PhotoProviderException("delete_not_confirmed");
    }
}
