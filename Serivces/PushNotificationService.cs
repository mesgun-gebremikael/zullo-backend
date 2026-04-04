using FirebaseAdmin.Messaging;

namespace Zullo.Api.Services
{
    public class PushNotificationService
    {
        public async Task SendMessageNotificationAsync(
            string deviceToken,
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
                Data = new Dictionary<string, string>
                {
                    { "type", "message" },
                    { "senderName", senderName },
                    { "messageText", messageText }
                }
            };

            await FirebaseMessaging.DefaultInstance.SendAsync(message);
        }
    }
}


