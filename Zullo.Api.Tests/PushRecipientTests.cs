using Xunit;
using Zullo.Api.Services;

namespace Zullo.Api.Tests;

public class PushRecipientTests
{
    [Fact]
    public void MessagePushIdentifiesRecipientSeparatelyFromSender()
    {
        var message = PushNotificationService.CreateMessage(
            "device", "sender-A", "Sender", "Hello", null, "recipient-B");

        Assert.Equal("recipient-B", message.Data["recipientUserId"]);
        Assert.Equal("sender-A", message.Data["senderUserId"]);
        Assert.Equal("message", message.Data["type"]);
        Assert.Equal("Hello", message.Notification.Body);
        Assert.Equal("device", message.Token);
    }
}
