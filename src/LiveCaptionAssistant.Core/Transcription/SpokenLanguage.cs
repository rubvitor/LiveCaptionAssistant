namespace LiveCaptionAssistant.Core.Transcription;

public enum SpokenLanguage
{
    English,
    Portuguese,
}

public enum LanguageMode
{
    AutoDetect,
    English,
    Portuguese,
}

public static class SpokenLanguageExtensions
{
    public static string ToIsoCode(this SpokenLanguage language) => language switch
    {
        SpokenLanguage.English => "en",
        SpokenLanguage.Portuguese => "pt",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    public static string ToShortLabel(this SpokenLanguage language) => language switch
    {
        SpokenLanguage.English => "EN",
        SpokenLanguage.Portuguese => "PT",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    public static string ToDisplayName(this SpokenLanguage language) => language switch
    {
        SpokenLanguage.English => "English",
        SpokenLanguage.Portuguese => "Portuguese",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>Language name used when instructing the chat model.</summary>
    public static string ToPromptName(this SpokenLanguage language) => language switch
    {
        SpokenLanguage.English => "English",
        SpokenLanguage.Portuguese => "Brazilian Portuguese",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    public static SpokenLanguage? FromIsoCode(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "en" => SpokenLanguage.English,
        "pt" => SpokenLanguage.Portuguese,
        _ => null,
    };

    public static SpokenLanguage? ToFixedLanguage(this LanguageMode mode) => mode switch
    {
        LanguageMode.English => SpokenLanguage.English,
        LanguageMode.Portuguese => SpokenLanguage.Portuguese,
        _ => null,
    };
}
