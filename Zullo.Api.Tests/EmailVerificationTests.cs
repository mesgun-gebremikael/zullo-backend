using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zullo.Api.Data;
using Zullo.Api.Services;

namespace Zullo.Api.Tests;

public sealed class EmailVerificationTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Sender : IVerificationEmailSender
    {
        public bool IsConfigured { get; set; } = true;
        public string Code = "";
        public int Sends;
        public bool Fail;
        public Action? AfterAccept;
        public bool InternalTimeout;
        public Task SendCodeAsync(string address, string code, CancellationToken cancellationToken)
        { if (InternalTimeout) throw new OperationCanceledException("provider deadline"); if (Fail) throw new IOException("provider failure"); Code = code; Sends++; AfterAccept?.Invoke(); return Task.CompletedTask; }
    }
    private sealed class Harness : IAsyncDisposable
    {
        public readonly SqliteConnection Connection = new("Data Source=:memory:");
        public AppDbContext Db = null!;
        public readonly Sender Mail = new();
        public readonly Clock Time = new();
        public PendingEmailRegistrationService Service = null!;
        public static async Task<Harness> Start()
        {
            var h = new Harness(); await h.Connection.OpenAsync();
            h.Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(h.Connection).Options);
            await h.Db.Database.EnsureCreatedAsync();
            h.Service = new PendingEmailRegistrationService(h.Db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 64) }).Build(), h.Mail, h.Time);
            return h;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
    [Fact] public async Task RegistrationCreatesNoUserAndPersistsOnlyHashesUntilOwnershipIsProven()
    {
        await using var h = await Harness.Start(); var challenge = await h.Service.Begin("person@example.test", "strong-password");
        Assert.Empty(await h.Db.Users.ToListAsync());
        var stored = await h.Db.PendingEmailRegistrations.SingleAsync();
        Assert.NotEqual(h.Mail.Code, stored.CodeHash); Assert.DoesNotContain("strong-password", stored.PasswordHash);
        var user = await h.Service.Verify(challenge.ChallengeId, h.Mail.Code);
        Assert.Equal("person@example.test", user.Email); Assert.NotNull(user.EmailVerifiedAtUtc);
        Assert.True(BCrypt.Net.BCrypt.Verify("strong-password", user.PasswordHash));
        Assert.Empty(stored.PasswordHash); Assert.Empty(stored.CodeHash);
        Assert.Equal("verification_invalid", (await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Verify(challenge.ChallengeId, h.Mail.Code))).Code);
    }
    [Fact] public async Task CorrectPasswordCanResumePendingFormButCannotAuthenticateOrResumeWrongPassword()
    {
        await using var h = await Harness.Start(); var challenge = await h.Service.Begin("person@example.test", "strong-password");
        Assert.Equal(challenge.ChallengeId, (await h.Service.Resume(challenge.Email, "strong-password"))!.ChallengeId);
        Assert.Null(await h.Service.Resume(challenge.Email, "wrong-password")); Assert.Empty(await h.Db.Users.ToListAsync());
    }
    [Fact] public async Task FiveWrongCodesExhaustChallengeIncludingCorrectCodeAfterward()
    {
        await using var h = await Harness.Start(); var challenge = await h.Service.Begin("person@example.test", "strong-password"); var correct = h.Mail.Code;
        var wrong = correct == "000000" ? "999999" : "000000";
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Verify(challenge.ChallengeId, wrong));
        await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Verify(challenge.ChallengeId, correct));
        Assert.Empty(await h.Db.Users.ToListAsync());
    }
    [Fact] public async Task ExpiredCodeAndOldResentChallengeCannotCreateUser()
    {
        await using var h = await Harness.Start(); var original = await h.Service.Begin("person@example.test", "strong-password"); var code = h.Mail.Code;
        h.Time.Now += TimeSpan.FromMinutes(16);
        await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Verify(original.ChallengeId, code));
        var replacement = await h.Service.Resend(original.ChallengeId);
        Assert.NotEqual(original.ChallengeId, replacement.ChallengeId);
        await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Verify(original.ChallengeId, code));
        Assert.NotNull(await h.Service.Verify(replacement.ChallengeId, h.Mail.Code));
    }
    [Fact] public async Task ResendBudgetCannotBeResetByRegisteringAgain()
    {
        await using var h = await Harness.Start(); var challenge = await h.Service.Begin("person@example.test", "strong-password");
        Assert.Equal(429, (await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Resend(challenge.ChallengeId))).Status);
        for (var i = 1; i < 5; i++) { h.Time.Now += TimeSpan.FromSeconds(61); challenge = await h.Service.Resend(challenge.ChallengeId); }
        h.Time.Now += TimeSpan.FromSeconds(61);
        Assert.Equal(429, (await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Begin(challenge.Email, "different-password"))).Status);
        Assert.Equal(5, h.Mail.Sends);
    }
    [Fact] public async Task MissingSenderAndDeliveryFailureNeverCreateUserOrClaimDelivery()
    {
        await using var h = await Harness.Start(); h.Mail.IsConfigured = false;
        Assert.Equal(503, (await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Begin("person@example.test", "strong-password"))).Status);
        Assert.Empty(await h.Db.PendingEmailRegistrations.ToListAsync());
        h.Mail.IsConfigured = true; h.Mail.Fail = true;
        Assert.Equal(503, (await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Begin("person@example.test", "strong-password"))).Status);
        Assert.Empty(await h.Db.Users.ToListAsync());
        var failed = await h.Db.PendingEmailRegistrations.AsNoTracking().SingleAsync();
        Assert.Null(failed.DeliveryConfirmedAtUtc);
        Assert.Equal(1, failed.SendCount);
        Assert.True(failed.NextSendAtUtc > h.Time.Now.UtcDateTime);
    }
    [Fact] public async Task ProviderFailureDoesNotResetCooldownOrHourlySendBudget()
    {
        await using var h = await Harness.Start(); h.Mail.Fail = true;
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(503, (await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Begin("person@example.test", "strong-password"))).Status);
            Assert.Equal(429, (await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Begin("person@example.test", "strong-password"))).Status);
            h.Time.Now += TimeSpan.FromSeconds(61);
        }
        Assert.Equal(5, (await h.Db.PendingEmailRegistrations.AsNoTracking().SingleAsync()).SendCount);
        Assert.Equal(429, (await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Begin("person@example.test", "strong-password"))).Status);
        Assert.Empty(await h.Db.Users.ToListAsync());
    }

    [Fact] public async Task InternalProviderCancellationIsUnavailableAndKeepsRateReservation()
    {
        await using var h = await Harness.Start(); h.Mail.InternalTimeout = true;
        var failure = await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Begin("person@example.test", "strong-password"));
        Assert.Equal(503, failure.Status);
        Assert.Equal("verification_delivery_failed", failure.Code);
        Assert.Equal(1, (await h.Db.PendingEmailRegistrations.AsNoTracking().SingleAsync()).SendCount);
        Assert.Empty(await h.Db.Users.ToListAsync());
    }

    [Fact] public async Task AcceptedMailRemainsVerifiableWhenBrowserClosesBeforeConfirmationWrite()
    {
        await using var h = await Harness.Start();
        using var browser = new CancellationTokenSource();
        h.Mail.AfterAccept = browser.Cancel;
        var challenge = await h.Service.Begin("person@example.test", "strong-password", browser.Token);
        Assert.True(browser.IsCancellationRequested);
        Assert.NotNull((await h.Db.PendingEmailRegistrations.AsNoTracking().SingleAsync()).DeliveryConfirmedAtUtc);
        Assert.NotNull(await h.Service.Verify(challenge.ChallengeId, h.Mail.Code));
    }

    [Fact] public async Task ResendRemovesOldDeliveryProofBeforeSendingNewCode()
    {
        await using var h = await Harness.Start();
        var original = await h.Service.Begin("person@example.test", "strong-password");
        h.Time.Now += TimeSpan.FromSeconds(61);
        h.Mail.Fail = true;
        await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Resend(original.ChallengeId));
        var replacement = await h.Db.PendingEmailRegistrations.AsNoTracking().SingleAsync();
        Assert.NotEqual(original.ChallengeId, replacement.ChallengeId);
        Assert.Null(replacement.DeliveryConfirmedAtUtc);
        Assert.Empty(await h.Db.Users.ToListAsync());
        await Assert.ThrowsAsync<EmailVerificationFailure>(() => h.Service.Verify(replacement.ChallengeId, h.Mail.Code));
    }

    [Theory]
    [InlineData("person@example.test", true)] [InlineData("a+b@sub.example.org", true)]
    [InlineData("person@localhost", false)] [InlineData("a..b@gmail.com", false)]
    [InlineData("a@-example.com", false)] [InlineData("a@b.com\r\nBcc:c@d.com", false)]
    public void SyntaxIsConservativeWithoutBlockingCustomProviders(string input, bool valid) => Assert.Equal(valid, EmailAddressPolicy.TryNormalize(input, out _));
    [Fact] public void ProviderTyposAreSuggestedNotSilentlyRewritten()
    { Assert.True(EmailAddressPolicy.TryNormalize("Person@gmail.ocm", out var normalized)); Assert.Equal("person@gmail.ocm", normalized); Assert.Equal("person@gmail.com", EmailAddressPolicy.SuggestAddress(normalized)); }
}
