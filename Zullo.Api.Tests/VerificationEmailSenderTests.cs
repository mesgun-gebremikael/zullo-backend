using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using Xunit;
using Zullo.Api.Services;

namespace Zullo.Api.Tests;

public class VerificationEmailSenderTests
{
    private sealed class Transport(string failure = "") : IVerificationSmtpTransport
    {
        public bool Sent, Disposed;
        public SecureSocketOptions Security;
        public Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken ct)
        { Security = security; return Step("connect", ct); }
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => Step("auth", ct);
        public Task SendAsync(MimeMessage message, CancellationToken ct)
        { if (failure == "send") throw new IOException("send failure"); Sent = true; return Task.CompletedTask; }
        public Task DisconnectAsync(bool quit, CancellationToken ct) => Step("disconnect", ct);
        private Task Step(string stage, CancellationToken ct) => failure == "hang" ? Task.Delay(Timeout.Infinite, ct) : failure == stage ? throw new IOException(stage) : Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
    private static IConfiguration Settings(string mode = "StartTls") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["EmailVerification:Smtp:Enabled"] = "true", ["EmailVerification:Smtp:Host"] = "smtp.example.test",
        ["EmailVerification:Smtp:Port"] = "587", ["EmailVerification:Smtp:FromAddress"] = "verification@example.test",
        ["EmailVerification:Smtp:Username"] = "configured-sender", ["EmailVerification:Smtp:Password"] = "test-only-secret",
        ["EmailVerification:Smtp:Security"] = mode,
    }).Build();

    [Theory]
    [InlineData("connect")]
    [InlineData("auth")]
    [InlineData("send")]
    public async Task ConnectAuthenticationAndSendFailuresNeverClaimAcceptedDelivery(string stage)
    {
        var transport = new Transport(stage);
        var sender = new SmtpVerificationEmailSender(Settings(), () => transport);
        await Assert.ThrowsAsync<IOException>(() => sender.SendCodeAsync("owner@example.test", "123456", default));
        Assert.False(transport.Sent);
        Assert.True(transport.Disposed);
    }
    [Fact]
    public async Task EntireDeliveryHasABudgetEvenWhenProviderDoesNotRespond()
    {
        var transport = new Transport("hang");
        var sender = new SmtpVerificationEmailSender(Settings(), () => transport, TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendCodeAsync("owner@example.test", "123456", default).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(transport.Sent); Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task AcceptedMessageRemainsAcceptedWhenDisconnectFails()
    {
        var transport = new Transport("disconnect");
        var sender = new SmtpVerificationEmailSender(Settings(), () => transport);
        await sender.SendCodeAsync("owner@example.test", "123456", default);
        Assert.True(transport.Sent); Assert.True(transport.Disposed);
        Assert.Equal(SecureSocketOptions.StartTls, transport.Security);
    }
    [Theory]
    [InlineData("EmailVerification:Smtp:Port", "not-a-port")]
    [InlineData("EmailVerification:Smtp:Port", "65536")]
    [InlineData("EmailVerification:Smtp:Enabled", "not-a-boolean")]
    public void InvalidConfigurationIsUnavailableInsteadOfCrashingRegistration(string key, string value)
    {
        var configuration = Settings();
        configuration[key] = value;
        var sender = new SmtpVerificationEmailSender(configuration);
        Assert.False(sender.IsConfigured);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Auto")]
    [InlineData("StartTlsWhenAvailable")]
    public async Task OpportunisticOrPlaintextTransportIsNotConfigured(string mode)
    {
        var factoryCalled = false;
        var sender = new SmtpVerificationEmailSender(Settings(mode), () => { factoryCalled = true; return new Transport(); });
        Assert.False(sender.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendCodeAsync("owner@example.test", "123456", default));
        Assert.False(factoryCalled);
    }
}
