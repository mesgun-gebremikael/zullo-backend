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
var photoProvider = new TestPhotoProvider();
await using var admin = new NpgsqlConnection(adminConnection);
await admin.OpenAsync();
await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin).ExecuteNonQueryAsync();
try
{
    await using (var db = new AppDbContext(options))
    {
        await db.GetService<IMigrator>().MigrateAsync("20261027231755_PreserveDiscoveryPreferences");
        foreach (var user in users)
        {
            // Seed the old schema using only columns that existed before this upgrade.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"User\" (\"Id\",\"Email\",\"PasswordHash\",\"IsVerified\",\"CreatedAtUtc\",\"LikesRemaining\",\"MatchRadiusKm\",\"LikesResetAtUtc\") VALUES ({user.Id},{user.Email},{user.PasswordHash},{user.IsVerified},{user.CreatedAtUtc},{user.LikesRemaining},{user.MatchRadiusKm},{user.LikesResetAtUtc})");
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
                services.AddHttpClient<ICommerceVerifier, RevenueCatVerifier>();
                services.AddScoped<CommerceService>();
                        services.AddSingleton<IPhotoProvider>(photoProvider);
                        services.AddScoped<PhotoLifecycle>();
                        services.AddScoped<PhotoCleanup>();
                        services.AddScoped<LegacyPhotoAdoption>();
            services.AddSingleton<MessageSendLimiter>();
            services.AddAuthenticationProtection();
            services.AddSignalR();
            services.AddSingleton<IUserIdProvider, NameIdentifierUserIdProvider>();
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
                {
                o.Events = new JwtBearerEvents { OnMessageReceived = ChatBearerAuthentication.ReadHubToken, OnTokenValidated = ChatBearerAuthentication.ValidateAccount };
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(key), ClockSkew = TimeSpan.Zero
                };
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
    HubConnection Socket(Guid user, bool authenticated = true, bool queryToken = false)
    {
        var token = Token(user);
        return new HubConnectionBuilder().WithUrl("http://localhost/hubs/chat" + (queryToken ? "?access_token=" + token : ""), o =>
        {
            o.Transports = HttpTransportType.WebSockets;
            o.HttpMessageHandlerFactory = _ => server.CreateHandler();
            if (authenticated && !queryToken) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            o.WebSocketFactory = async (context, cancellation) =>
            {
                var ws = server.CreateWebSocketClient();
                if (authenticated && !queryToken) ws.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
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
    var browser = Socket(concurrentSender.Id, queryToken: true);
    var browserMessage = new TaskCompletionSource<MessageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
    browser.On<MessageDto>("MessageReceived", value => browserMessage.TrySetResult(value));
    sockets.Add(browser); await browser.StartAsync();
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
    Check((await browserMessage.Task.WaitAsync(TimeSpan.FromSeconds(10))).Id == concurrent[0].Id, "Browser query-authenticated socket missed its own message");
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
    var blockedAtReceiver = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    var blockedAtPeer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    receiver.On<JsonElement>("RelationChanged", value => blockedAtReceiver.TrySetResult(value));
    peer.On<JsonElement>("RelationChanged", value => blockedAtPeer.TrySetResult(value));
    var blockResponse = await b.PostAsJsonAsync("/blocks", new { blockedUserId = concurrentSender.Id });
    Check(blockResponse.IsSuccessStatusCode, "Block API failed against PostgreSQL");
    Check((await blockedAtReceiver.Task.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("otherUserId").GetGuid() == concurrentSender.Id,
        "Blocker did not receive relationship invalidation");
    Check((await blockedAtPeer.Task.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("otherUserId").GetGuid() == recipient.Id,
        "Blocked user did not receive relationship invalidation");
    Check((await a.PostAsJsonAsync("/messages/send", new { toUserId = recipient.Id, text = "One delivery", clientMessageId = "parallel-key" })).StatusCode == HttpStatusCode.Forbidden,
        "Blocked replay leaked canonical message");
    Check((await a.GetAsync($"/messages/thread-page?otherUserId={recipient.Id}")).StatusCode == HttpStatusCode.Forbidden, "Blocked history was accessible");
    var ownedBlocks = await b.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks");
    Check(ownedBlocks!.Any(row => row.UserId == concurrentSender.Id), "Blocked list omitted the blocked account");
    await a.DeleteAsync($"/blocks/{recipient.Id}");
    Check((await a.GetAsync($"/messages/thread-page?otherUserId={recipient.Id}")).StatusCode == HttpStatusCode.Forbidden,
        "Blocked user removed the other account's block");
    Check((await b.DeleteAsync($"/blocks/{concurrentSender.Id}")).IsSuccessStatusCode, "Unblock API failed");
    Check((await a.GetAsync($"/messages/thread-page?otherUserId={recipient.Id}")).StatusCode == HttpStatusCode.Forbidden,
        "Unblock restored the old match");
    var likeA = await (await a.PostAsJsonAsync("/swipe/like", new { targetUserId = recipient.Id })).Content.ReadFromJsonAsync<JsonElement>();
    Check(!likeA.GetProperty("matched").GetBoolean(), "Unblock reused an old reciprocal like");
    var likeB = await (await b.PostAsJsonAsync("/swipe/like", new { targetUserId = concurrentSender.Id })).Content.ReadFromJsonAsync<JsonElement>();
    Check(likeB.GetProperty("matched").GetBoolean(), "Fresh reciprocal likes did not create a new match");
    var newHistory = await a.GetFromJsonAsync<MessagePageDto>($"/messages/thread-page?otherUserId={recipient.Id}");
    Check(newHistory!.Messages.Count == 0, "New match resurrected old conversation");
    Console.WriteLine("PASS: PostgreSQL block/unblock, live invalidation on both users, owned list, fresh reciprocal rematch and empty new history.");
    // Real PostgreSQL row locking: a single purchased Boost cannot be spent twice.
    await using (var db = new AppDbContext(options))
    {
        var wallet = await db.CommerceAccounts.SingleOrDefaultAsync(w => w.UserId == concurrentSender.Id);
        if (wallet == null) { wallet = new CommerceAccount { UserId = concurrentSender.Id }; db.CommerceAccounts.Add(wallet); }
        wallet.PurchasedBoosts = 1;
        wallet.BoostUntilUtc = null;
        await db.SaveChangesAsync();
    }
    var boostAttempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => a.PostAsync("/commerce/boost", null)));
    Check(boostAttempts.Count(r => r.StatusCode == HttpStatusCode.OK) == 1, "Concurrent Boost spent the same credit twice");
    Check(boostAttempts.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 7, "Active Boost was not protected against duplicate activation");
    await Parallel.ForEachAsync(Enumerable.Range(0, 8), async (_, cancellation) => {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CommerceService>().ApplyCredit(concurrentSender.Id,
            new VerifiedCredit("app_store:pg-idempotency", "zullo_superlikes_5", 5, 0), false, cancellation);
    });
    await using (var db = new AppDbContext(options))
    {
        var wallet = await db.CommerceAccounts.SingleAsync(w => w.UserId == concurrentSender.Id);
        Check(wallet.PurchasedSuperLikes == 5, "Concurrent replay multiplied purchased credits");
        Check(wallet.PurchasedBoosts == 0, "Boost balance was not persisted");
        wallet.PurchasedSuperLikes = 1;
        foreach (var like in await db.Likes.Where(l => l.FromUserId == concurrentSender.Id).ToListAsync()) like.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }
    var likesInvalidation = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    populationSockets[998].On<JsonElement>("LikesChanged", value => likesInvalidation.TrySetResult(value));
    populationSockets[999].On<JsonElement>("LikesChanged", value => likesInvalidation.TrySetResult(value));
    var superLikeDelivery = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    populationSockets[998].On<JsonElement>("SuperLikeReceived", value => superLikeDelivery.TrySetResult(value));
    populationSockets[999].On<JsonElement>("SuperLikeReceived", value => superLikeDelivery.TrySetResult(value));
    var superAttempts = await Task.WhenAll(new[] { users[998].Id, users[999].Id }.Select(target =>
        a.PostAsJsonAsync("/swipe/like", new { targetUserId = target, superLike = true })));
    Check(superAttempts.Count(r => r.StatusCode == HttpStatusCode.OK) == 1, "Concurrent Superlikes overspent a single credit");
    Check(!(await likesInvalidation.Task.WaitAsync(TimeSpan.FromSeconds(5))).EnumerateObject().Any(),
        "Likes invalidation must not reveal sender identity to a free recipient");
    Check((await superLikeDelivery.Task.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("senderUserId").GetGuid() == concurrentSender.Id,
        "Superlike did not reach its authenticated recipient");
    await using (var db = new AppDbContext(options))
        Check((await db.CommerceAccounts.SingleAsync(w => w.UserId == concurrentSender.Id)).PurchasedSuperLikes == 0, "Superlike debit was not persisted");
    var regularLikeInvalidation = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    populationSockets[997].On<JsonElement>("LikesChanged", value => regularLikeInvalidation.TrySetResult(value));
    await using (var db = new AppDbContext(options)) {
        foreach (var like in await db.Likes.Where(l => l.FromUserId == concurrentSender.Id).ToListAsync()) like.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }
    Check((await a.PostAsJsonAsync("/swipe/like", new { targetUserId = users[997].Id })).StatusCode == HttpStatusCode.OK,
        "Ordinary like could not be sent");
    Check(!(await regularLikeInvalidation.Task.WaitAsync(TimeSpan.FromSeconds(5))).EnumerateObject().Any(),
        "Ordinary like must update its recipient without revealing identity");
    var incoming = Client(users[997].Id);
    var hiddenSummary = await incoming.GetFromJsonAsync<JsonElement>("/likes/received/summary");
    Check(hiddenSummary.GetProperty("totalCount").GetInt32() == 1 && hiddenSummary.GetProperty("hiddenCount").GetInt32() == 1,
        "PostgreSQL hidden likes summary must count a regular incoming like");
    Console.WriteLine("PASS: real PostgreSQL concurrent Boost activation, credit replay and Superlike debit.");
    // Real PostgreSQL owner locks, upload leases, unique-key conflicts and cleanup claims.
    var photoOwner = new User { PasswordHash = BCrypt.Net.BCrypt.HashPassword("photo-closure-password") };
    var secondOwner = new User();
    await using (var db = new AppDbContext(options)) { db.Users.AddRange(photoOwner, secondOwner); await db.SaveChangesAsync(); }
    var photoBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0 };
    async Task<PhotoView> UploadPhoto(Guid owner, Guid id) {
        await using var db = new AppDbContext(options);
        return await new PhotoLifecycle(db, photoProvider).Upload(owner, id, photoBytes, default);
    }
    var duplicateId = Guid.NewGuid();
    var duplicates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => UploadPhoto(photoOwner.Id, duplicateId)));
    Check(duplicates.All(p => p.State is PhotoStates.Ready or PhotoStates.Uploading) && photoProvider.Uploads == 1, "Concurrent duplicate uploads did not share one durable reservation");
    var otherId = Guid.NewGuid();
    async Task<int> ReserveCollision(Guid owner) {
        try { await UploadPhoto(owner, otherId); return 200; }
        catch (PhotoOperationException ex) { return ex.Status; }
    }
    var collision = await Task.WhenAll(ReserveCollision(photoOwner.Id), ReserveCollision(secondOwner.Id));
    Check(collision.Count(s => s == 200) == 1 && collision.Any(s => s is 404 or 409), "Cross-owner UUID conflict must be a controlled response");
    var attached = await UploadPhoto(photoOwner.Id, Guid.NewGuid());
    using var photoClient = Client(photoOwner.Id);
    Check((await photoClient.PostAsJsonAsync("/me/profile", new { displayName = "Photo test", age = 25, gender = "Man", photoUrls = new[] { (await UploadPhoto(photoOwner.Id, duplicateId)).Url, attached.Url }.Distinct().ToArray() })).IsSuccessStatusCode,
        "Owned photos could not be attached in PostgreSQL");
    // Pick an unlinked draft, expire it, and race two worker instances.
    var draft = await UploadPhoto(photoOwner.Id, Guid.NewGuid());
    await using (var db = new AppDbContext(options)) { (await db.OwnedPhotos.FindAsync(draft.Id))!.NextAttemptUtc = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync(); }
    async Task SweepPhotos() {
        await using var db = new AppDbContext(options);
        await new PhotoCleanup(db, new PhotoLifecycle(db, photoProvider), photoProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger<PhotoCleanup>.Instance).RunOnce();
    }
    await Task.WhenAll(SweepPhotos(), SweepPhotos());
    Check(photoProvider.Deletions.GetValueOrDefault(draft.Id) == 1, "Two PostgreSQL workers deleted the same claimed image");
    var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finishUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var inflightId = Guid.NewGuid();
    photoProvider.BeforeUpload = async photo => { if (photo.Id == inflightId) { uploadStarted.TrySetResult(); await finishUpload.Task; } };
    var inflight = UploadPhoto(photoOwner.Id, inflightId);
    await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var photoClosure = await photoClient.PostAsJsonAsync("/me/account/removal", new { password = "photo-closure-password", confirm = true });
    Check(photoClosure.StatusCode == HttpStatusCode.Accepted, "In-flight provider call held a lock that blocked account closure");
    finishUpload.TrySetResult();
    try { await inflight; throw new Exception("A closed account upload was resurrected"); }
    catch (PhotoOperationException ex) { Check(ex.Status == 503, "Unexpected retired upload status"); }
    photoProvider.BeforeUpload = null;
    await SweepPhotos(); await SweepPhotos();
    await using (var db = new AppDbContext(options)) {
        Check(!await db.OwnedPhotos.AnyAsync(p => p.UserId == photoOwner.Id && p.State != PhotoStates.Deleted), "Closed account owned images were not cleaned");
        Check((await db.AccountRemovalRequests.SingleAsync(r => r.FormerUserId == photoOwner.Id)).ExternalAssetsReviewed, "Owned image erasure completion was not persisted");
    }
    Console.WriteLine("PASS: PostgreSQL concurrent photo uploads, cross-owner UUID conflict, two-worker cleanup and closure during upload.");
    await using (var db = new AppDbContext(options)) {
        (await db.Users.SingleAsync(u => u.Id == concurrentSender.Id)).PasswordHash = BCrypt.Net.BCrypt.HashPassword("closure-test-password");
        await db.SaveChangesAsync();
    }
    Check((await a.PatchAsJsonAsync("/me/account/pause", new { paused = true })).StatusCode == HttpStatusCode.OK,
        "Pause was not persisted");
    Check((await a.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.IsPaused, "Paused account was not marked paused");
    await a.PatchAsJsonAsync("/me/account/pause", new { paused = false });
    var closureSocket = Socket(concurrentSender.Id);
    var accountClosed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    closureSocket.On<JsonElement>("AccountClosed", value => accountClosed.TrySetResult(value));
    await closureSocket.StartAsync();
    var editDuringClosure = a.PatchAsJsonAsync("/me/profile", new { bio = "Concurrent edit" });
    var sendDuringClosure = b.PostAsJsonAsync("/messages/send", new { toUserId = concurrentSender.Id, text = "Concurrent close", clientMessageId = "closure-race" });
    var closureResponse = await a.PostAsJsonAsync("/me/account/removal", new { password = "closure-test-password", confirm = true });
    await editDuringClosure;
    var lateMessage = await sendDuringClosure;
    Check(lateMessage.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Forbidden,
        "Concurrent message returned an unexpected account-closure result");
    Check(closureResponse.StatusCode == HttpStatusCode.Accepted, "Account closure was not accepted");
    await accountClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check((await a.GetAsync("/me/account")).StatusCode == HttpStatusCode.Unauthorized, "Old JWT could access a closed account");
    await using (var db = new AppDbContext(options)) {
        Check(await db.AccountRemovalRequests.AnyAsync(r => r.FormerUserId == concurrentSender.Id), "External asset follow-up was lost");
        Check(!await db.Profiles.AnyAsync(p => p.UserId == concurrentSender.Id), "Closed profile remained published");
        Check(!await db.Messages.AnyAsync(m => m.FromUserId == concurrentSender.Id || m.ToUserId == concurrentSender.Id), "Concurrent message survived account closure");
    }
    Console.WriteLine("PASS: PostgreSQL pause/reactivate, account closure, durable external-asset follow-up and old JWT revocation.");
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

sealed class TestPhotoProvider : IPhotoProvider
{
    public bool Enabled => true;
    public string CloudName => "test-cloud";
    public int Uploads;
    public ConcurrentDictionary<Guid, int> Deletions = new();
    public Func<OwnedPhoto, Task>? BeforeUpload;
    public async Task<VerifiedPhoto> Upload(OwnedPhoto photo, byte[] bytes, CancellationToken cancellation) {
        Interlocked.Increment(ref Uploads);
        if (BeforeUpload != null) await BeforeUpload(photo);
        return new("asset-" + photo.Id, photo.PublicId!, $"https://res.cloudinary.com/test-cloud/image/upload/v1/{photo.PublicId}.png");
    }
    public Task Delete(OwnedPhoto photo, CancellationToken cancellation) { Deletions.AddOrUpdate(photo.Id, 1, (_, count) => count + 1); return Task.CompletedTask; }
    public Task<VerifiedPhoto?> InspectLegacy(string id, CancellationToken cancellation) => Task.FromResult<VerifiedPhoto?>(null);
}
