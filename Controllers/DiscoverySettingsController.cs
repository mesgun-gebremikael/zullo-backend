using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Services;
namespace Zullo.Api.Controllers;

public class DiscoverySettingsUpdate : IValidatableObject
{
    [MaxLength(30)] public string? InterestedIn { get; set; }
    public bool? AutomaticLocation { get; set; }
    public bool? ExpandDistance { get; set; }
    public bool? ExpandAge { get; set; }
    [Range(18,100)] public int? MinAge { get; set; }
    [Range(18,100)] public int? MaxAge { get; set; }
    [Range(1,300)] public int? RadiusKm { get; set; }
    [Range(-90,90)] public double? Lat { get; set; }
    [Range(-180,180)] public double? Lng { get; set; }
    [MaxLength(150)] public string? LocationName { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Lat.HasValue != Lng.HasValue || (Lat.HasValue && (!double.IsFinite(Lat.Value) || !double.IsFinite(Lng!.Value))))
            yield return new ValidationResult("Supply a valid latitude and longitude together.");
        if (InterestedIn != null && !new[] { "", "Alla", "Män", "Kvinnor" }.Contains(InterestedIn.Trim()))
            yield return new ValidationResult("Choose a supported discovery preference.");
        if (LocationName?.Contains('\0') == true) yield return new ValidationResult("Invalid location name.");
    }
}

[ApiController, Authorize, Route("me/discovery-settings")]
public class DiscoverySettingsController(AppDbContext db) : ControllerBase
{
    private static object View(User u, Profile p, bool locationChanged = false) => new { locationChanged, radiusKm = u.MatchRadiusKm, minAge = u.DiscoveryMinAge,
        maxAge = u.DiscoveryMaxAge, p.InterestedIn, u.ExpandDistance, u.ExpandAge, p.AutomaticLocation,
        locationName = p.DiscoveryLocationName, p.LocationUpdatedAtUtc };
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        var u = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == me && !u.IsClosed);
        var p = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == me);
        return u == null || p == null ? NotFound() : Ok(View(u,p));
    }
    [HttpPatch]
    public async Task<IActionResult> Patch(DiscoverySettingsUpdate value)
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        await using var tx = await db.Database.BeginTransactionAsync();
        await AccountLifecycleLock.Shared(db, me);
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"User\" WHERE \"Id\" = {me} FOR UPDATE");
        var u = await db.Users.SingleOrDefaultAsync(u => u.Id == me && !u.IsClosed);
        var p = await db.Profiles.SingleOrDefaultAsync(p => p.UserId == me);
        if (u == null || p == null) return NotFound();
        var min = value.MinAge ?? u.DiscoveryMinAge; var max = value.MaxAge ?? u.DiscoveryMaxAge;
        if (min > max) return BadRequest(new { message = "Kontrollera åldersintervallet." });
        if (value.MinAge.HasValue || value.MaxAge.HasValue) u.DiscoveryAgeConfigured = true;
        u.DiscoveryMinAge = min; u.DiscoveryMaxAge = max;
        if (value.InterestedIn != null) p.InterestedIn = value.InterestedIn.Trim();
        if (value.RadiusKm is int radius) u.MatchRadiusKm = radius;
        if (value.ExpandDistance is bool distance) u.ExpandDistance = distance;
        if (value.ExpandAge is bool age) u.ExpandAge = age;
        if (value.AutomaticLocation is bool automatic) p.AutomaticLocation = automatic;
        var locationChanged = false;
        if (value.Lat.HasValue) {
            if (!p.AutomaticLocation) return BadRequest(new { message = "Aktivera plats för att uppdatera den." });
            locationChanged = p.LocationUpdatedAtUtc == null || GeoService.DistanceKm(p.Lat, p.Lng, value.Lat.Value, value.Lng!.Value) >= 1;
            p.Lat = value.Lat.Value; p.Lng = value.Lng!.Value;
            p.DiscoveryLocationName = string.IsNullOrWhiteSpace(value.LocationName) ? "Nuvarande plats" : value.LocationName.Trim();
            p.LocationUpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(); await tx.CommitAsync(); return Ok(View(u,p,locationChanged));
    }
}
