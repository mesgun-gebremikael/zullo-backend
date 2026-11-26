using Zullo.Api.Services;
namespace Zullo.Api.Tests;
internal sealed class TestVerificationEmailSender : IVerificationEmailSender
{
    public bool IsConfigured => true;
    public string Code { get; private set; } = "";
    public Task SendCodeAsync(string address, string code, CancellationToken cancellationToken)
    { Code = code; return Task.CompletedTask; }
}
