
using FirebaseAdmin.Messaging;

namespace Zullo.Api.Services
{
    public class PushNotificationService
    {
        public async Task SendMessageNotificationAsync(
    string deviceToken,
    string senderUserId,
    string senderName,
    string messageText,
    string? senderPhotoUrl,
    string recipientUserId)
        {
            if (string.IsNullOrWhiteSpace(deviceToken))
                return;

            var message = CreateMessage(deviceToken, senderUserId, senderName,
                messageText, senderPhotoUrl, recipientUserId);
            await FirebaseMessaging.DefaultInstance.SendAsync(message);
        }

        internal static Message CreateMessage(string deviceToken, string senderUserId,
            string senderName, string messageText, string? senderPhotoUrl, string recipientUserId)
        {
            return new Message
            {
                Token = deviceToken,

                Notification = new Notification
                {
                    Title = senderName,
                    Body = messageText
                },

                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification
                    {
                        Title = senderName,
                        Body = messageText,
                        Sound = "default",
                        ChannelId = "messages",
                        DefaultSound = true,
                        DefaultVibrateTimings = true,
                        DefaultLightSettings = true
                    }
                },

                Data = new Dictionary<string, string>
{
                    { "type", "message" },
                    { "recipientUserId", recipientUserId },
                     { "senderUserId", senderUserId },
                     { "senderName", senderName },
                      { "messageText", messageText },
                      { "senderPhotoUrl", senderPhotoUrl ?? "" }
}
            };

        }
    }
}
