using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Reflection;
using Zullo.Api.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Services;

namespace Zullo.Api.Controllers;

public record SaveOnboardingDraft([Range(0, long.MaxValue)] long Revision, [Range(0, 18)] int Step, Dictionary<string, JsonElement> Data);

[ApiController, Authorize, Route("me/onboarding"), EnableRateLimiting("commerce")]
public class OnboardingController(AppDbContext db) : ControllerBase
{
    private static readonly HashSet<string> TextFields = new(StringComparer.Ordinal) {
        "displayName", "gender", "interestedIn", "workStatus", "studyPlace", "studySubject", "workPlace", "jobTitle", "livePlace", "originPlace",
        "intention", "workout", "pets", "smoking", "relationshipHistory", "zodiacSign", "religion", "childrenCount", "wantChildren", "bio", "promptKey", "promptAnswer"
    };
    private static readonly Dictionary<string,int> Limits = typeof(UpsertProfileDto).GetProperties()
        .Where(p => p.GetCustomAttribute<MaxLengthAttribute>() != null)
        .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p.GetCustomAttribute<MaxLengthAttribute>()!.Length, StringComparer.Ordinal);
    private static string? Validate(Dictionary<string, JsonElement>? data)
    {
        if (data == null || data.Count > 26) return "Utkastet kunde inte sparas.";
        foreach (var (key, value) in data) {
            if (TextFields.Contains(key)) {
                var max = Limits.GetValueOrDefault(key, 120);
                if (value.ValueKind != JsonValueKind.String || (value.GetString()!.Length > max || value.GetString()!.Contains('\0'))) return "Ett svar är för långt eller ogiltigt.";
            } else if (key == "birthDate") {
                if (value.ValueKind != JsonValueKind.Null && (value.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out var date) || date > DateOnly.FromDateTime(DateTime.UtcNow) || date.Year < DateTime.UtcNow.Year - 100)) return "Kontrollera födelsedatumet.";
            } else if (key is "heightCm" or "distanceKm") {
                if (key == "heightCm" && value.ValueKind == JsonValueKind.Null) continue;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < (key == "heightCm" ? 140 : 1) || number > (key == "heightCm" ? 260 : 300)) return "Kontrollera dina val.";
            } else if (key == "photoUrls") {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 6 || value.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String || p.GetString()!.Length > 2048)) return "Välj högst sex bilder.";
                var urls = value.EnumerateArray().Select(p => p.GetString()!).ToList();
                if (urls.Distinct(StringComparer.Ordinal).Count() != urls.Count) return "Samma bild får inte läggas till flera gånger.";
            } else return "Utkastet innehåller ett okänt fält.";
        }
        return null;
    }
    private static string Canonical(Dictionary<string, JsonElement> data) => JsonSerializer.Serialize(data.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value));
    private static List<string> Photos(Dictionary<string, JsonElement> data) => data.TryGetValue("photoUrls", out var photos)
        ? photos.EnumerateArray().Select(p => p.GetString()!).ToList() : new();
    private static Dictionary<string, JsonElement> Read(OnboardingDraft draft) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(draft.DataJson)!;
    private static object View(OnboardingDraft draft, Dictionary<string, JsonElement> data, bool photosExpired = false) => new { draft.Revision, draft.Step, data, photosExpired };

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        var draft = await db.OnboardingDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.UserId == me);
        if (draft == null) return await db.Profiles.AnyAsync(p => p.UserId == me)
            ? Conflict(new { code = "profile_exists", message = "Profilen är redan färdig. Öppna din profil för att fortsätta." }) : NotFound();
        var data = Read(draft); var urls = Photos(data);
        var owned = await db.OwnedPhotos.AsNoTracking().Where(p => p.UserId == me && (p.State == PhotoStates.Attached || (p.State == PhotoStates.Ready && p.NextAttemptUtc > DateTime.UtcNow))).ToListAsync();
        var available = urls.Where(url => owned.Any(p => p.Url == url)).ToList();
        data["photoUrls"] = JsonSerializer.SerializeToElement(available);
        return Ok(View(draft, data, available.Count != urls.Count));
    }

    [HttpPut, RequestSizeLimit(65536)]
    public async Task<IActionResult> Save([FromBody] SaveOnboardingDraft input)
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        string? error;
        try { error = Validate(input.Data); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return BadRequest(new { message = "Utkastet innehåller ogiltig text." }); }
        if (error != null) return BadRequest(new { message = error });
        await using var tx = await db.Database.BeginTransactionAsync();
        await AccountLifecycleLock.Shared(db, me);
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"User\" WHERE \"Id\" = {me} FOR UPDATE");
        if (!await db.Users.AnyAsync(u => u.Id == me && !u.IsClosed)) return Unauthorized();
        if (await db.Profiles.AnyAsync(p => p.UserId == me)) return Conflict(new { code = "profile_exists", message = "Profilen finns redan. Öppna appen igen." });
        input.Data.TryAdd("photoUrls", JsonSerializer.SerializeToElement(Array.Empty<string>()));
        var json = Canonical(input.Data);
        var draft = await db.OnboardingDrafts.SingleOrDefaultAsync(d => d.UserId == me);
        if ((draft?.Revision ?? 0) != input.Revision) {
            if (draft != null && Canonical(Read(draft)) == json && draft.Step == input.Step) return Ok(View(draft, Read(draft)));
            return Conflict(new { code = "draft_conflict", message = "Profilutkastet har ändrats på en annan enhet. Öppna det igen för att fortsätta." });
        }
        var photos = await db.OwnedPhotos.Where(p => p.UserId == me).ToListAsync();
        var wanted = Photos(input.Data);
        if (wanted.Any(url => !photos.Any(p => p.Url == url && (p.State == PhotoStates.Attached || (p.State == PhotoStates.Ready && p.NextAttemptUtc > DateTime.UtcNow)))))
            return BadRequest(new { code = "draft_photo_expired", message = "En osparad bild har gått ut. Lägg till den igen.",
                validPhotoUrls = wanted.Where(url => photos.Any(p => p.Url == url && (p.State == PhotoStates.Attached || (p.State == PhotoStates.Ready && p.NextAttemptUtc > DateTime.UtcNow)))).ToList() });
        var previous = draft == null ? new List<string>() : Photos(Read(draft));
        foreach (var photo in photos.Where(p => p.State == PhotoStates.Ready)) {
            if (wanted.Contains(photo.Url ?? "")) photo.NextAttemptUtc = DateTime.UtcNow.AddDays(7);
            else if (previous.Contains(photo.Url ?? "")) { photo.State = PhotoStates.Pending; photo.NextAttemptUtc = DateTime.UtcNow; }
        }
        if (draft == null) { draft = new OnboardingDraft { UserId = me }; db.OnboardingDrafts.Add(draft); }
        draft.DataJson = json; draft.Step = input.Step; draft.Revision++; draft.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return Ok(View(draft, input.Data));
    }
}
