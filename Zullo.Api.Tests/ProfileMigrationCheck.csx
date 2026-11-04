using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Zullo.Api.Data;
using Zullo.Api.Models;

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
    async Task<string> BlockSnapshot() => (string)(await new NpgsqlCommand("SELECT (to_jsonb(b) - 'SnapshotDisplayName' - 'SnapshotAge' - 'SnapshotPhotoUrl')::text FROM \"Blocks\" b", connection).ExecuteScalarAsync())!;
    var blockBefore = await BlockSnapshot();
    async Task<string> Snapshot() => (string)(await new NpgsqlCommand("SELECT (to_jsonb(p) - 'InterestedIn')::text FROM \"Profiles\" p", connection).ExecuteScalarAsync())!;
    var before = await Snapshot();
    await migrator.MigrateAsync();
    if (await Snapshot() != before) throw new Exception("Forward migration changed existing profile fields.");
    if (await BlockSnapshot() != blockBefore) throw new Exception("Forward migration changed existing block relationship.");
    var restoredBlock = await db.Blocks.AsNoTracking().SingleAsync();
    if (restoredBlock.Id != blockId || restoredBlock.SnapshotDisplayName != "" || restoredBlock.SnapshotAge != null || restoredBlock.SnapshotPhotoUrl != "")
        throw new Exception("Legacy block snapshot defaults changed.");
    var restored = await db.Profiles.AsNoTracking().SingleAsync();
    if (restored.InterestedIn != "" || restored.Workout != "Ofta" || restored.HeightCm != 175 || restored.PhotoUrls.Count != 2)
        throw new Exception("Existing values or backward-compatible preference default changed.");
    await migrator.MigrateAsync(); // repeatability
    if (await Snapshot() != before) throw new Exception("Repeated migration changed data.");
    await migrator.MigrateAsync("20260408213102_AddClientMessageIdToMessage");
    if (await Snapshot() != before) throw new Exception("Rollback changed existing profile fields.");
    await migrator.MigrateAsync();
    if (await Snapshot() != before) throw new Exception("Reapplying migration changed existing profile fields.");
    if (await BlockSnapshot() != blockBefore) throw new Exception("Repeated migration, rollback or reapply changed existing block relationship.");
    Console.WriteLine("PASS: PostgreSQL migration, repeat, rollback and reapply preserve all seeded profile fields and blocked relationships.");
}
finally
{
    NpgsqlConnection.ClearAllPools();
    await new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
}
