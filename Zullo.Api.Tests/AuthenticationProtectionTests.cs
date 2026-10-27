using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zullo.Api.Controllers;
using Zullo.Api.Data;
using Zullo.Api.Dtos;
using Zullo.Api.Models;
using Zullo.Api.Services;

namespace Zullo.Api.Tests;

public class AuthenticationProtectionTests
{
    [Theory]
    [InlineData("known@example.test")]
    [InlineData("unknown@example.test")]
    public async Task AccountLimitIsSharedAcrossRequestsAndClientAddresses(string email)
    {
        await using var app = await AuthApp.Start();
        for (var i = 0; i < 5; i++)
        {
            using var client = app.Client($"192.0.2.{i + 1}");
            var reply = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        }
        using var nextClient = app.Client("192.0.2.99");
        var denied = await nextClient.PostAsJsonAsync("/api/auth/login", new
        {
            email = email.ToUpperInvariant(), password = "correct-password"
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.InRange(denied.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        Assert.DoesNotContain("token", await denied.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IpLimitCoversLoginAndRegistrationAndCannotBeBypassedWithForwardedHeader()
    {
        await using var app = await AuthApp.Start();
        using var client = app.Client("192.0.2.1");
        for (var i = 0; i < 20; i++)
        {
            // Invalid bodies still consume ingress permits before MVC validation.
            var response = await client.PostAsJsonAsync(i % 2 == 0 ? "/api/auth/login" : "/api/auth/register", new { });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.3");
        var denied = await client.PostAsJsonAsync("/api/auth/register", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.NotNull(denied.Headers.RetryAfter);
        using var otherIp = app.Client("192.0.2.2");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await otherIp.PostAsJsonAsync("/api/auth/login", new { })).StatusCode);
    }

    [Fact]
    public async Task InvalidCredentialsHaveTheSameResponseForMissingPasswordAndUnknownAccounts()
    {
        await using var app = await AuthApp.Start();
        using var client = app.Client("192.0.2.1");
        string? expected = null;
        foreach (var email in new[] { "known@example.test", "passwordless@example.test", "unknown@example.test" })
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            expected ??= body;
            Assert.Equal(expected, body);
        }
    }

    [Fact]
    public async Task ValidLoginAndRegistrationStillReturnTokens()
    {
        await using var app = await AuthApp.Start();
        using var client = app.Client("192.0.2.1");
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "KNOWN@example.test", password = "correct-password"
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loggedIn = (await login.Content.ReadFromJsonAsync<AuthResponseDto>())!;
        Assert.False(string.IsNullOrWhiteSpace(loggedIn.Token));
        Assert.NotEqual(Guid.Empty, loggedIn.UserId);
        var registration = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "new@example.test", password = "new-password-123"
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var registered = (await registration.Content.ReadFromJsonAsync<AuthResponseDto>())!;
        Assert.False(string.IsNullOrWhiteSpace(registered.Token));
        Assert.NotEqual(loggedIn.UserId, registered.UserId);
    }

    [Fact]
    public async Task ParallelAttemptsCannotExceedAccountBudget()
    {
        using var limiter = new LoginAttemptService();
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
            limiter.TryAcquire("same@example.test", out var retry))));
        Assert.Equal(5, results.Count(allowed => allowed));
        Assert.True(limiter.TryAcquire("other@example.test", out _));
    }

    [Fact]
    public async Task AccountBudgetRecoversAfterWindowWithoutPermanentlyLockingAccount()
    {
        using var limiter = new LoginAttemptService(1, TimeSpan.FromMilliseconds(50));
        Assert.True(limiter.TryAcquire(" user@example.test ", out _));
        Assert.False(limiter.TryAcquire("USER@example.test", out _));
        // Short test-only window; bounded wait for the production replenishment mechanism.
        var recovered = false;
        for (var i = 0; i < 100 && !recovered; i++)
        {
            await Task.Delay(20);
            recovered = limiter.TryAcquire("user@example.test", out _);
        }
        Assert.True(recovered);
    }

    private sealed class AuthApp : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private IHost _host = null!;

        public static async Task<AuthApp> Start()
        {
            var app = new AuthApp();
            await app._connection.OpenAsync();
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(app._connection).Options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Users.AddRange(new User { Email = "known@example.test", PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-password", 4) },
                    new User { Email = "passwordless@example.test" });
                await db.SaveChangesAsync();
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }).Build();
            app._host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(configuration);
                services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
                services.AddDbContext<AppDbContext>(o => o.UseSqlite(app._connection));
                services.AddAuthenticationProtection();
            }).Configure(builder =>
            {
                // Only this test host accepts an address override, not application code.
                builder.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-IP"].ToString());
                    return next(context);
                });
                builder.UseRouting();
                builder.UseRateLimiter();
                builder.UseEndpoints(endpoints => endpoints.MapControllers());
            })).StartAsync();
            return app;
        }

        public HttpClient Client(string ip)
        {
            var client = _host.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Test-IP", ip);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            if (_host != null) { await _host.StopAsync(); _host.Dispose(); }
            await _connection.DisposeAsync();
        }
    }
}
