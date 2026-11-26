using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zullo.Api.Models;
using Zullo.Api.Services;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class SwipeLifecycleRaceTests
{
    private sealed class ChangingProvider : ICommerceVerifier
    {
        public bool Enabled => true;
        public Func<Task> Change { get; set; } = () => Task.CompletedTask;
        public async Task<VerifiedCommerce> Fetch(Guid user, CancellationToken cancellation = default)
        {
            await Change();
            return new VerifiedCommerce(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DateTime.UtcNow.AddDays(20), DateTime.UtcNow.AddDays(-1), new());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LikeRechecksActorAfterProviderWait(bool closeAccount)
    {
        var provider = new ChangingProvider();
        await using var app = await TestApp.Start(configure: services => services.AddSingleton<ICommerceVerifier>(provider));
        await app.AddSwipeProfiles(app.A, app.B);
        await using (var db = app.Db())
        {
            db.CommerceAccounts.Add(new CommerceAccount { UserId = app.A, PremiumUntilUtc = DateTime.UtcNow.AddDays(20) });
            await db.SaveChangesAsync();
        }
        var changed = false;
        provider.Change = async () =>
        {
            await using var db = app.Db();
            await db.Profiles.Where(p => p.UserId == app.A).ExecuteDeleteAsync();
            if (closeAccount) await db.Users.Where(u => u.Id == app.A).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsClosed, true));
            changed = true;
        };
        var response = await app.Client(app.A).PostAsJsonAsync("/swipe/like", new { targetUserId = app.B });
        Assert.True(changed);
        Assert.Equal(closeAccount ? HttpStatusCode.Unauthorized : HttpStatusCode.Conflict, response.StatusCode);
        await using var check = app.Db();
        Assert.Empty(await check.Likes.ToListAsync());
        Assert.Empty(await check.Matches.ToListAsync());
        Assert.Empty(await check.MatchNotifications.ToListAsync());
    }
}
