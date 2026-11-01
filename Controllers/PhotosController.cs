using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zullo.Api.Services;

namespace Zullo.Api.Controllers;

[ApiController, Authorize, Route("me/photos")]
public class PhotosController(PhotoLifecycle photos, IPhotoProvider provider) : ControllerBase
{
    [HttpGet("availability")]
    public IActionResult Availability() => Ok(new { available = provider.Enabled, maxBytes = PhotoLifecycle.MaxBytes });
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        if (!CurrentUserService.TryGetUserId(User, out var user)) return Unauthorized();
        var photo = await photos.Read(user, id);
        return photo == null ? NotFound() : Ok(photo);
    }
    [HttpPut("{id:guid}"), Consumes("multipart/form-data"), EnableRateLimiting("photos")]
    [RequestSizeLimit(PhotoLifecycle.MaxBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = PhotoLifecycle.MaxBytes + 65536)]
    public async Task<IActionResult> Upload(Guid id, [FromForm] IFormFile file, CancellationToken cancellation)
    {
        if (!CurrentUserService.TryGetUserId(User, out var user)) return Unauthorized();
        if (file == null || file.Length < 12 || file.Length > PhotoLifecycle.MaxBytes)
            return BadRequest(new { code = "invalid_image", message = "Välj en JPG-, PNG- eller WebP-bild på högst 10 MB." });
        using var buffer = new MemoryStream();
        await using (var input = file.OpenReadStream()) {
            var chunk = new byte[65536]; int read;
            while ((read = await input.ReadAsync(chunk, cancellation)) > 0) {
                if (buffer.Length + read > PhotoLifecycle.MaxBytes) return BadRequest(new { code = "image_too_large", message = "Bilden är för stor. Max 10 MB." });
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellation);
            }
        }
        try {
            var result = await photos.Upload(user, id, buffer.ToArray(), cancellation);
            return result.State == Zullo.Api.Models.PhotoStates.Uploading ? Accepted(result) : Ok(result);
        } catch (PhotoOperationException ex) { return StatusCode(ex.Status, new { code = ex.Code, message = ex.Message }); }
    }
}
