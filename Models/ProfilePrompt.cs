namespace Zullo.Api.Models;

/// Stable question identifiers are localized by the client; answers are user text.
public sealed record ProfilePrompt(string Key, string Answer);
