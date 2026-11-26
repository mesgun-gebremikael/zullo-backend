using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zullo.Api.Models;
using Zullo.Api.Services;
using static Zullo.Api.Tests.ChatAuthorizationTests;

namespace Zullo.Api.Tests;

public class MatchNotificationTests
{
    sealed class Push : PushNotificationService
    {
        public readonly List<(string Token, string Peer, string Recipient, string Match)> Sent = new();
        public bool Fail;
        public int MessagesSent;
        public override Task SendMessageNotificationAsync(string token, string sender, string name, string text, string? photo, string recipient, CancellationToken cancellationToken = default)
        { MessagesSent++; return Task.CompletedTask; }
        public override Task SendMatchNotificationAsync(string token, string peer, string recipient, string match, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("sensitive-device-token-must-not-be-logged");
            Sent.Add((token, peer, recipient, match));
            return Task.CompletedTask;
        }
    }

    static async Task<Match> Queue(TestApp app, int recipients = 1)
    {
        await using var db = app.Db();
        var users = await db.Users.ToListAsync();
        users.Single(u => u.Id == app.A).DeviceToken = "device-A";
        users.Single(u => u.Id == app.B).DeviceToken = "device-B";
        var match = TestApp.Pair(app.A, app.B);
        db.Matches.Add(match);
        db.MatchNotifications.Add(new() { MatchId = match.Id, RecipientUserId = app.A, OtherUserId = app.B });
        if (recipients == 2) db.MatchNotifications.Add(new() { MatchId = match.Id, RecipientUserId = app.B, OtherUserId = app.A });
        await db.SaveChangesAsync();
        return match;
    }

    [Fact]
    public async Task ReciprocalLikesCommitOneMatchAndTwoUniqueOutboxEvents()
    {
        await using var app = await TestApp.Start();
        await app.AddSwipeProfiles(app.A, app.B);
        Assert.Equal(HttpStatusCode.OK, (await app.Client(app.A).PostAsJsonAsync("/swipe/like", new { targetUserId = app.B })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client(app.B).PostAsJsonAsync("/swipe/like", new { targetUserId = app.A })).StatusCode);
        await using (var db = app.Db())
        {
            Assert.Single(await db.Matches.ToListAsync());
            var events = await db.MatchNotifications.ToListAsync();
            Assert.Equal(2, events.Count);
            Assert.Equal(new[] { app.A, app.B }.Order(), events.Select(n => n.RecipientUserId).Order());
            // Bypass the intentionally independent swipe rate-limit window.
            foreach (var like in await db.Likes.ToListAsync()) like.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await app.Client(app.A).PostAsJsonAsync("/swipe/like", new { targetUserId = app.B })).StatusCode);
        await using (var db = app.Db()) Assert.Equal(2, await db.MatchNotifications.CountAsync());
    }

    [Fact]
    public async Task CommittedMatchDeliversToBothRecipientsAndDoesNotRepeat()
    {
        await using var app = await TestApp.Start();
        var match = await Queue(app, 2);
        var push = new Push();
        await using var db = app.Db();
        var worker = new MatchNotificationDispatcher(db, push, NullLogger<MatchNotificationDispatcher>.Instance);
        Assert.Equal(2, await worker.DispatchBatch());
        Assert.Equal(0, await worker.DispatchBatch());
        Assert.Equal(2, push.Sent.Count);
        Assert.All(push.Sent, s => Assert.Equal(match.Id.ToString(), s.Match));
        Assert.Contains(push.Sent, s => s.Token == "device-A" && s.Peer == app.B.ToString() && s.Recipient == app.A.ToString());
        Assert.Contains(push.Sent, s => s.Token == "device-B" && s.Peer == app.A.ToString() && s.Recipient == app.B.ToString());
    }

    [Fact]
    public async Task FailedPushRetriesTheSameCommittedEvent()
    {
        await using var app = await TestApp.Start();
        var match = await Queue(app);
        var push = new Push { Fail = true };
        await using var db = app.Db();
        var worker = new MatchNotificationDispatcher(db, push, NullLogger<MatchNotificationDispatcher>.Instance);
        Assert.Equal(0, await worker.DispatchBatch());
        var row = await db.MatchNotifications.SingleAsync();
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.SentAtUtc);
        Assert.True(row.NextAttemptAtUtc > DateTime.UtcNow);
        Assert.Equal(0, await worker.DispatchBatch());
        row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        push.Fail = false;
        Assert.Equal(1, await worker.DispatchBatch());
        Assert.Equal(match.Id.ToString(), Assert.Single(push.Sent).Match);
    }

    [Theory]
    [InlineData("block")]
    [InlineData("closed")]
    [InlineData("expired")]
    public async Task RevokedOrExpiredMatchNoticeCannotLeak(string reason)
    {
        await using var app = await TestApp.Start();
        await Queue(app);
        await using var db = app.Db();
        if (reason == "block") db.Blocks.Add(new() { FromUserId = app.B, BlockedUserId = app.A });
        if (reason == "closed") (await db.Users.FindAsync(app.B))!.IsClosed = true;
        if (reason == "expired") (await db.MatchNotifications.SingleAsync()).CreatedAtUtc = DateTime.UtcNow.AddDays(-2);
        await db.SaveChangesAsync();
        var push = new Push();
        Assert.Equal(0, await new MatchNotificationDispatcher(db, push, NullLogger<MatchNotificationDispatcher>.Instance).DispatchBatch());
        Assert.Empty(push.Sent);
        Assert.NotNull((await db.MatchNotifications.SingleAsync()).CancelledAtUtc);
    }

    [Fact]
    public async Task PausingDiscoveryDoesNotSilenceAnAlreadyCommittedMatch()
    {
        await using var app = await TestApp.Start();
        await Queue(app, 2);
        await using var db = app.Db();
        (await db.Users.FindAsync(app.A))!.IsPaused = true;
        await db.SaveChangesAsync();
        var push = new Push();
        Assert.Equal(2, await new MatchNotificationDispatcher(db, push, NullLogger<MatchNotificationDispatcher>.Instance).DispatchBatch());
        Assert.Equal(2, push.Sent.Count);
    }

    [Fact]
    public async Task UnmatchingCascadesPendingNotices()
    {
        await using var app = await TestApp.Start();
        await Queue(app, 2);
        await using var db = app.Db();
        db.Matches.Remove(await db.Matches.SingleAsync());
        await db.SaveChangesAsync();
        Assert.Empty(await db.MatchNotifications.ToListAsync());
    }

    [Fact]
    public async Task MissingTokenWaitsForRegistrationWithoutDroppingMatch()
    {
        await using var app = await TestApp.Start();
        await Queue(app);
        await using var db = app.Db();
        (await db.Users.FindAsync(app.A))!.DeviceToken = null;
        await db.SaveChangesAsync();
        var push = new Push();
        var worker = new MatchNotificationDispatcher(db, push, NullLogger<MatchNotificationDispatcher>.Instance);
        Assert.Equal(0, await worker.DispatchBatch());
        var row = await db.MatchNotifications.SingleAsync();
        Assert.Null(row.SentAtUtc); Assert.Null(row.CancelledAtUtc);
        Assert.Empty(push.Sent);
        (await db.Users.FindAsync(app.A))!.DeviceToken = "late-token";
        row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        Assert.Equal(1, await worker.DispatchBatch());
        Assert.Equal("late-token", Assert.Single(push.Sent).Token);
    }

    [Fact]
    public async Task PausedDiscoveryDoesNotSilenceExistingMatchedMessageNotifications()
    {
        var push = new Push();
        await using var app = await TestApp.Start(configure: services => services.AddSingleton<PushNotificationService>(push));
        await app.Match();
        await using (var db = app.Db())
        {
            var recipient = (await db.Users.FindAsync(app.B))!;
            recipient.IsPaused = true;
            recipient.DeviceToken = "device-B";
            await db.SaveChangesAsync();
        }
        var response = await app.Client(app.A).PostAsJsonAsync("/messages/send", new { toUserId = app.B, text = "Existing chats stay usable", clientMessageId = "paused-message" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, push.MessagesSent);
        var retry = await app.Client(app.A).PostAsJsonAsync("/messages/send", new { toUserId = app.B, text = "Existing chats stay usable", clientMessageId = "paused-message" });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, push.MessagesSent);
    }

    [Fact]
    public void MatchAndSuperlikePayloadsUseNativeResourcesAndStableIds()
    {
        var match = PushNotificationService.CreateMatch("token", "peer", "recipient", "pair");
        Assert.Null(match.Notification);
        Assert.Null(match.Android.Notification.Body);
        Assert.Equal("zullo_match_title", match.Android.Notification.TitleLocKey);
        Assert.Equal("zullo_match_body", match.Android.Notification.BodyLocKey);
        Assert.Equal("zullo_match_body", match.Apns.Aps.Alert.LocKey);
        Assert.Equal("default", match.Apns.Aps.Sound);
        Assert.Equal("match-pair", match.Data["eventId"]);
        Assert.Equal("match-pair", match.Android.CollapseKey);
        Assert.Equal("recipient", match.Data["recipientUserId"]);
        Assert.DoesNotContain("senderName", match.Data.Keys);
        var superlike = PushNotificationService.CreateSuperLike("token", "recipient", "like-id");
        Assert.Null(superlike.Notification);
        Assert.Null(superlike.Android.Notification.Body);
        Assert.Equal("zullo_superlike_body", superlike.Android.Notification.BodyLocKey);
        Assert.Equal("zullo_superlike_body", superlike.Apns.Aps.Alert.LocKey);
    }
}
