using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zullo.Api.Controllers;
using Zullo.Api.Data;
using Zullo.Api.Dtos;
using Zullo.Api.Models;
using Zullo.Api.Services;

namespace Zullo.Api.Tests;

public class Matchningstester
{
    [Fact]
    public async Task EnsidigLike_SkaparIngenMatchning()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = SkapaDatabas(connection);
        var a = new User();
        var b = new User();
        db.Users.AddRange(a, b);
        await db.SaveChangesAsync();

        var svar = await SkapaController(db, a.Id).Like(new SwipeTargetDto { TargetUserId = b.Id });

        var resultat = Assert.IsType<OkObjectResult>(svar);
        Assert.False(Assert.IsType<LikeResponseDto>(resultat.Value).Matched);
        Assert.Single(await db.Likes.ToListAsync());
        Assert.Empty(await db.Matches.ToListAsync());
    }

    [Fact]
    public async Task OmsesidigaLikes_SkaparExaktEnMatchning()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = SkapaDatabas(connection);
        var a = new User();
        var b = new User();
        db.Users.AddRange(a, b);
        await db.SaveChangesAsync();

        var forsta = await SkapaController(db, a.Id).Like(new SwipeTargetDto { TargetUserId = b.Id });
        var andra = await SkapaController(db, b.Id).Like(new SwipeTargetDto { TargetUserId = a.Id });

        Assert.False(Assert.IsType<LikeResponseDto>(Assert.IsType<OkObjectResult>(forsta).Value).Matched);
        Assert.True(Assert.IsType<LikeResponseDto>(Assert.IsType<OkObjectResult>(andra).Value).Matched);

        var matchning = Assert.Single(await db.Matches.ToListAsync());
        Assert.Equal(2, await db.Likes.CountAsync());
        Assert.Equal(a.Id.CompareTo(b.Id) < 0 ? a.Id : b.Id, matchning.UserAId);
        Assert.Equal(a.Id.CompareTo(b.Id) < 0 ? b.Id : a.Id, matchning.UserBId);
    }

    private static AppDbContext SkapaDatabas(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static SwipeController SkapaController(AppDbContext db, Guid userId)
    {
        var controller = new SwipeController(
            db,
            new LikeLimitService(db),
            new UserRelationService(db));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
                    "Test"))
            }
        };
        return controller;
    }
}
