using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zullo.Api.Models;
using Zullo.Api.Services;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;
namespace Zullo.Api.Tests;

public class OnboardingDraftTests
{
    static object Draft(long revision = 0, int step = 0, object? data = null) => new { revision, step, data = data ?? new { displayName = "Anna", birthDate = "1998-05-02", gender = "Kvinna", photoUrls = Array.Empty<string>() } };
    [Fact]
    public async Task PrivateDraftResumesForItsOwnerWithoutPublishingAProfile()
    {
        await using var app = await TestApp.Start(); using var a = app.Client(app.A); using var b = app.Client(app.B); using var anon = app.Client(app.A,"missing");
        Assert.Equal(HttpStatusCode.Unauthorized,(await anon.GetAsync("/me/onboarding")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await a.GetAsync("/me/onboarding")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await a.PutAsJsonAsync("/me/onboarding", Draft(step:7))).StatusCode);
        var saved = await a.GetFromJsonAsync<JsonElement>("/me/onboarding");
        Assert.Equal(7,saved.GetProperty("step").GetInt32()); Assert.Equal("Anna",saved.GetProperty("data").GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.NotFound,(await b.GetAsync("/me/onboarding")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await a.GetAsync("/me/profile")).StatusCode);
        await using var db = app.Db(); Assert.Empty(await db.Profiles.ToListAsync()); Assert.Single(await db.OnboardingDrafts.ToListAsync());
    }
    [Fact]
    public async Task RetriedIdenticalWriteIsIdempotentAndStaleChangedWriteCannotOverwrite()
    {
        await using var app = await TestApp.Start(); using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.OK,(await a.PutAsJsonAsync("/me/onboarding",Draft())).StatusCode);
        var retry = await a.PutAsJsonAsync("/me/onboarding",Draft()); Assert.Equal(HttpStatusCode.OK,retry.StatusCode);
        Assert.Equal(1,(await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict,(await a.PutAsJsonAsync("/me/onboarding",Draft(step:3))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await a.PutAsJsonAsync("/me/onboarding",Draft(revision:1,step:3))).StatusCode);
        Assert.Equal(2,(await a.GetFromJsonAsync<JsonElement>("/me/onboarding")).GetProperty("revision").GetInt64());
    }
    [Theory]
    [InlineData("{\"distanceKm\":\"oops\"}")]
    [InlineData("{\"heightCm\":true}")]
    [InlineData("{\"userId\":\"another-user\"}")]
    [InlineData("{\"photoUrls\":\"bad\"}")]
    [InlineData("{\"photoUrls\":[null]}")]
    [InlineData("{\"birthDate\":\"3000-01-01\"}")]
    [InlineData("{\"isVerified\":true}")]
    [InlineData("{\"displayName\":\"nul\\u0000text\"}")]
    [InlineData("{\"distanceKm\":301}")]
    public async Task InvalidDraftFieldsAreRejectedWithoutServerErrors(string data)
    {
        await using var app = await TestApp.Start(); using var a = app.Client(app.A);
        var response = await a.PutAsJsonAsync("/me/onboarding", Draft(data:JsonSerializer.Deserialize<JsonElement>(data)));
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        await using var db = app.Db(); Assert.Empty(await db.OnboardingDrafts.ToListAsync());
    }
    [Fact]
    public async Task DraftCannotReferenceSomeoneElsesOrAnExpiredPhoto()
    {
        await using var app = await TestApp.Start(); using var a = app.Client(app.A);
        await using var db = app.Db();
        var foreign = new OwnedPhoto { Id=Guid.NewGuid(),UserId=app.B,State=PhotoStates.Ready,Url="https://res.cloudinary.com/test/foreign.jpg",NextAttemptUtc=DateTime.UtcNow.AddDays(1) };
        var expired = new OwnedPhoto { Id=Guid.NewGuid(),UserId=app.A,State=PhotoStates.Ready,Url="https://res.cloudinary.com/test/expired.jpg",NextAttemptUtc=DateTime.UtcNow.AddMinutes(-1) };
        db.OwnedPhotos.AddRange(foreign,expired); await db.SaveChangesAsync();
        foreach(var url in new[]{foreign.Url,expired.Url}) Assert.Equal(HttpStatusCode.BadRequest,(await a.PutAsJsonAsync("/me/onboarding",Draft(data:new { photoUrls=new[]{url} }))).StatusCode);
        Assert.Empty(await db.OnboardingDrafts.ToListAsync());
    }
    [Fact]
    public async Task SelectedDraftPhotoIsKeptSevenDaysAndRemovingItQueuesCleanup()
    {
        await using var app = await TestApp.Start(); using var a = app.Client(app.A);
        var id=Guid.NewGuid(); const string url="https://res.cloudinary.com/test/owned.jpg";
        await using (var db=app.Db()) {db.OwnedPhotos.Add(new OwnedPhoto{Id=id,UserId=app.A,State=PhotoStates.Ready,Url=url,NextAttemptUtc=DateTime.UtcNow.AddHours(1)});await db.SaveChangesAsync();}
        Assert.Equal(HttpStatusCode.OK,(await a.PutAsJsonAsync("/me/onboarding",Draft(data:new {photoUrls=new[]{url}}))).StatusCode);
        await using(var db=app.Db()){var photo=await db.OwnedPhotos.FindAsync(id);Assert.Equal(PhotoStates.Ready,photo!.State);Assert.True(photo.NextAttemptUtc>DateTime.UtcNow.AddDays(6));}
        await a.PutAsJsonAsync("/me/onboarding",Draft(revision:1,data:new {photoUrls=Array.Empty<string>()}));
        await using(var db=app.Db()){Assert.Equal(PhotoStates.Pending,(await db.OwnedPhotos.FindAsync(id))!.State);}
    }
    [Fact]
    public async Task ExpiredDraftPhotosAreReportedWithoutLosingTheOtherAnswers()
    {
        await using var app = await TestApp.Start();using var a=app.Client(app.A);
        var id=Guid.NewGuid();const string url="https://res.cloudinary.com/test/owned.jpg";
        await using(var db=app.Db()){db.OwnedPhotos.Add(new OwnedPhoto{Id=id,UserId=app.A,State=PhotoStates.Ready,Url=url,NextAttemptUtc=DateTime.UtcNow.AddHours(1)});await db.SaveChangesAsync();}
        await a.PutAsJsonAsync("/me/onboarding",Draft(data:new{displayName="Saved name",photoUrls=new[]{url}}));
        await using(var db=app.Db()){(await db.OwnedPhotos.FindAsync(id))!.State=PhotoStates.Deleted;await db.SaveChangesAsync();}
        var saved=await a.GetFromJsonAsync<JsonElement>("/me/onboarding");
        Assert.True(saved.GetProperty("photosExpired").GetBoolean());Assert.Empty(saved.GetProperty("data").GetProperty("photoUrls").EnumerateArray());Assert.Equal("Saved name",saved.GetProperty("data").GetProperty("displayName").GetString());
    }
    [Fact]
    public async Task FinalProfileSaveAndAccountClosureRemoveTheirPrivateDrafts()
    {
        await using var app=await TestApp.Start();using var a=app.Client(app.A);using var b=app.Client(app.B);
        await a.PutAsJsonAsync("/me/onboarding",Draft());await b.PutAsJsonAsync("/me/onboarding",Draft());
        Assert.Equal(HttpStatusCode.BadRequest,(await a.PostAsJsonAsync("/me/profile",new{displayName="Anna",age=25,gender="Kvinna"})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await a.GetAsync("/me/onboarding")).StatusCode);
        var urls=new[]{"https://res.cloudinary.com/test/a.jpg","https://res.cloudinary.com/test/b.jpg"};
        await using(var db=app.Db()){foreach(var url in urls) db.OwnedPhotos.Add(new OwnedPhoto{Id=Guid.NewGuid(),UserId=app.A,State=PhotoStates.Ready,Url=url,NextAttemptUtc=DateTime.UtcNow.AddHours(1)});await db.SaveChangesAsync();}
        Assert.Equal(HttpStatusCode.OK,(await a.PostAsJsonAsync("/me/profile",new{displayName="Anna",age=25,gender="Kvinna",photoUrls=urls})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await a.GetAsync("/me/onboarding")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await a.PutAsJsonAsync("/me/onboarding",Draft())).StatusCode);
        await using(var db=app.Db()){(await db.Users.FindAsync(app.B))!.PasswordHash=BCrypt.Net.BCrypt.HashPassword("test-password");await db.SaveChangesAsync();}
        Assert.Equal(HttpStatusCode.Accepted,(await b.PostAsJsonAsync("/me/account/removal",new{password="test-password",confirm=true})).StatusCode);
        await using(var db=app.Db()){Assert.Empty(await db.OnboardingDrafts.ToListAsync());}
    }
}
