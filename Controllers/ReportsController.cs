using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Dtos;
using Zullo.Api.Services;

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

   

    // POST /reports
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReportDto dto)
    {
        Guid meId;
        try { meId = CurrentUserService.GetUserIdOrThrow(User); }
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