using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Services;

var database = "zullo_profile_" + Guid.NewGuid().ToString("N");
var adminConnection = Environment.GetEnvironmentVariable("ZULLO_PROFILE_TEST_ADMIN_CONNECTION")
    ?? "Host=127.0.0.1;Port=5432;Database=zullo;Username=zullo";
var connectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database }.ConnectionString;
await using var admin = new NpgsqlConnection(adminConnection);
await admin.OpenAsync();
await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin).ExecuteNonQueryAsync();
try
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.EnableDynamicJson();
    await using var dataSource = dataSourceBuilder.Build();
    await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(dataSource).Options);
    var migrator = db.GetService<IMigrator>();
    await migrator.MigrateAsync("20260408213102_AddClientMessageIdToMessage");
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    var user = new User { Email = "migration@example.test", MatchRadiusKm = 250 };
    var blockedUser = new User { Email = "blocked-migration@example.test" };
    var profile = new Profile { UserId = user.Id, DisplayName = "Legacy name", Age = 29,
        Gender = "Kvinna", Bio = "Legacy bio", Workout = "Ofta", Smoking = "Nej", HeightCm = 175,
        LivePlace = "Stockholm", OriginPlace = "Asmara", Lat = 59.3293, Lng = 18.0686,
        PhotoUrls = new() { "https://images.example/a", "https://images.example/b" },
        Interests = new() { "Musik", "Resor" }, IsVisible = true };
    var baselineColumnsByTable = new Dictionary<string, string[]>();
    foreach (var entity in new object[] { user, blockedUser, profile })
    {
        var entityType = db.Model.FindEntityType(entity.GetType())!;
        // Seed only columns present in the baseline schema, regardless of later model additions.
        var baselineColumns = new HashSet<string>();
        await using (var schema = new NpgsqlCommand("SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table", connection))
        {
            schema.Parameters.AddWithValue("table", entityType.GetTableName()!);
            await using var reader = await schema.ExecuteReaderAsync();
            while (await reader.ReadAsync()) baselineColumns.Add(reader.GetString(0));
        }
        baselineColumnsByTable[entityType.GetTableName()!] = baselineColumns.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        var properties = entityType.GetProperties().Where(p => baselineColumns.Contains(p.Name) && p.PropertyInfo?.GetValue(entity) != null).ToArray();
        var columns = string.Join(",", properties.Select(p => $"\"{p.Name}\""));
        var parameters = string.Join(",", properties.Select((_, i) => "@p" + i));
        await using var insert = new NpgsqlCommand($"INSERT INTO \"{entityType.GetTableName()}\" ({columns}) VALUES ({parameters})", connection);
        for (var i = 0; i < properties.Length; i++)
        {
            var value = properties[i].PropertyInfo!.GetValue(entity)!;
            insert.Parameters.Add(value is List<string> list
                ? new NpgsqlParameter("p" + i, NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(list) }
                : new NpgsqlParameter("p" + i, value));
        }
        await insert.ExecuteNonQueryAsync();
    }
    var blockId = Guid.NewGuid();
    await using (var seedBlock = new NpgsqlCommand("INSERT INTO \"Blocks\" (\"Id\", \"FromUserId\", \"BlockedUserId\", \"CreatedAtUtc\") VALUES (@id, @owner, @target, @created)", connection))
    {
        seedBlock.Parameters.AddWithValue("id", blockId);
        seedBlock.Parameters.AddWithValue("owner", user.Id);
        seedBlock.Parameters.AddWithValue("target", blockedUser.Id);
        seedBlock.Parameters.AddWithValue("created", DateTime.UtcNow);
        await seedBlock.ExecuteNonQueryAsync();
    }
    async Task<string> RowsSnapshot(string table, IReadOnlyList<string> columns)
    {
        // The schema-derived projection compares every pre-existing column,
        // including nulls. Additive columns are checked explicitly below.
        var projection = string.Join(",", columns.Select(c => $"\"{c.Replace("\"", "\"\"")}\""));
        var key = table is "Profiles" or "CommerceAccounts" ? "UserId" : table == "PurchaseGrants" ? "TransactionKey" : "Id";
        var sql = $"SELECT COALESCE(jsonb_agg(to_jsonb(s) ORDER BY s.\"{key}\"), '[]'::jsonb)::text FROM (SELECT {projection} FROM \"{table}\") s";
        return (string)(await new NpgsqlCommand(sql, connection).ExecuteScalarAsync())!;
    }
    async Task<string[]> Columns(string table)
    {
        var names = new List<string>();
        await using var command = new NpgsqlCommand("SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table ORDER BY column_name", connection);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names.ToArray();
    }
    async Task<string> BlockSnapshot() => (string)(await new NpgsqlCommand("SELECT (to_jsonb(b) - 'SnapshotDisplayName' - 'SnapshotAge' - 'SnapshotPhotoUrl')::text FROM \"Blocks\" b", connection).ExecuteScalarAsync())!;
    Task<string> Snapshot() => RowsSnapshot("Profiles", baselineColumnsByTable["Profiles"]);
    Task<string> UserSnapshot() => RowsSnapshot("User", baselineColumnsByTable["User"]);
    var blockBefore = await BlockSnapshot();
    var before = await Snapshot();
    var usersBefore = await UserSnapshot();
    await migrator.MigrateAsync();
    if (await Snapshot() != before) throw new Exception("Forward migration changed existing profile fields.");
    if (await UserSnapshot() != usersBefore) throw new Exception("Forward migration changed existing user fields.");
    if (await BlockSnapshot() != blockBefore) throw new Exception("Forward migration changed existing block relationship.");
    var restoredBlock = await db.Blocks.AsNoTracking().SingleAsync();
    if (restoredBlock.Id != blockId || restoredBlock.SnapshotDisplayName != "" || restoredBlock.SnapshotAge != null || restoredBlock.SnapshotPhotoUrl != "")
        throw new Exception("Legacy block snapshot defaults changed.");
    var restored = await db.Profiles.AsNoTracking().SingleAsync();
    if (restored.InterestedIn != "" || restored.Workout != "Ofta" || restored.HeightCm != 175 || restored.PhotoUrls.Count != 2 ||
        restored.AutomaticLocation || restored.DiscoveryLocationName != "" || restored.LocationUpdatedAtUtc != null ||
        restored.PromptKey != "" || restored.PromptAnswer != "" || restored.ConversationPrompts.Count != 0)
        throw new Exception("Existing profile values or backward-compatible additive defaults changed.");
    if (await db.Users.AnyAsync(u => u.EmailVerifiedAtUtc != null || u.DiscoveryAgeConfigured || u.ExpandAge || u.ExpandDistance || u.DiscoveryMinAge != 18 || u.DiscoveryMaxAge != 99))
        throw new Exception("Legacy user verification or discovery defaults changed.");
    await migrator.MigrateAsync(); // repeatability
    if (await Snapshot() != before || await UserSnapshot() != usersBefore) throw new Exception("Repeated migration changed existing data.");
    await migrator.MigrateAsync("20260408213102_AddClientMessageIdToMessage");
    if (await Snapshot() != before || await UserSnapshot() != usersBefore) throw new Exception("Rollback changed existing fields.");
    await migrator.MigrateAsync();
    if (await Snapshot() != before || await UserSnapshot() != usersBefore) throw new Exception("Reapplying migration changed existing fields.");
    if (await BlockSnapshot() != blockBefore) throw new Exception("Repeated migration, rollback or reapply changed existing block relationship.");

    // Exercise the new migration from the immediately preceding released schema.
    // Only this uniquely named disposable database is ever rolled back.
    const string previousRelease = "20261103035014_BlockedProfileReferences";
    await migrator.MigrateAsync(previousRelease);
    const string legacyKey = "small_joy";
    const string legacyAnswer = "Legacy answer · أهلاً · እንኳን";
    await using (var seedLegacyPrompt = new NpgsqlCommand("UPDATE \"Profiles\" SET \"PromptKey\" = @key, \"PromptAnswer\" = @answer WHERE \"UserId\" = @user", connection))
    {
        seedLegacyPrompt.Parameters.AddWithValue("key", legacyKey);
        seedLegacyPrompt.Parameters.AddWithValue("answer", legacyAnswer);
        seedLegacyPrompt.Parameters.AddWithValue("user", user.Id);
        if (await seedLegacyPrompt.ExecuteNonQueryAsync() != 1) throw new Exception("Could not seed released-schema legacy prompt.");
    }
    // Seed actual released ledger rows before the new provider-alias column is
    // available. New migrations must not regrant credits or change paid balances.
    await using (var seedWallet = new NpgsqlCommand("INSERT INTO \"CommerceAccounts\" (\"UserId\", \"NextSuperLikeRefreshUtc\", \"IncludedSuperLikes\", \"IncludedBoosts\", \"PurchasedSuperLikes\", \"PurchasedBoosts\", \"HideActivity\", \"Incognito\", \"ProviderSnapshotMs\") VALUES (@user, @now, 2, 1, 7, 4, FALSE, FALSE, 42)", connection))
    {
        seedWallet.Parameters.AddWithValue("user", user.Id);
        seedWallet.Parameters.AddWithValue("now", DateTime.UtcNow);
        await seedWallet.ExecuteNonQueryAsync();
    }
    await using (var seedGrant = new NpgsqlCommand("INSERT INTO \"PurchaseGrants\" (\"TransactionKey\", \"UserId\", \"ProductId\", \"SuperLikes\", \"Boosts\", \"Refunded\") VALUES ('appstore:legacy-receipt', @user, 'zullo_superlikes_5', 5, 0, FALSE)", connection))
    {
        seedGrant.Parameters.AddWithValue("user", user.Id);
        await seedGrant.ExecuteNonQueryAsync();
    }
    var releasedWalletColumns = await Columns("CommerceAccounts");
    var releasedGrantColumns = await Columns("PurchaseGrants");
    var releasedWalletSnapshot = await RowsSnapshot("CommerceAccounts", releasedWalletColumns);
    var releasedGrantSnapshot = await RowsSnapshot("PurchaseGrants", releasedGrantColumns);
    var releasedUserColumns = await Columns("User");
    var releasedUserSnapshot = await RowsSnapshot("User", releasedUserColumns);
    // No released profile column is excluded: only the newly added collection.
    async Task<string> ReleasedProfileSnapshot() => (string)(await new NpgsqlCommand("SELECT (to_jsonb(p) - 'ConversationPrompts')::text FROM \"Profiles\" p", connection).ExecuteScalarAsync())!;
    var releasedProfileSnapshot = await ReleasedProfileSnapshot();
    var releasedBlockColumns = await Columns("Blocks");
    var releasedBlockSnapshot = await RowsSnapshot("Blocks", releasedBlockColumns);

    async Task AssertReleasedDataPreserved()
    {
        if (await ReleasedProfileSnapshot() != releasedProfileSnapshot ||
            await RowsSnapshot("User", releasedUserColumns) != releasedUserSnapshot ||
            await RowsSnapshot("Blocks", releasedBlockColumns) != releasedBlockSnapshot ||
            await RowsSnapshot("CommerceAccounts", releasedWalletColumns) != releasedWalletSnapshot ||
            await RowsSnapshot("PurchaseGrants", releasedGrantColumns) != releasedGrantSnapshot)
            throw new Exception("Latest migration changed a released profile, legacy prompt, user or block field.");
    }
    async Task AssertPromptDefaultAndLegacyFallback()
    {
        var emptyJson = (bool)(await new NpgsqlCommand("SELECT \"ConversationPrompts\" = '[]'::jsonb FROM \"Profiles\"", connection).ExecuteScalarAsync())!;
        if (!emptyJson) throw new Exception("Legacy profile did not receive an empty JSON prompt collection.");
        db.ChangeTracker.Clear();
        var legacy = await db.Profiles.AsNoTracking().SingleAsync();
        var prompts = ConversationPrompts.Read(legacy);
        if (legacy.ConversationPrompts.Count != 0 || legacy.PromptKey != legacyKey || legacy.PromptAnswer != legacyAnswer ||
            prompts.Count != 1 || prompts[0].Key != legacyKey || prompts[0].Answer != legacyAnswer)
            throw new Exception("Legacy prompt fallback failed after PostgreSQL migration.");
        if (await db.Users.AnyAsync(u => u.EmailVerifiedAtUtc != null))
            throw new Exception("Migration falsely marked a legacy email as verified.");
        var grant = await db.PurchaseGrants.AsNoTracking().SingleAsync();
        if (grant.ProviderTransactionId != null || grant.SuperLikes != 5 || grant.Boosts != 0 || grant.Refunded)
            throw new Exception("Migration changed a legacy receipt or falsely invented a provider alias.");
        var wallet = await db.CommerceAccounts.AsNoTracking().SingleAsync();
        if (wallet.IncludedSuperLikes != 2 || wallet.IncludedBoosts != 1 || wallet.PurchasedSuperLikes != 7 || wallet.PurchasedBoosts != 4)
            throw new Exception("Migration regranted or erased existing paid/included credits.");
    }

    await migrator.MigrateAsync();
    await AssertReleasedDataPreserved();
    await AssertPromptDefaultAndLegacyFallback();
    await migrator.MigrateAsync();
    await AssertReleasedDataPreserved();

    var expectedPrompts = new List<ProfilePrompt>
    {
        new(legacyKey, legacyAnswer),
        new("weekend", "海辺で音楽を聴く 🌊"),
        new("travel_story", "አዲስ አበባ · Stockholm · رحلة")
    };
    var editable = await db.Profiles.SingleAsync();
    ConversationPrompts.Assign(editable, expectedPrompts);
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var reloaded = await db.Profiles.AsNoTracking().SingleAsync();
    if (!reloaded.ConversationPrompts.SequenceEqual(expectedPrompts) || reloaded.PromptKey != legacyKey || reloaded.PromptAnswer != legacyAnswer)
        throw new Exception("PostgreSQL prompt JSON converter lost Unicode collection or legacy projection.");
    var arrayLength = (int)(await new NpgsqlCommand("SELECT jsonb_array_length(\"ConversationPrompts\") FROM \"Profiles\"", connection).ExecuteScalarAsync())!;
    if (arrayLength != 3) throw new Exception("Conversation prompts were not stored as a JSON array.");

    // In-place edits must be detected by the value comparer, not just assignment.
    editable = await db.Profiles.SingleAsync();
    expectedPrompts[1] = new ProfilePrompt("weekend", "Crème brûlée et promenades à Malmö");
    editable.ConversationPrompts[1] = expectedPrompts[1];
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    reloaded = await db.Profiles.AsNoTracking().SingleAsync();
    if (!reloaded.ConversationPrompts.SequenceEqual(expectedPrompts))
        throw new Exception("PostgreSQL prompt value comparer missed an in-place Unicode answer edit.");
    await AssertReleasedDataPreserved();

    // Verify the production provider enforces outbox uniqueness and match erasure.
    var match = new Match { UserAId = user.Id, UserBId = blockedUser.Id };
    db.Matches.Add(match);
    db.MatchNotifications.AddRange(
        new MatchNotification { MatchId = match.Id, RecipientUserId = user.Id, OtherUserId = blockedUser.Id },
        new MatchNotification { MatchId = match.Id, RecipientUserId = blockedUser.Id, OtherUserId = user.Id });
    await db.SaveChangesAsync();
    await using (var duplicate = new NpgsqlCommand("INSERT INTO \"MatchNotifications\" (\"Id\", \"MatchId\", \"RecipientUserId\", \"OtherUserId\", \"CreatedAtUtc\", \"NextAttemptAtUtc\", \"Attempts\") VALUES (@id, @match, @recipient, @other, @now, @now, 0)", connection))
    {
        duplicate.Parameters.AddWithValue("id", Guid.NewGuid());
        duplicate.Parameters.AddWithValue("match", match.Id);
        duplicate.Parameters.AddWithValue("recipient", user.Id);
        duplicate.Parameters.AddWithValue("other", blockedUser.Id);
        duplicate.Parameters.AddWithValue("now", DateTime.UtcNow);
        try
        {
            await duplicate.ExecuteNonQueryAsync();
            throw new Exception("PostgreSQL accepted a duplicate match notification recipient.");
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation) { }
    }
    if (await db.MatchNotifications.CountAsync(n => n.MatchId == match.Id) != 2)
        throw new Exception("Outbox duplicate attempt changed valid recipient rows.");
    var pendingIndex = (long)(await new NpgsqlCommand("SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'MatchNotifications' AND indexname = 'IX_MatchNotifications_SentAtUtc_CancelledAtUtc_NextAttemptAtUtc'", connection).ExecuteScalarAsync())!;
    if (pendingIndex != 1) throw new Exception("PostgreSQL outbox pending-delivery index is missing.");
    await db.Matches.Where(m => m.Id == match.Id).ExecuteDeleteAsync();
    if (await db.MatchNotifications.AnyAsync(n => n.MatchId == match.Id))
        throw new Exception("PostgreSQL match deletion did not erase its notification outbox rows.");
    db.ChangeTracker.Clear();

    await migrator.MigrateAsync(previousRelease);
    await AssertReleasedDataPreserved();
    await migrator.MigrateAsync();
    await AssertReleasedDataPreserved();
    await AssertPromptDefaultAndLegacyFallback();
    Console.WriteLine("PASS: PostgreSQL migrations preserve every seeded legacy field; email/provider-alias defaults and paid balances, Unicode prompt conversion/comparison and match-outbox constraints/cascade verified through repeat, isolated rollback and reapply.");
}
finally
{
    NpgsqlConnection.ClearAllPools();
    await new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
}
