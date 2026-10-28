using System.Net;
using Xunit;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class BrowserChatAuthenticationTests
{
    [Fact]
    public async Task BrowserQueryTokenAuthenticatesChatNegotiation()
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter;
        client.DefaultRequestHeaders.Authorization = null;
        var reply = await client.PostAsync($"/hubs/chat/negotiate?negotiateVersion=1&access_token={token}", null);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Contains("connectionToken", await reply.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("expired")]
    public async Task MissingInvalidAndExpiredBrowserTokensAreRejected(string authentication)
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A, authentication);
        var token = client.DefaultRequestHeaders.Authorization?.Parameter ?? "";
        client.DefaultRequestHeaders.Authorization = null;
        var reply = await client.PostAsync($"/hubs/chat/negotiate?negotiateVersion=1&access_token={token}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
    }

    [Theory]
    [InlineData("/messages/thread")]
    [InlineData("/messages/thread-page")]
    [InlineData("/matches")]
    public async Task QueryTokenCannotAuthenticateOrdinaryApiRoutes(string route)
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter;
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"{route}?otherUserId={app.B}&access_token={token}")).StatusCode);
    }

    [Fact]
    public async Task QueryTokenDoesNotOverrideAnInvalidAuthorizationHeader()
    {
        await using var app = await TestApp.Start();
        using var valid = app.Client(app.A);
        using var invalid = app.Client(app.A, "invalid");
        var token = valid.DefaultRequestHeaders.Authorization!.Parameter;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await invalid.PostAsync($"/hubs/chat/negotiate?negotiateVersion=1&access_token={token}", null)).StatusCode);
    }
}
