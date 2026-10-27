using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Zullo.Api.Data;
using Zullo.Api.Models;

NpgsqlConnection.GlobalTypeMapper.EnableDynamicJson();
var database = "zullo_profile_" + Guid.NewGuid().ToString("N");
var adminConnection = Environment.GetEnvironmentVariable("ZULLO_PROFILE_TEST_ADMIN_CONNECTION")
    ?? "Host=127.0.0.1;Port=5432;Database=zullo;Username=zullo";
var connectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database }.ConnectionString;
await using var admin = new NpgsqlConnection(adminConnection);
await admin.OpenAsync();
await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin).ExecuteNonQueryAsync();
try
{
    await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
    var migrator = db.GetService<IMigrator>();
    await migrator.MigrateAsync("20260408213102_AddClientMessageIdToMessage");
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    var user = new User { Email = "migration@example.test", MatchRadiusKm = 250 };
    var profile = new Profile { UserId = user.Id, DisplayName = "Legacy name", Age = 29,
        Gender = "Kvinna", Bio = "Legacy bio", Workout = "Ofta", Smoking = "Nej", HeightCm = 175,
        LivePlace = "Stockholm", OriginPlace = "Asmara", Lat = 59.3293, Lng = 18.0686,
        PhotoUrls = new() { "https://images.example/a", "https://images.example/b" },
        Interests = new() { "Musik", "Resor" }, IsVisible = true };
    foreach (var entity in new object[] { user, profile })
    {
        var entityType = db.Model.FindEntityType(entity.GetType())!;
        var properties = entityType.GetProperties().Where(p => p.Name != "InterestedIn" && p.PropertyInfo?.GetValue(entity) != null).ToArray();
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
    async Task<string> Snapshot() => (string)(await new NpgsqlCommand("SELECT (to_jsonb(p) - 'InterestedIn')::text FROM \"Profiles\" p", connection).ExecuteScalarAsync())!;
    var before = await Snapshot();
    await migrator.MigrateAsync();
    if (await Snapshot() != before) throw new Exception("Forward migration changed existing profile fields.");
    var restored = await db.Profiles.AsNoTracking().SingleAsync();
    if (restored.InterestedIn != "" || restored.Workout != "Ofta" || restored.HeightCm != 175 || restored.PhotoUrls.Count != 2)
        throw new Exception("Existing values or backward-compatible preference default changed.");
    await migrator.MigrateAsync(); // repeatability
    if (await Snapshot() != before) throw new Exception("Repeated migration changed data.");
    await migrator.MigrateAsync("20260408213102_AddClientMessageIdToMessage");
    if (await Snapshot() != before) throw new Exception("Rollback changed existing profile fields.");
    await migrator.MigrateAsync();
    if (await Snapshot() != before) throw new Exception("Reapplying migration changed existing profile fields.");
    Console.WriteLine("PASS: PostgreSQL migration, repeat, rollback and reapply preserve all seeded profile fields.");
}
finally
{
    NpgsqlConnection.ClearAllPools();
    await new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
}
