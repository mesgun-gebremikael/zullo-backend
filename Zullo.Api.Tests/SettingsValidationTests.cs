using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class SettingsValidationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"paused\":null}")]
    public async Task MissingPauseChoiceCannotReactivateAccount(string json)
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/me/account/pause", new { paused = true })).StatusCode);
        using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsync("/me/account/pause", body)).StatusCode);
        await using var db = app.Db();
        Assert.True((await db.Users.SingleAsync(u => u.Id == app.A)).IsPaused);
        Assert.False((await db.Users.SingleAsync(u => u.Id == app.B)).IsPaused);
    }

    [Fact]
    public async Task MissingReportTargetReturnsNotFoundWithoutPersistingReport()
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/reports", new {
            reportedUserId = Guid.NewGuid(), reason = "Inappropriate behaviour"
        })).StatusCode);
        await using var db = app.Db();
        Assert.Empty(await db.Reports.ToListAsync());
    }
}
