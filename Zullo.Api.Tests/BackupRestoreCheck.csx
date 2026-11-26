using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zullo.Api.Data;
using Zullo.Api.Models;

// This executable is invoked only by verify-backup-restore.py, which creates its
// own container and synthetic databases. It never accepts a production source.
if (args.Length != 2 || args[0] is not ("seed" or "verify")) throw new Exception("Expected seed/verify and a fixture snapshot path.");
var connectionString = Environment.GetEnvironmentVariable("ZULLO_BACKUP_DRILL_CONNECTION")
    ?? throw new Exception("Synthetic drill connection is required.");
var settings = new NpgsqlConnectionStringBuilder(connectionString);
var drillId = Environment.GetEnvironmentVariable("ZULLO_BACKUP_DRILL_ID");
if (!Guid.TryParseExact(drillId, "N", out _) || settings.Host != "127.0.0.1" ||
    settings.Database != (args[0] == "seed" ? "zullo_restore_source_" : "zullo_restore_target_") + drillId)
    throw new Exception("This check only accepts the isolated local synthetic drill databases.");
var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
builder.EnableDynamicJson();
await using var source = builder.Build();
await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(source).Options);
var owner = Guid.Parse("11111111-1111-4111-8111-111111111111");
var peer = Guid.Parse("22222222-2222-4222-8222-222222222222");
var matchId = Guid.Parse("33333333-3333-4333-8333-333333333333");
var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
if (args[0] == "seed")
{
    await db.Database.MigrateAsync();
    db.Users.AddRange(new User { Id = owner, Email = "restore-owner@example.test", PasswordHash = "synthetic-only-no-login", EmailVerifiedAtUtc = now },
        new User { Id = peer, Email = "restore-peer@example.test", PasswordHash = "synthetic-only-no-login" });
    db.Profiles.Add(new Profile { UserId = owner, DisplayName = "Återställning · مرحباً · እንኳን", Age = 29,
        Bio = "Synthetic fixture", Gender = "Kvinna", PhotoUrls = new() { "https://images.example.test/a", "https://images.example.test/b" },
        ConversationPrompts = new() { new("small_joy", "Kaffe · قهوة"), new("ask_me", "Resor · 旅行") },
        AutomaticLocation = true, DiscoveryLocationName = "Stockholm", LocationUpdatedAtUtc = now,
        Lat = 59.3293, Lng = 18.0686, IsVisible = true });
    db.Matches.Add(new Match { Id = matchId, UserAId = owner, UserBId = peer, CreatedAtUtc = now });
    db.Messages.Add(new Message { FromUserId = owner, ToUserId = peer, Text = "Synthetic chat · أهلاً", ClientMessageId = "restore-message", DeliveryKey = "restore-delivery", CreatedAtUtc = now });
    db.OnboardingDrafts.Add(new OnboardingDraft { UserId = peer, Revision = 7, Step = 3, DataJson = "{\"bio\":\"Draft · 你好\"}", UpdatedAtUtc = now });
    db.CommerceAccounts.Add(new CommerceAccount { UserId = owner, PurchasedBoosts = 2, PurchasedSuperLikes = 5,
        IncludedSuperLikes = 1, NextSuperLikeRefreshUtc = now.AddDays(7), PremiumUntilUtc = now.AddMonths(1) });
    db.PurchaseGrants.Add(new PurchaseGrant { UserId = owner, TransactionKey = "play_store:synthetic-receipt",
        ProductId = "zullo_superlikes_5", ProviderTransactionId = "synthetic-rc-alias", SuperLikes = 5 });
    db.PushInstallations.Add(new PushInstallation { Id = Guid.Parse("44444444-4444-4444-8444-444444444444"),
        Version = 12, UserId = null, Token = null, TokenHash = null, UpdatedAtUtc = now });
    db.MatchNotifications.Add(new MatchNotification { MatchId = matchId, RecipientUserId = owner, OtherUserId = peer,
        Attempts = 2, CreatedAtUtc = now, NextAttemptAtUtc = now.AddMinutes(1) });
    db.PendingEmailRegistrations.Add(new PendingEmailRegistration { Email = "pending-restore@example.test", PasswordHash = "synthetic-hash",
        CodeHash = new string('a', 64), CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(15),
        NextSendAtUtc = now.AddMinutes(1), SendWindowStartedAtUtc = now, SendCount = 1, DeliveryConfirmedAtUtc = now });
    await db.SaveChangesAsync();
}
if ((await db.Database.GetPendingMigrationsAsync()).Any()) throw new Exception("Restored migration history is incomplete.");
await using var connection = await source.OpenConnectionAsync();
var tables = new List<string>();
await using (var command = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename", connection))
await using (var reader = await command.ExecuteReaderAsync())
    while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
var snapshot = new SortedDictionary<string, string>();
foreach (var table in tables)
{
    var name = table.Replace("\"", "\"\"");
    var sql = $"SELECT COALESCE(jsonb_agg(v.row ORDER BY v.row::text), '[]'::jsonb)::text FROM (SELECT to_jsonb(t) AS row FROM \"{name}\" t) v";
    snapshot[table] = (string)(await new NpgsqlCommand(sql, connection).ExecuteScalarAsync())!;
}
if (args[0] == "seed")
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(snapshot));
else
{
    var expected = JsonSerializer.Deserialize<SortedDictionary<string, string>>(await File.ReadAllTextAsync(args[1]))!;
    if (!snapshot.SequenceEqual(expected)) throw new Exception("Restored schema tables or row values differ from the full synthetic snapshot.");
    var previousSequence = await db.Messages.MaxAsync(m => m.DeliverySequence);
    var nextMessage = new Message { FromUserId = peer, ToUserId = owner, Text = "New write after restore", ClientMessageId = "post-restore", DeliveryKey = "post-restore-key" };
    db.Messages.Add(nextMessage);
    await db.SaveChangesAsync();
    if (nextMessage.DeliverySequence <= previousSequence) throw new Exception("Message sequence was not restored correctly.");
    // Exercise an actual restored unique index rather than only its metadata.
    try
    {
        await new NpgsqlCommand("INSERT INTO \"PurchaseGrants\" SELECT * FROM \"PurchaseGrants\"", connection).ExecuteNonQueryAsync();
        throw new Exception("Restored purchase ledger uniqueness was lost.");
    }
    catch (PostgresException error) when (error.SqlState == "23505") { }
}
Console.WriteLine($"PASS: synthetic {args[0]} checked {tables.Count} public tables, migration history and restored data integrity.");
