using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ReportsController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetMeIdOrThrow()
    {
        var meIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(meIdStr) || !Guid.TryParse(meIdStr, out var meId))
            throw new UnauthorizedAccessException("Missing/invalid user id in token.");
        return meId;
    }

    public class CreateReportDto
    {
        public Guid ReportedUserId { get; set; }
        public string Reason { get; set; } = "";
    }

    // POST /reports
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReportDto dto)
    {
        Guid meId;
        try { meId = GetMeIdOrThrow(); }
        catch { return Unauthorized(); }

        if (dto.ReportedUserId == Guid.Empty)
            return BadRequest("reportedUserId is required.");

        if (dto.ReportedUserId == meId)
            return BadRequest("You cannot report yourself.");

        if (string.IsNullOrWhiteSpace(dto.Reason))
            return BadRequest("reason is required.");

        var report = new Report
        {
            FromUserId = meId,
            ReportedUserId = dto.ReportedUserId,
            Reason = dto.Reason.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Reports.Add(report);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Report saved",
            report.Id
        });
    }
}