using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Zullo.Api.Controllers;
using Zullo.Api.Data;
using Zullo.Api.Dtos;
using Zullo.Api.Models;
using Zullo.Api.Services;

namespace Zullo.Api.Tests;

// Real MVC routing, authorization filters, JWT validation, controllers and a relational DB.
// Firebase startup is deliberately absent: users have no device tokens, so no push is sent.
public class ChatAuthorizationTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task OrdinaryUserCannotForceMatch(string environment)
    {
        await using var app = await TestApp.Start(environment);
        using var client = app.Client(app.A);
        var response = await client.PostAsync($"/matches/force-match?targetUserId={app.B}", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var db = app.Db();
        Assert.Empty(await db.Matches.ToListAsync());
        Assert.Empty(await db.Likes.ToListAsync());
    }

    [Theory]
    [InlineData("thread", "missing")]
    [InlineData("send", "missing")]
    [InlineData("mark-read", "missing")]
    [InlineData("thread", "invalid")]
    [InlineData("send", "invalid")]
    [InlineData("mark-read", "invalid")]
    [InlineData("thread", "expired")]
    [InlineData("send", "expired")]
    [InlineData("mark-read", "expired")]
    public async Task ChatRequiresValidAuthentication(string action, string authentication)
    {
        await using var app = await TestApp.Start();
        await app.Match();
        using var client = app.Client(app.A, authentication);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Request(client, action, app.B)).StatusCode);
        await app.AssertMessagesUnchanged();
    }

    [Theory]
    [InlineData("thread")]
    [InlineData("send")]
    [InlineData("mark-read")]
    public async Task UnmatchedUserCannotAccessChat(string action)
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(client, action, app.B)).StatusCode);
        await app.AssertMessagesUnchanged();
    }

    [Theory]
    [InlineData("thread", false)]
    [InlineData("send", false)]
    [InlineData("mark-read", false)]
    [InlineData("thread", true)]
    [InlineData("send", true)]
    [InlineData("mark-read", true)]
    public async Task BlockingAnExistingMatchDeniesBothDirections(string action, bool reverse)
    {
        await using var app = await TestApp.Start();
        await app.Match();
        // Block through the public API, then try either direction of the existing chat.
        using var blocker = app.Client(app.A);
        var block = await blocker.PostAsJsonAsync("/blocks", new { blockedUserId = app.B });
        Assert.Equal(HttpStatusCode.OK, block.StatusCode);
        using var client = app.Client(reverse ? app.B : app.A);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Request(client, action, reverse ? app.A : app.B)).StatusCode);
        await app.AssertMessagesUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedUsersCannotCreateReciprocalMatch(bool reverse)
    {
        await using var app = await TestApp.Start();
        await using (var db = app.Db())
        {
            db.Likes.Add(new Like { FromUserId = app.B, ToUserId = app.A });
            db.Blocks.Add(new Block
            {
                FromUserId = reverse ? app.B : app.A,
                BlockedUserId = reverse ? app.A : app.B
            });
            await db.SaveChangesAsync();
        }
        using var client = app.Client(app.A);
        var response = await client.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var after = app.Db();
        Assert.Empty(await after.Matches.ToListAsync());
        Assert.Single(await after.Likes.ToListAsync());
        Assert.Equal(50, (await after.Users.SingleAsync(u => u.Id == app.A)).LikesRemaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedMatchIsHiddenFromMatchList(bool reverse)
    {
        await using var app = await TestApp.Start();
        await app.Match();
        await using (var db = app.Db())
        {
            db.Blocks.Add(new Block { FromUserId = app.A, BlockedUserId = app.B });
            await db.SaveChangesAsync();
        }
        using var client = app.Client(reverse ? app.B : app.A);
        var response = await client.GetAsync("/matches");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<MatchListItemDto>>())!);
    }

    [Theory]
    [InlineData("thread")]
    [InlineData("send")]
    [InlineData("mark-read")]
    public async Task RemovedMatchImmediatelyLosesChatAccess(string action)
    {
        await using var app = await TestApp.Start();
        await app.Match();
        using var client = app.Client(app.A);
        Assert.Equal(HttpStatusCode.OK, (await Request(client, "thread", app.B)).StatusCode);
        await using (var db = app.Db())
        {
            db.Matches.RemoveRange(await db.Matches.ToListAsync());
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(client, action, app.B)).StatusCode);
        await app.AssertMessagesUnchanged();
    }

    [Fact]
    public async Task MutualLikesStillAllowSendingReadingAndMarkingRead()
    {
        await using var app = await TestApp.Start();
        using var a = app.Client(app.A);
        using var b = app.Client(app.B);
        var first = await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False((await first.Content.ReadFromJsonAsync<LikeResponseDto>())!.Matched);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(a, "send", app.B)).StatusCode);
        var second = await b.PostAsJsonAsync("/swipe/like", new { targetUserId = app.A });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await second.Content.ReadFromJsonAsync<LikeResponseDto>())!.Matched);
        var sent = await a.PostAsJsonAsync("/messages/send", new
        {
            toUserId = app.B, text = "Allowed message", clientMessageId = "positive-flow"
        });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var message = (await sent.Content.ReadFromJsonAsync<MessageDto>())!;
        Assert.Equal(app.A, message.FromUserId);
        Assert.Equal(app.B, message.ToUserId);
        var thread = await b.GetFromJsonAsync<List<MessageDto>>($"/messages/thread?otherUserId={app.A}");
        Assert.Contains(thread!, m => m.Id == message.Id && m.Text == "Allowed message");
        var read = await Request(b, "mark-read", app.A);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(1, (await read.Content.ReadFromJsonAsync<MarkReadResponseDto>())!.Updated);
        await using var db = app.Db();
        Assert.Single(await db.Matches.ToListAsync());
        Assert.NotNull((await db.Messages.SingleAsync(m => m.Id == message.Id)).ReadAtUtc);
    }

    [Fact]
    public async Task BeingMatchedToSomeoneDoesNotExposeTheirOtherConversations()
    {
        await using var app = await TestApp.Start();
        await app.Match();
        await using (var db = app.Db())
        {
            db.Matches.Add(TestApp.Pair(app.A, app.C));
            await db.SaveChangesAsync();
        }
        using var client = app.Client(app.A);
        var thread = await client.GetFromJsonAsync<List<MessageDto>>($"/messages/thread?otherUserId={app.B}");
        Assert.Single(thread!);
        Assert.Equal("existing A-B message", thread![0].Text);
        using var outsider = app.Client(app.C);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(outsider, "thread", app.B)).StatusCode);
        await Request(client, "mark-read", app.B);
        await using var after = app.Db();
        Assert.Null((await after.Messages.SingleAsync(m => m.Text == "private B-C message")).ReadAtUtc);
    }

    private static Task<HttpResponseMessage> Request(HttpClient client, string action, Guid other) =>
        action == "thread" ? client.GetAsync($"/messages/thread?otherUserId={other}") :
        action == "mark-read" ? client.PostAsync($"/messages/mark-read?otherUserId={other}", null) :
        client.PostAsJsonAsync("/messages/send", new
        {
            toUserId = other, text = "Must not be saved", clientMessageId = "unauthorized"
        });

    internal sealed class TestApp : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        private IHost _host = null!;
        public Guid A { get; } = Guid.NewGuid();
        public Guid B { get; } = Guid.NewGuid();
        public Guid C { get; } = Guid.NewGuid();
        public AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

        public static async Task<TestApp> Start(string environment = "Production")
        {
            var app = new TestApp();
            await app._connection.OpenAsync();
            await using (var db = app.Db())
            {
                await db.Database.EnsureCreatedAsync();
                db.Users.AddRange(new User { Id = app.A }, new User { Id = app.B }, new User { Id = app.C });
                db.Messages.AddRange(
                    new Message { FromUserId = app.B, ToUserId = app.A, Text = "existing A-B message" },
                    new Message { FromUserId = app.B, ToUserId = app.C, Text = "private B-C message" });
                await db.SaveChangesAsync();
            }
            app._host = await new HostBuilder().ConfigureWebHost(web => web.UseEnvironment(environment)
                .UseTestServer().ConfigureServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(MessagesController).Assembly);
                    services.AddDbContext<AppDbContext>(o => o.UseSqlite(app._connection));
                    services.AddScoped<UserRelationService>();
                    services.AddScoped<LikeLimitService>();
                    services.AddScoped<PushNotificationService>();
                    services.AddSignalR();
                    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
                    {
                        o.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
                            ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(app._key),
                            ClockSkew = TimeSpan.Zero
                        };
                    });
                    services.AddAuthorization();
                    services.AddAuthenticationProtection();
                }).Configure(builder =>
                {
                    builder.UseRouting();
                    builder.UseRateLimiter();
                    builder.UseAuthentication();
                    builder.UseAuthorization();
                    builder.UseEndpoints(e => e.MapControllers());
                })).StartAsync();
            return app;
        }

        public HttpClient Client(Guid user, string authentication = "valid")
        {
            var client = _host.GetTestClient();
            if (authentication == "missing") return client;
            var token = new JwtSecurityToken(claims: new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.ToString())
            }, expires: DateTime.UtcNow.AddMinutes(authentication == "expired" ? -1 : 5),
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                authentication == "invalid" ? "not-a-valid-jwt" : new JwtSecurityTokenHandler().WriteToken(token));
            return client;
        }

        public static Match Pair(Guid a, Guid b) => new()
        {
            UserAId = a.CompareTo(b) < 0 ? a : b, UserBId = a.CompareTo(b) < 0 ? b : a
        };

        public async Task Match()
        {
            await using var db = Db();
            db.Matches.Add(Pair(A, B));
            await db.SaveChangesAsync();
        }

        public async Task AssertMessagesUnchanged()
        {
            await using var db = Db();
            Assert.Equal(2, await db.Messages.CountAsync());
            Assert.All(await db.Messages.ToListAsync(), m => Assert.Null(m.ReadAtUtc));
        }

        public async ValueTask DisposeAsync()
        {
            if (_host != null) { await _host.StopAsync(); _host.Dispose(); }
            await _connection.DisposeAsync();
        }
    }
}
