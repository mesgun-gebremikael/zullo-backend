using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using Zullo.Api.Controllers;
using Zullo.Api.Data;
using Zullo.Api.Dtos;
using Zullo.Api.Services;

namespace Zullo.Api.Tests;

public class RegistrationConflictTests
{
    [Fact]
    public async Task ConcurrentEmailInsertReturnsExistingAccountErrorWithoutToken()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new RaceDb(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options,
            "IX_User_Email");
        await db.Database.EnsureCreatedAsync();
        using var limiter = new LoginAttemptService();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 64) }).Build();
        var sender = new TestVerificationEmailSender();
        var verifier = new PendingEmailRegistrationService(db, config, sender);
        var controller = new AuthController(db, config, limiter, verifier);
        var pending = await verifier.Begin("race@example.test", "password123");
        var result = Assert.IsType<ConflictObjectResult>(await controller.VerifyEmail(new VerifyEmailRequest { ChallengeId = pending.ChallengeId, Code = sender.Code }));
        Assert.Equal("email_in_use", result.Value!.GetType().GetProperty("code")!.GetValue(result.Value));
        Assert.True(db.SaveAttempted);
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task UnrelatedDatabaseConstraintIsNotMisreportedAsDuplicateEmail()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new RaceDb(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options,
            "SomeOtherConstraint");
        await db.Database.EnsureCreatedAsync();
        using var limiter = new LoginAttemptService();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 64) }).Build();
        var sender = new TestVerificationEmailSender();
        var verifier = new PendingEmailRegistrationService(db, config, sender);
        var controller = new AuthController(db, config, limiter, verifier);
        var pending = await verifier.Begin("other@example.test", "password123");
        await Assert.ThrowsAsync<DbUpdateException>(() => controller.VerifyEmail(new VerifyEmailRequest { ChallengeId = pending.ChallengeId, Code = sender.Code }));
    }

    // Emulates PostgreSQL rejecting a second request after its existence check.
    private sealed class RaceDb(DbContextOptions<AppDbContext> options, string constraint) : AppDbContext(options)
    {
        public bool SaveAttempted { get; private set; }
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!ChangeTracker.Entries<Zullo.Api.Models.User>().Any(e => e.State == EntityState.Added)) return base.SaveChangesAsync(cancellationToken);
            SaveAttempted = true;
            throw new DbUpdateException("Insert conflict", new PostgresException(
                "duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
                constraintName: constraint));
        }
    }
}
