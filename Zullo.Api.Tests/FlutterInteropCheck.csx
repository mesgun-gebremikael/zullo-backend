using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Zullo.Api.Controllers;
using Zullo.Api.Data;
using Zullo.Api.Hubs;
using Zullo.Api.Models;
using Zullo.Api.Services;

// Disposable local integration host, never Program.Main or production Firebase.
NpgsqlConnection.GlobalTypeMapper.EnableDynamicJson();
var frontend = Path.GetFullPath(args[0]);
var fixture = Path.GetFullPath(args[1]);
var adminString = Environment.GetEnvironmentVariable("ZULLO_CHAT_TEST_ADMIN_CONNECTION")
    ?? "Host=127.0.0.1;Port=5432;Database=zullo;Username=zullo";
var database = "zullo_interop_" + Guid.NewGuid().ToString("N");
var connection = new NpgsqlConnectionStringBuilder(adminString) { Database = database }.ConnectionString;
var key = RandomNumberGenerator.GetBytes(32);
var a = new User { Email = "interop-a@example.test" };
var b = new User { Email = "interop-b@example.test" };
var c = new User { Email = "interop-c@example.test" };
await using var admin = new NpgsqlConnection(adminString);
await admin.OpenAsync();
await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin).ExecuteNonQueryAsync();
IHost? host = null;
try
{
    await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options))
    {
        await db.Database.MigrateAsync();
        db.Users.AddRange(a, b, c);
        foreach (var user in new[] { a, b, c })
            db.Profiles.Add(new Profile { UserId = user.Id, DisplayName = "Synthetic interop user", Age = 25,
                Gender = "Man", IsVisible = true, PhotoUrls = new() });
        db.Matches.Add(new Match { UserAId = a.Id.CompareTo(b.Id) < 0 ? a.Id : b.Id,
            UserBId = a.Id.CompareTo(b.Id) < 0 ? b.Id : a.Id });
        await db.SaveChangesAsync();
    }
    host = await new HostBuilder().ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Critical))
        .ConfigureWebHost(web => web.UseKestrel().UseUrls("http://127.0.0.1:0").ConfigureServices(s =>
        {
            s.AddControllers().AddApplicationPart(typeof(MessagesController).Assembly);
            s.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
            s.AddScoped<UserRelationService>(); s.AddScoped<LikeLimitService>(); s.AddScoped<PushNotificationService>();
            s.AddSingleton<MessageSendLimiter>(); s.AddAuthenticationProtection();
            s.AddSignalR(); s.AddSingleton<IUserIdProvider, NameIdentifierUserIdProvider>();
            s.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
            {
                o.Events = new JwtBearerEvents { OnMessageReceived = ChatBearerAuthentication.ReadHubToken };
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(key), ClockSkew = TimeSpan.Zero
                };
            });
            s.AddAuthorization();
        }).Configure(app =>
        {
            app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(e => { e.MapControllers(); e.MapHub<ChatHub>("/hubs/chat"); });
        })).StartAsync();
    var url = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    string Token(Guid id) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        claims: new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) }, expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)));
    await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { a = a.Id, b = b.Id, c = c.Id, tokenA = Token(a.Id), tokenB = Token(b.Id) }));
    var start = new ProcessStartInfo("flutter") { WorkingDirectory = frontend, UseShellExecute = false };
    foreach (var argument in new[] { "test", "test/integration_backend_interop.dart", "--reporter", "expanded", $"--dart-define=ZULLO_API_URL={url}" })
        start.ArgumentList.Add(argument);
    start.Environment["ZULLO_INTEROP_FIXTURE"] = fixture;
    using var child = Process.Start(start)!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try { await child.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
    if (child.ExitCode != 0) throw new Exception("Flutter/backend interop check failed.");
    Console.WriteLine("PASS: actual Flutter services + loopback Kestrel HTTP/SignalR + PostgreSQL; match, send, replay, read, reconnect, authorization and block.");
}
finally
{
    File.Delete(fixture);
    if (host != null) { await host.StopAsync(); host.Dispose(); }
    NpgsqlConnection.ClearAllPools();
    await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
}
