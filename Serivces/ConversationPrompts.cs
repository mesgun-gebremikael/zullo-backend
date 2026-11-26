using Zullo.Api.Models;

namespace Zullo.Api.Services;
public static class ConversationPrompts
{
    public static readonly string[] Keys = ["small_joy", "unexpected_skill", "weekend", "ask_me", "first_date", "perfect_evening", "travel_story", "food_opinion", "teach_me", "simple_plan", "surprise", "talk_all_night"];
    public const int Maximum = 3;
    public static bool IsValidCollection(IReadOnlyList<ProfilePrompt> prompts, bool allowUnanswered) =>
        prompts.Count <= Maximum && prompts.All(p => p != null && p.Key != null && p.Answer != null &&
            Keys.Contains(p.Key.Trim()) && p.Key.Length <= 60 && p.Answer.Length <= 250 &&
            !p.Key.Contains('\0') && !p.Answer.Contains('\0') &&
            (allowUnanswered || !string.IsNullOrWhiteSpace(p.Answer))) &&
        prompts.Select(p => p.Key.Trim()).Distinct(StringComparer.Ordinal).Count() == prompts.Count;

    public static List<ProfilePrompt> Read(Profile profile) => profile.ConversationPrompts.Count > 0
        ? profile.ConversationPrompts.Select(p => new ProfilePrompt(p.Key, p.Answer)).ToList()
        : IsValid(profile.PromptKey, profile.PromptAnswer) && profile.PromptKey.Length > 0
            ? [new ProfilePrompt(profile.PromptKey, profile.PromptAnswer)] : [];

    public static List<ProfilePrompt> MergeLegacy(Profile profile, string key, string answer)
    {
        var existing = Read(profile);
        if (existing.Count > 0) existing.RemoveAt(0);
        if (key.Length > 0) {
            // Old clients cannot silently replace an additional question's answer.
            if (existing.Any(p => p.Key == key)) throw new ArgumentException("This question is already present in your profile.");
            existing.Insert(0, new ProfilePrompt(key, answer));
        }
        return existing;
    }
    public static void Assign(Profile profile, List<ProfilePrompt> prompts)
    {
        profile.ConversationPrompts = prompts.Select(p => new ProfilePrompt(p.Key.Trim(), p.Answer.Trim())).ToList();
        profile.PromptKey = profile.ConversationPrompts.FirstOrDefault()?.Key ?? "";
        profile.PromptAnswer = profile.ConversationPrompts.FirstOrDefault()?.Answer ?? "";
    }
    public static bool IsValid(string key, string answer) =>
        (key.Length == 0 && answer.Length == 0) || (Keys.Contains(key) && !string.IsNullOrWhiteSpace(answer));
}
