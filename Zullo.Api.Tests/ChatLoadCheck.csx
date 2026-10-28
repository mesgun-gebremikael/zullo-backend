using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Zullo.Api.Controllers;
using Zullo.Api.Data;
using Zullo.Api.Dtos;
using Zullo.Api.Hubs;
using Zullo.Api.Models;
using Zullo.Api.Services;

NpgsqlConnection.GlobalTypeMapper.EnableDynamicJson();
var adminConnection = Environment.GetEnvironmentVariable("ZULLO_CHAT_TEST_ADMIN_CONNECTION")
    ?? "Host=127.0.0.1;Port=5432;Database=zullo;Username=zullo";
var database = "zullo_chat_" + Guid.NewGuid().ToString("N");
var connectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database }.ConnectionString;
var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
var key = RandomNumberGenerator.GetBytes(32);
var users = Enumerable.Range(0, 1003).Select(i => new User { Email = $"load-{i}@example.test" }).ToArray();
var recipient = users[1000];
var concurrentSender = users[1001];
var outsider = users[1002];
var sockets = new List<HubConnection>();
IHost? host = null;
var results = new List<object>();
await using var admin = new NpgsqlConnection(adminConnection);
await admin.OpenAsync();
await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin).ExecuteNonQueryAsync();
try
{
    await using (var db = new AppDbContext(options))
    {
        await db.GetService<IMigrator>().MigrateAsync("20261027231755_PreserveDiscoveryPreferences");
        db.Users.AddRange(users);
        foreach (var user in users)
        {
            db.Profiles.Add(new Profile { UserId = user.Id, DisplayName = "Synthetic test user", Age = 25,
                Gender = "Man", IsVisible = true,
                PhotoUrls = new() { "https://images.example/1", "https://images.example/2" } });
            if (user.Id != recipient.Id)
                db.Matches.Add(new Match
                {
                    UserAId = user.Id.CompareTo(recipient.Id) < 0 ? user.Id : recipient.Id,
                    UserBId = user.Id.CompareTo(recipient.Id) < 0 ? recipient.Id : user.Id
                });
        }
        await db.SaveChangesAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        for (var i = 0; i < 125; i++)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO "Messages" ("Id","FromUserId","ToUserId","Text","ClientMessageId","CreatedAtUtc","ReadAtUtc")
                VALUES (@id,@from,@to,@text,@key,@time,NULL)
                """, connection);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("from", users[0].Id);
            insert.Parameters.AddWithValue("to", recipient.Id);
            insert.Parameters.AddWithValue("text", i < 2 ? "Historical duplicate" : "Historical message");
            insert.Parameters.AddWithValue("key", i < 2 ? "historical-key" : $"old-{i}");
            insert.Parameters.AddWithValue("time", DateTime.UtcNow.AddDays(-1).AddSeconds(i / 3));
            await insert.ExecuteNonQueryAsync();
        }
        async Task<string> Snapshot() => (string)(await new NpgsqlCommand("""
            SELECT jsonb_agg(to_jsonb(m) - 'DeliveryKey' - 'DeliverySequence' ORDER BY "Id")::text FROM "Messages" m
            """, connection).ExecuteScalarAsync())!;
        var oldRows = await Snapshot();
        await db.GetService<IMigrator>().MigrateAsync();
        Check(await Snapshot() == oldRows, "Migration modified historical message content or IDs");
        Check(await db.Messages.CountAsync(m => m.ClientMessageId == "historical-key") == 2, "Migration deleted historical duplicates");
        await db.GetService<IMigrator>().MigrateAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261027231755_PreserveDiscoveryPreferences");
        Check(await Snapshot() == oldRows, "Rollback modified historical message content");
        await db.GetService<IMigrator>().MigrateAsync();
        Check(await Snapshot() == oldRows, "Reapply modified historical message content");
    }

    host = await new HostBuilder().ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Critical))
        .ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(MessagesController).Assembly);
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
            services.AddScoped<UserRelationService>();
            services.AddScoped<PushNotificationService>();
            services.AddScoped<LikeLimitService>();
            services.AddSingleton<MessageSendLimiter>();
            services.AddAuthenticationProtection();
            services.AddSignalR();
            services.AddSingleton<IUserIdProvider, NameIdentifierUserIdProvider>();
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(key), ClockSkew = TimeSpan.Zero
                });
            services.AddAuthorization();
        }).Configure(app =>
        {
            app.UseRouting(); app.UseWebSockets(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => { endpoints.MapControllers(); endpoints.MapHub<ChatHub>("/hubs/chat"); });
        })).StartAsync();
    var server = host.GetTestServer();
    string Token(Guid user) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        claims: new[] { new Claim(ClaimTypes.NameIdentifier, user.ToString()) },
        expires: DateTime.UtcNow.AddHours(1),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)));
    HttpClient Client(Guid user)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }
    HubConnection Socket(Guid user, bool authenticated = true)
    {
        var token = Token(user);
        return new HubConnectionBuilder().WithUrl("http://localhost/hubs/chat", o =>
        {
            o.Transports = HttpTransportType.WebSockets;
            o.HttpMessageHandlerFactory = _ => server.CreateHandler();
            if (authenticated) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            o.WebSocketFactory = async (context, cancellation) =>
            {
                var ws = server.CreateWebSocketClient();
                if (authenticated) ws.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
                return await ws.ConnectAsync(context.Uri, cancellation);
            };
        }).Build();
    }
    using var a = Client(concurrentSender.Id);
    using var b = Client(recipient.Id);
    var liveMessages = new ConcurrentDictionary<Guid, byte>();
    var duplicateEvents = 0;
    var receiver = Socket(recipient.Id);
    receiver.On<MessageDto>("MessageReceived", m => { if (!liveMessages.TryAdd(m.Id, 0)) Interlocked.Increment(ref duplicateEvents); });
    sockets.Add(receiver);
    await receiver.StartAsync();
    var peer = Socket(concurrentSender.Id);
    var receipt = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    peer.On<JsonElement>("MessagesRead", value => receipt.TrySetResult(value));
    sockets.Add(peer); await peer.StartAsync();
    var forbiddenEvents = 0;
    var third = Socket(outsider.Id);
    third.On<MessageDto>("MessageReceived", _ => Interlocked.Increment(ref forbiddenEvents));
    sockets.Add(third); await third.StartAsync();
    await using (var unauthenticated = Socket(Guid.NewGuid(), false))
    {
        var denied = false;
        try { await unauthenticated.StartAsync(); } catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized) { denied = true; }
        Check(denied, "Unauthenticated SignalR connection was accepted");
    }

    async Task<MessageDto> Deliver(HttpClient client, Guid to, string id, string text)
    {
        var reply = await client.PostAsJsonAsync("/messages/send", new { toUserId = to, text, clientMessageId = id });
        Check(reply.StatusCode == HttpStatusCode.OK, $"Unexpected send status {reply.StatusCode}");
        return (await reply.Content.ReadFromJsonAsync<MessageDto>())!;
    }
    var concurrent = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Deliver(a, recipient.Id, "parallel-key", "One delivery")));
    Check(concurrent.Select(m => m.Id).Distinct().Count() == 1, "Concurrent replay produced multiple message IDs");
    await using (var db = new AppDbContext(options))
        Check(await db.Messages.CountAsync(m => m.ClientMessageId == "parallel-key") == 1, "Concurrent replay inserted duplicates");
    var conflict = await a.PostAsJsonAsync("/messages/send", new { toUserId = recipient.Id, text = "Different content", clientMessageId = "parallel-key" });
    Check(conflict.StatusCode == HttpStatusCode.Conflict, "Conflicting replay was accepted");
    await Until(() => liveMessages.ContainsKey(concurrent[0].Id), TimeSpan.FromSeconds(10));
    var read = await b.PostAsync($"/messages/mark-read?otherUserId={concurrentSender.Id}", null);
    Check(read.StatusCode == HttpStatusCode.OK, "Read update failed");
    var ack = await receipt.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(ack.GetProperty("readThroughSequence").GetInt64() == concurrent[0].Sequence, "Read receipt lacks exact sequence cutoff");
    var prior = (await b.GetFromJsonAsync<MessagePageDto>($"/messages/thread-page?otherUserId={concurrentSender.Id}"))!;
    await receiver.StopAsync();
    var offline = await Deliver(a, recipient.Id, "offline-key", "Recover after reconnect");
    await receiver.StartAsync();
    var catchup = (await b.GetFromJsonAsync<MessagePageDto>($"/messages/thread-page?otherUserId={concurrentSender.Id}&after={prior.LatestCursor}"))!;
    Check(catchup.Messages.Any(m => m.Id == offline.Id), "Reconnect catchup missed stored message");
    var senderPage = (await a.GetFromJsonAsync<MessagePageDto>($"/messages/thread-page?otherUserId={recipient.Id}"))!;
    Check(senderPage.OutgoingReadThrough == concurrent[0].Sequence && senderPage.Messages.Single(m => m.Id == offline.Id).ReadAtUtc == null,
        "Read watermark falsely acknowledged a later message");
    Check(forbiddenEvents == 0, "A third account received another conversation's live message");

    // Backward paging, including equal historical timestamps, against real PostgreSQL.
    using (var historyClient = Client(users[0].Id))
    {
        var ids = new HashSet<Guid>(); string? before = null;
        do
        {
            var page = (await historyClient.GetFromJsonAsync<MessagePageDto>($"/messages/thread-page?otherUserId={recipient.Id}&take=17" + (before == null ? "" : $"&before={before}")))!;
            Check(page.Messages.Count <= 17, "History exceeded page size");
            foreach (var message in page.Messages) Check(ids.Add(message.Id), "Duplicate across PostgreSQL history pages");
            before = page.NextCursor;
        } while (before != null);
        Check(ids.Count == 125, "History paging lost historical messages");
    }

    var populationSockets = new List<HubConnection>();
    foreach (var population in new[] { 100, 500, 1000 })
    {
        var toConnect = users.Take(population).Skip(populationSockets.Count).Select(user => Socket(user.Id)).ToArray();
        await Parallel.ForEachAsync(toConnect, new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (connection, cancellation) =>
            await connection.StartAsync(cancellation));
        sockets.AddRange(toConnect); populationSockets.AddRange(toConnect);
        var durations = new ConcurrentDictionary<string, ConcurrentBag<double>>();
        var failures = new ConcurrentBag<string>();
        var beforeCount = liveMessages.Count;
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var elapsed = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, population), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (i, cancellation) =>
        {
            using var client = Client(users[i].Id);
            async Task Measure(string name, Func<Task<HttpResponseMessage>> operation)
            {
                var clock = Stopwatch.StartNew();
                using var response = await operation();
                await response.Content.LoadIntoBufferAsync();
                durations.GetOrAdd(name, _ => new()).Add(clock.Elapsed.TotalMilliseconds);
                if (!response.IsSuccessStatusCode) failures.Add($"{name}:{(int)response.StatusCode}");
            }
            await Measure("send", () => client.PostAsJsonAsync("/messages/send", new { toUserId = recipient.Id,
                text = "Synthetic load message", clientMessageId = $"wave-{population}-{i}" }, cancellation));
            await Measure("history", () => client.GetAsync($"/messages/thread-page?otherUserId={recipient.Id}&take=25", cancellation));
            await Measure("matches", () => client.GetAsync("/matches", cancellation));
            await Measure("feed", () => client.GetAsync("/swipe/feed?take=10", cancellation));
        });
        elapsed.Stop();
        await Until(() => liveMessages.Count >= beforeCount + population, TimeSpan.FromSeconds(15));
        Check(failures.IsEmpty, "Load request failures: " + string.Join(",", failures.Take(10)));
        Check(liveMessages.Count == beforeCount + population, "Live delivery count differs from successful sends");
        var endpointMetrics = durations.ToDictionary(entry => entry.Key, entry =>
        {
            var values = entry.Value.Order().ToArray();
            return new { count = values.Length, p50Ms = Math.Round(values[(values.Length - 1) / 2], 2),
                p95Ms = Math.Round(values[(int)Math.Ceiling(values.Length * .95) - 1], 2), maxMs = Math.Round(values[^1], 2) };
        });
        var metric = new { population, activeSignalRConnections = sockets.Count, httpConcurrency = 32,
            requests = population * 4, failures = failures.Count, elapsedSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 2),
            combinedClientServerCpuSeconds = Math.Round((process.TotalProcessorTime - cpuBefore).TotalSeconds, 2),
            combinedClientServerWorkingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 2), endpoints = endpointMetrics };
        results.Add(metric);
        Console.WriteLine(JsonSerializer.Serialize(metric));
    }
    var soakSeconds = int.TryParse(Environment.GetEnvironmentVariable("ZULLO_CHAT_SOAK_SECONDS"), out var configuredSoak)
        ? Math.Clamp(configuredSoak, 0, 3600) : 0;
    if (soakSeconds > 0)
    {
        var soak = Stopwatch.StartNew();
        var iteration = 0;
        var operations = 0;
        var initialManaged = GC.GetTotalMemory(true);
        while (soak.Elapsed.TotalSeconds < soakSeconds)
        {
            var round = iteration++;
            await Parallel.ForEachAsync(Enumerable.Range(0, 1000), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, cancellation) =>
            {
                using var client = Client(users[i].Id);
                await Deliver(client, recipient.Id, $"soak-{round}-{i}", "Synthetic soak message");
                using var history = await client.GetAsync($"/messages/thread-page?otherUserId={recipient.Id}&take=25", cancellation);
                Check(history.IsSuccessStatusCode, "Soak history request failed");
                using var matches = await client.GetAsync("/matches", cancellation);
                Check(matches.IsSuccessStatusCode, "Soak matches request failed");
                Interlocked.Add(ref operations, 3);
            });
            using var receiverList = await b.GetAsync("/matches");
            Check(receiverList.IsSuccessStatusCode, "Large recipient match list failed");
            if (iteration % 5 == 0)
                Console.WriteLine(JsonSerializer.Serialize(new { soakElapsedSeconds = Math.Round(soak.Elapsed.TotalSeconds), operations,
                    managedMiB = Math.Round(GC.GetTotalMemory(false) / 1048576d, 2) }));
        }
        var soakResult = new { soakSeconds = Math.Round(soak.Elapsed.TotalSeconds, 2), operations,
            managedBeforeMiB = Math.Round(initialManaged / 1048576d, 2),
            managedAfterMiB = Math.Round(GC.GetTotalMemory(true) / 1048576d, 2),
            httpConcurrency = 16, activeSignalRConnections = sockets.Count };
        results.Add(soakResult);
        Console.WriteLine(JsonSerializer.Serialize(soakResult));
    }

    var deliveredRows = 0;
    await using (var db = new AppDbContext(options))
        deliveredRows = await db.Messages.CountAsync(m => m.DeliveryKey != null);
    // Replay the busiest last wave: identities remain stable and no live events repeat.
    await Until(() => liveMessages.Count == deliveredRows - 1, TimeSpan.FromSeconds(15));
    var liveBeforeReplay = liveMessages.Count;
    await Parallel.ForEachAsync(Enumerable.Range(0, 1000), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (i, _) =>
    {
        using var client = Client(users[i].Id);
        await Deliver(client, recipient.Id, $"wave-1000-{i}", "Synthetic load message");
    });
    await using (var db = new AppDbContext(options))
        Check(await db.Messages.CountAsync(m => m.DeliveryKey != null) == deliveredRows, "Bulk replay inserted additional rows");
    Check(liveMessages.Count == liveBeforeReplay && duplicateEvents == 0, "Replay produced duplicate live events");
    await using (var db = new AppDbContext(options))
    {
        db.Blocks.Add(new Block { FromUserId = recipient.Id, BlockedUserId = concurrentSender.Id });
        await db.SaveChangesAsync();
    }
    Check((await a.PostAsJsonAsync("/messages/send", new { toUserId = recipient.Id, text = "One delivery", clientMessageId = "parallel-key" })).StatusCode == HttpStatusCode.Forbidden,
        "Blocked replay leaked canonical message");
    Check((await a.GetAsync($"/messages/thread-page?otherUserId={recipient.Id}")).StatusCode == HttpStatusCode.Forbidden, "Blocked history was accessible");
    await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("ZULLO_CHAT_TEST_RESULTS") ?? "/tmp/zullo-chat-load-results.json",
        JsonSerializer.Serialize(new { scenario = "In-process TestServer HTTP/WebSockets + real PostgreSQL; not a production capacity certification", results }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS: migration, concurrent idempotency, authenticated live delivery, receipt cutoff, reconnect catchup, paging, load and blocked replay.");
}
finally
{
    await Parallel.ForEachAsync(sockets, new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (connection, _) => await connection.DisposeAsync());
    if (host != null) { await host.StopAsync(); host.Dispose(); }
    NpgsqlConnection.ClearAllPools();
    await new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
}

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Until(Func<bool> condition, TimeSpan timeout)
{
    var clock = Stopwatch.StartNew();
    while (!condition() && clock.Elapsed < timeout) await Task.Delay(20);
    Check(condition(), "Timed out waiting for live delivery");
}
