using FirebaseAdmin.Messaging;

namespace Zullo.Api.Services;

public class PushNotificationService
{
    public virtual async Task SendMessageNotificationAsync(string deviceToken,
        string senderUserId, string senderName, string messageText,
        string? senderPhotoUrl, string recipientUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken)) return;
        await FirebaseMessaging.DefaultInstance.SendAsync(CreateMessage(deviceToken,
            senderUserId, senderName, messageText, senderPhotoUrl, recipientUserId), cancellationToken);
    }

    public virtual async Task SendMatchNotificationAsync(string deviceToken,
        string otherUserId, string recipientUserId, string matchId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken)) return;
        await FirebaseMessaging.DefaultInstance.SendAsync(CreateMatch(deviceToken,
            otherUserId, recipientUserId, matchId), cancellationToken);
    }

    public virtual async Task SendSuperLikeNotificationAsync(string deviceToken,
        string recipientUserId, string eventId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken)) return;
        await FirebaseMessaging.DefaultInstance.SendAsync(CreateSuperLike(deviceToken,
            recipientUserId, eventId), cancellationToken);
    }

    internal static Message CreateMessage(string deviceToken, string senderUserId,
        string senderName, string messageText, string? senderPhotoUrl, string recipientUserId)
    {
        // An already queued OS alert can outlive logout/account switching. Never
        // put another account's sender name, photo or message text in that alert.
        var android = Android("ZULLO", null);
        android.Notification.BodyLocKey = "zullo_message_body";
        return new Message {
            Token = deviceToken,
            Android = android,
            Apns = new ApnsConfig {
                Headers = new Dictionary<string, string> { ["apns-push-type"] = "alert", ["apns-priority"] = "10" },
                Aps = new Aps { Sound = "default", Alert = new ApsAlert { Title = "ZULLO", LocKey = "zullo_message_body" } }
            },
            Data = new Dictionary<string, string> {
                ["type"] = "message", ["recipientUserId"] = recipientUserId,
                ["senderUserId"] = senderUserId
            }
        };
    }

    internal static Message CreateMatch(string token, string peer, string recipient, string matchId)
    {
        var collapse = $"match-{matchId}";
        var android = Android(null, null);
        android.CollapseKey = collapse;
        android.Notification.Tag = collapse;
        android.Notification.TitleLocKey = "zullo_match_title";
        android.Notification.BodyLocKey = "zullo_match_body";
        return new Message {
            Token = token,
            Android = android,
            Apns = new ApnsConfig {
                Headers = new Dictionary<string, string> { ["apns-collapse-id"] = collapse, ["apns-push-type"] = "alert", ["apns-priority"] = "10" },
                Aps = new Aps { Sound = "default", Alert = new ApsAlert { TitleLocKey = "zullo_match_title", LocKey = "zullo_match_body" } }
            },
            Data = new Dictionary<string, string> {
                ["type"] = "match", ["recipientUserId"] = recipient,
                ["senderUserId"] = peer, ["matchId"] = matchId,
                ["eventId"] = collapse
            }
        };
    }

    internal static Message CreateSuperLike(string token, string recipient, string eventId)
    {
        var collapse = $"superlike-{eventId}";
        var android = Android("ZULLO ⭐", null);
        android.CollapseKey = collapse;
        android.Notification.Tag = collapse;
        android.Notification.BodyLocKey = "zullo_superlike_body";
        return new Message {
            Token = token,
            Android = android,
            Apns = new ApnsConfig {
                Headers = new Dictionary<string, string> { ["apns-collapse-id"] = collapse, ["apns-push-type"] = "alert" },
                Aps = new Aps { Sound = "default", Alert = new ApsAlert { Title = "ZULLO ⭐", LocKey = "zullo_superlike_body" } }
            },
            Data = new Dictionary<string, string> { ["type"] = "super_like", ["recipientUserId"] = recipient, ["eventId"] = collapse }
        };
    }

    private static AndroidConfig Android(string? title, string? body) => new() {
        Priority = Priority.High,
        Notification = new AndroidNotification {
            Title = title, Body = body, Sound = "default", ChannelId = "messages",
            DefaultSound = true, DefaultVibrateTimings = true, DefaultLightSettings = true
        }
    };
}
