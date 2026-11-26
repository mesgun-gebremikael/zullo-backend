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
        Assert.Null(message.Notification);
        Assert.Equal("ZULLO", message.Android.Notification.Title);
        Assert.Equal("zullo_message_body", message.Android.Notification.BodyLocKey);
        Assert.Equal("zullo_message_body", message.Apns.Aps.Alert.LocKey);
        Assert.False(message.Data.ContainsKey("senderName"));
        Assert.False(message.Data.ContainsKey("messageText"));
        Assert.False(message.Data.ContainsKey("senderPhotoUrl"));
        Assert.Equal("device", message.Token);
    }
}
