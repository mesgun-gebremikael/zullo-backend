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
        var controller = new AuthController(db, new ConfigurationBuilder().Build(), limiter);
        var result = Assert.IsType<BadRequestObjectResult>(await controller.Register(new RegisterRequestDto
        { Email = "race@example.test", Password = "password123" }));
        Assert.Equal("User already exists.", Assert.IsType<ErrorMessageResponseDto>(result.Value).Message);
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
        var controller = new AuthController(db, new ConfigurationBuilder().Build(), limiter);
        await Assert.ThrowsAsync<DbUpdateException>(() => controller.Register(new RegisterRequestDto
        { Email = "other@example.test", Password = "password123" }));
    }

    // Emulates PostgreSQL rejecting a second request after its existence check.
    private sealed class RaceDb(DbContextOptions<AppDbContext> options, string constraint) : AppDbContext(options)
    {
        public bool SaveAttempted { get; private set; }
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveAttempted = true;
            throw new DbUpdateException("Insert conflict", new PostgresException(
                "duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
                constraintName: constraint));
        }
    }
}
