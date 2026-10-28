using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zullo.Api.Dtos;
using Zullo.Api.Hubs;
using Zullo.Api.Models;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class MessageReliabilityTests
{
    private static Task<HttpResponseMessage> Send(HttpClient client, Guid to, string key, string text = "Hello") =>
        client.PostAsJsonAsync("/messages/send", new { toUserId = to, text, clientMessageId = key });

    [Fact]
    public async Task ReplayReturnsCanonicalMessageWithoutNewRowsEventsOrQuotaConsumption()
    {
        var hub = new RecordingHub();
        await using var app = await TestApp.Start(configure: s => s.AddSingleton<IHubContext<ChatHub>>(hub));
        await app.Match();
        using var client = app.Client(app.A);
        var first = await Send(client, app.B, "stable-key", "  Hello  ");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var canonical = (await first.Content.ReadFromJsonAsync<MessageDto>())!;
        for (var i = 0; i < 8; i++)
        {
            var retry = await Send(client, app.B, "stable-key");
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            var message = (await retry.Content.ReadFromJsonAsync<MessageDto>())!;
            Assert.Equal(canonical.Id, message.Id);
            Assert.Equal(canonical.Sequence, message.Sequence);
            Assert.Equal(canonical.CreatedAtUtc, message.CreatedAtUtc);
        }
        Assert.Equal(1, hub.EventCount);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, app.B, "second-key")).StatusCode);
        await using var db = app.Db();
        Assert.Single(await db.Messages.Where(m => m.ClientMessageId == "stable-key").ToListAsync());
    }

    [Fact]
    public async Task KeyReuseForDifferentContentIsConflictAndCannotEditDeliveredMessage()
    {
        await using var app = await TestApp.Start();
        await app.Match();
        using var client = app.Client(app.A);
        await Send(client, app.B, "stable-key", "Original");
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, app.B, "stable-key", "Changed")).StatusCode);
        await using var db = app.Db();
        Assert.Equal("Original", (await db.Messages.SingleAsync(m => m.ClientMessageId == "stable-key")).Text);
    }

    [Fact]
    public async Task BlockRevokesReplayAndPagedHistoryAccess()
    {
        await using var app = await TestApp.Start();
        await app.Match();
        using var client = app.Client(app.A);
        await Send(client, app.B, "stable-key");
        await using (var db = app.Db())
        {
            db.Blocks.Add(new Block { FromUserId = app.B, BlockedUserId = app.A });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, app.B, "stable-key")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/messages/thread-page?otherUserId={app.B}")).StatusCode);
    }

    [Fact]
    public async Task FailedLiveEventDoesNotTurnPersistedDeliveryOrReadIntoFailure()
    {
        var hub = new RecordingHub { Fail = true };
        await using var app = await TestApp.Start(configure: s => s.AddSingleton<IHubContext<ChatHub>>(hub));
        await app.Match();
        using var a = app.Client(app.A);
        using var b = app.Client(app.B);
        var sent = await Send(a, app.B, "network-failure");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsync($"/messages/mark-read?otherUserId={app.A}", null)).StatusCode);
        var retry = await Send(a, app.B, "network-failure");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.NotNull((await retry.Content.ReadFromJsonAsync<MessageDto>())!.ReadAtUtc);
        await using var db = app.Db();
        Assert.Single(await db.Messages.Where(m => m.ClientMessageId == "network-failure").ToListAsync());
    }

    [Fact]
    public async Task HistoricalDuplicateClientIdsArePreservedAndReplayIsDeterministic()
    {
        await using var app = await TestApp.Start();
        await app.Match();
        await using (var db = app.Db())
        {
            db.Messages.AddRange(Enumerable.Range(0, 2).Select(i => new Message
            {
                FromUserId = app.A, ToUserId = app.B, Text = "Legacy", ClientMessageId = "old-key",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-2).AddSeconds(i)
            }));
            await db.SaveChangesAsync();
        }
        using var client = app.Client(app.A);
        var response = await Send(client, app.B, "old-key", "Legacy");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var check = app.Db();
        var rows = await check.Messages.Where(m => m.ClientMessageId == "old-key").OrderBy(m => m.CreatedAtUtc).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].Id, (await response.Content.ReadFromJsonAsync<MessageDto>())!.Id);
        Assert.All(rows, m => Assert.Null(m.DeliveryKey));
    }

    [Fact]
    public async Task DatabaseRejectsDuplicateNewDeliveryKeysButAllowsDifferentConversations()
    {
        await using var app = await TestApp.Start();
        await using var db = app.Db();
        db.Messages.Add(new Message { FromUserId = app.A, ToUserId = app.B, Text = "1", ClientMessageId = "key", DeliveryKey = "key" });
        await db.SaveChangesAsync();
        db.Messages.Add(new Message { FromUserId = app.A, ToUserId = app.B, Text = "2", ClientMessageId = "key", DeliveryKey = "key" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Messages.Add(new Message { FromUserId = app.A, ToUserId = app.C, Text = "Other", ClientMessageId = "key", DeliveryKey = "key" });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Messages.CountAsync(m => m.DeliveryKey == "key"));
    }

    [Theory]
    [InlineData("take=0")]
    [InlineData("take=101")]
    [InlineData("before=invalid")]
    [InlineData("before=e30")]
    [InlineData("before=e30&after=e30")]
    public async Task PagingRejectsInvalidLimitsAndCursors(string query)
    {
        await using var app = await TestApp.Start();
        await app.Match();
        using var client = app.Client(app.A);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/messages/thread-page?otherUserId={app.B}&{query}")).StatusCode);
    }

    [Fact]
    public async Task HistoryIsBoundedStableAndIsolatedEvenWithEqualTimestamps()
    {
        await using var app = await TestApp.Start();
        await app.Match();
        var time = DateTime.UtcNow.AddDays(-1);
        await using (var db = app.Db())
        {
            db.Messages.AddRange(Enumerable.Range(0, 125).Select(i => new Message
            {
                FromUserId = app.A, ToUserId = app.B, Text = $"Message {i}", CreatedAtUtc = time
            }));
            await db.SaveChangesAsync();
        }
        using var client = app.Client(app.A);
        var ids = new HashSet<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await client.GetFromJsonAsync<MessagePageDto>($"/messages/thread-page?otherUserId={app.B}&take=17" +
                (cursor == null ? "" : $"&before={cursor}")))!;
            Assert.InRange(page.Messages.Count, 1, 17);
            Assert.Equal(page.Messages.OrderBy(m => m.Sequence).Select(m => m.Id), page.Messages.Select(m => m.Id));
            foreach (var message in page.Messages)
            {
                Assert.True(ids.Add(message.Id), "Duplicate across pages");
                Assert.DoesNotContain("private B-C", message.Text);
            }
            cursor = page.NextCursor;
            Assert.Equal(page.HasMore, cursor != null);
            pages++;
        } while (cursor != null && pages < 20);
        Assert.Null(cursor);
        Assert.Equal(126, ids.Count); // Includes the fixture's existing A-B message only.
    }

    [Fact]
    public async Task ForwardCatchupRetrievesEveryMissedMessageAcrossMultiplePages()
    {
        await using var app = await TestApp.Start();
        await app.Match();
        using var client = app.Client(app.A);
        var initial = (await client.GetFromJsonAsync<MessagePageDto>($"/messages/thread-page?otherUserId={app.B}"))!;
        await using (var db = app.Db())
        {
            db.Messages.AddRange(Enumerable.Range(0, 123).Select(i => new Message
            {
                FromUserId = app.B, ToUserId = app.A, Text = $"Missed {i}",
                // Delivery sequence, rather than timestamps, defines sync order.
                CreatedAtUtc = DateTime.UtcNow.AddDays(-3)
            }));
            await db.SaveChangesAsync();
        }
        var cursor = initial.LatestCursor;
        var seen = new HashSet<Guid>();
        var more = true;
        for (var i = 0; more && i < 20; i++)
        {
            var page = (await client.GetFromJsonAsync<MessagePageDto>($"/messages/thread-page?otherUserId={app.B}&take=19&after={cursor}"))!;
            foreach (var message in page.Messages) Assert.True(seen.Add(message.Id));
            cursor = page.NextCursor;
            more = page.HasMore;
        }
        Assert.False(more);
        Assert.Equal(123, seen.Count);
    }

    [Fact]
    public async Task NewDeliveryQuotaDoesNotRejectCanonicalRetries()
    {
        await using var app = await TestApp.Start();
        await app.Match();
        using var client = app.Client(app.A);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await Send(client, app.B, $"quota-{i}")).StatusCode);
        var limited = await Send(client, app.B, "quota-fourth");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, app.B, "quota-0")).StatusCode);
        await using var db = app.Db();
        Assert.Equal(3, await db.Messages.CountAsync(m => m.DeliveryKey != null && m.DeliveryKey.StartsWith("quota-")));
    }

    [Fact]
    public async Task ReceiptMetadataDoesNotAcknowledgeMessagesSentAfterTheRead()
    {
        await using var app = await TestApp.Start();
        await app.Match();
        using var a = app.Client(app.A);
        using var b = app.Client(app.B);
        var first = (await (await Send(a, app.B, "before-read")).Content.ReadFromJsonAsync<MessageDto>())!;
        var read = await b.PostAsync($"/messages/mark-read?otherUserId={app.A}", null);
        var receipt = (await read.Content.ReadFromJsonAsync<MarkReadResponseDto>())!;
        Assert.Equal(first.Sequence, receipt.ReadThroughSequence);
        var later = (await (await Send(a, app.B, "after-read")).Content.ReadFromJsonAsync<MessageDto>())!;
        var page = (await a.GetFromJsonAsync<MessagePageDto>($"/messages/thread-page?otherUserId={app.B}"))!;
        Assert.Equal(first.Sequence, page.OutgoingReadThrough);
        Assert.NotNull(page.OutgoingReadAtUtc);
        Assert.Null(page.Messages.Single(m => m.Id == later.Id).ReadAtUtc);
    }

    [Fact]
    public async Task PagedHistoryRejectsUnmatchedAndUnauthenticatedUsers()
    {
        await using var app = await TestApp.Start();
        using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await a.GetAsync($"/messages/thread-page?otherUserId={app.B}")).StatusCode);
        using var anonymous = app.Client(app.A);
        anonymous.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/messages/thread-page?otherUserId={app.B}")).StatusCode);
    }

    private sealed class RecordingHub : IHubContext<ChatHub>, IHubClients, IClientProxy, IGroupManager
    {
        public bool Fail { get; init; }
        public int EventCount { get; private set; }
        public IHubClients Clients => this;
        public IGroupManager Groups => this;
        public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> _) => this;
        public IClientProxy Client(string _) => this;
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> _) => this;
        public IClientProxy Group(string _) => this;
        public IClientProxy GroupExcept(string _, IReadOnlyList<string> __) => this;
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> _) => this;
        public IClientProxy User(string _) => this;
        public IClientProxy Users(IReadOnlyList<string> _) => this;
        public Task AddToGroupAsync(string _, string __, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string _, string __, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            EventCount++;
            return Fail ? Task.FromException(new IOException("Simulated connection loss")) : Task.CompletedTask;
        }
    }
}
