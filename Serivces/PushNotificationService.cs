
using FirebaseAdmin.Messaging;

namespace Zullo.Api.Services
{
    public class PushNotificationService
    {
        public async Task SendMessageNotificationAsync(
            string deviceToken,
            string senderUserId,
            string senderName,
            string messageText)
        {
            if (string.IsNullOrWhiteSpace(deviceToken))
                return;

            var message = new Message
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
                    { "senderUserId", senderUserId },
                    { "senderName", senderName },
                    { "messageText", messageText }
                }
            };

            await FirebaseMessaging.DefaultInstance.SendAsync(message);
        }
    }
}
