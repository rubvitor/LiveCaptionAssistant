using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionAssistant.Core.Transcription;

/// <summary>
/// Removes the artifacts Whisper produces on silence, music and noise (bracketed tags,
/// subtitle credits, looping repetitions) from a transcript.
/// </summary>
public static partial class HallucinationFilter
{
    public const float UncertainNoSpeechProbability = 0.35f;

    private static readonly HashSet<string> AlwaysDiscarded = new(StringComparer.Ordinal)
    {
        "legendas pela comunidade amaraorg",
        "legendas pela comunidade amara org",
        "subtitles by the amaraorg community",
        "subtitles by the amara org community",
        "thanks for watching",
        "thank you for watching",
        "thank you so much for watching",
        "obrigado por assistir",
        "obrigada por assistir",
        "inscrevase no canal",
        "se inscreva no canal",
        "please subscribe",
        "like and subscribe",
    };

    private static readonly HashSet<string> DiscardedWhenUncertain = new(StringComparer.Ordinal)
    {
        "you", "thank you", "thanks", "bye", "okay", "ok", "hmm", "uh", "um",
        "obrigado", "obrigada", "tchau", "e ai", "ta",
    };

    public static string Clean(string? text, float noSpeechProbability)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = NonSpeechTagRegex().Replace(text, " ");
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        cleaned = CollapseRepeatedSentences(cleaned);

        if (!cleaned.Any(char.IsLetterOrDigit))
        {
            return string.Empty;
        }

        var key = NormalizeKey(cleaned);
        if (AlwaysDiscarded.Contains(key))
        {
            return string.Empty;
        }

        if (noSpeechProbability >= UncertainNoSpeechProbability && DiscardedWhenUncertain.Contains(key))
        {
            return string.Empty;
        }

        return cleaned;
    }

    internal static string CollapseRepeatedSentences(string text)
    {
        var sentences = SentenceRegex().Matches(text)
            .Select(match => match.Value.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();

        if (sentences.Count < 2)
        {
            return text;
        }

        var kept = new List<string>(sentences.Count);
        string? previousKey = null;
        foreach (var sentence in sentences)
        {
            var key = NormalizeKey(sentence);
            if (key.Length > 0 && key == previousKey)
            {
                continue;
            }

            kept.Add(sentence);
            previousKey = key;
        }

        return string.Join(' ', kept);
    }

    internal static string NormalizeKey(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(character);
                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|[♪♫]+")]
    private static partial Regex NonSpeechTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^.!?…]+[.!?…]*")]
    private static partial Regex SentenceRegex();
}
