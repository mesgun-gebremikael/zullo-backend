using MailKit.Security;
using MimeKit;

namespace Zullo.Api.Services;

public interface IVerificationEmailSender
{
    bool IsConfigured { get; }
    Task SendCodeAsync(string address, string code, CancellationToken cancellationToken);
}

/// Uses an explicitly configured SMTP provider with certificate validation and
/// mandatory TLS. No console/pickup fake delivery exists in production code.
public sealed class SmtpVerificationEmailSender : IVerificationEmailSender
{
    private readonly IConfiguration configuration;
    private readonly Func<IVerificationSmtpTransport> createTransport;
    private readonly TimeSpan deliveryBudget;
    public SmtpVerificationEmailSender(IConfiguration configuration)
        : this(configuration, () => new MailKitVerificationSmtpTransport()) { }
    internal SmtpVerificationEmailSender(IConfiguration configuration, Func<IVerificationSmtpTransport> createTransport, TimeSpan? deliveryBudget = null)
    { this.configuration = configuration; this.createTransport = createTransport; this.deliveryBudget = deliveryBudget ?? TimeSpan.FromSeconds(12); }

    private sealed record Settings(string Host, int Port, string From, string? Username, string? Password, SecureSocketOptions Security);
    private Settings? ReadSettings()
    {
        var host = configuration["EmailVerification:Smtp:Host"];
        var from = configuration["EmailVerification:Smtp:FromAddress"];
        var validPort = int.TryParse(configuration["EmailVerification:Smtp:Port"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port);
        var username = configuration["EmailVerification:Smtp:Username"];
        var password = configuration["EmailVerification:Smtp:Password"];
        var mode = configuration["EmailVerification:Smtp:Security"];
        if (!bool.TryParse(configuration["EmailVerification:Smtp:Enabled"], out var enabled) || !enabled || string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(from) || !MailboxAddress.TryParse(from, out var mailbox) || !validPort || port is not (> 0 and <= 65535)
            || mode is not ("StartTls" or "SslOnConnect") || (!string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))) return null;
        return new Settings(host, port, mailbox.Address, username, password,
            mode == "SslOnConnect" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
    }
    public bool IsConfigured => ReadSettings() is not null;

    public async Task SendCodeAsync(string address, string code, CancellationToken cancellationToken)
    {
        var settings = ReadSettings() ?? throw new InvalidOperationException("Verification email delivery is unavailable.");
        var email = new MimeMessage();
        email.From.Add(new MailboxAddress("ZULLO", settings.From));
        email.To.Add(MailboxAddress.Parse(address));
        email.Subject = $"ZULLO · {code}";
        email.Body = new TextPart("plain") { Text =
            $"Your ZULLO email verification code is {code}.\n\nIt expires in 15 minutes. Never share this code.\nIf you did not request a ZULLO account, ignore this email." };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(deliveryBudget);
        var deliveryToken = deadline.Token;
        using var client = createTransport();
        await client.ConnectAsync(settings.Host, settings.Port, settings.Security, deliveryToken);
        if (!string.IsNullOrEmpty(settings.Username))
            await client.AuthenticateAsync(settings.Username, settings.Password ?? throw new InvalidOperationException("SMTP credentials are unavailable."), deliveryToken);
        await client.SendAsync(email, deliveryToken);
        // SMTP already acknowledged the message. A disconnect failure must
        // not invalidate an accepted verification email or trigger a resend.
        try { await client.DisconnectAsync(true, deliveryToken); }
        catch (Exception) { /* Disposal still closes the transport. */ }
    }
}

// Narrow transport seam lets delivery/close failure boundaries be verified
// without a network server, credentials or fake production delivery modes.
internal interface IVerificationSmtpTransport : IDisposable
{
    Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellation);
    Task AuthenticateAsync(string username, string password, CancellationToken cancellation);
    Task SendAsync(MimeMessage message, CancellationToken cancellation);
    Task DisconnectAsync(bool quit, CancellationToken cancellation);
}

internal sealed class MailKitVerificationSmtpTransport : IVerificationSmtpTransport
{
    private readonly MailKit.Net.Smtp.SmtpClient client = new() { Timeout = 10000 };
    public Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellation) => client.ConnectAsync(host, port, security, cancellation);
    public Task AuthenticateAsync(string username, string password, CancellationToken cancellation) => client.AuthenticateAsync(username, password, cancellation);
    public async Task SendAsync(MimeMessage message, CancellationToken cancellation) => await client.SendAsync(message, cancellation);
    public Task DisconnectAsync(bool quit, CancellationToken cancellation) => client.DisconnectAsync(quit, cancellation);
    public void Dispose() => client.Dispose();
}
