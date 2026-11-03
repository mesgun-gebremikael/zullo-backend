namespace Zullo.Api.Services;
public static class ConversationPrompts
{
    public static readonly string[] Keys = ["small_joy", "unexpected_skill", "weekend", "ask_me", "first_date"];
    public static bool IsValid(string key, string answer) =>
        (key.Length == 0 && answer.Length == 0) || (Keys.Contains(key) && !string.IsNullOrWhiteSpace(answer));
}
