using System.Globalization;
using System.Text;

namespace LiveCaptionAssistant.Core.Conversation;

public interface IQuestionDetector
{
    bool IsQuestion(string text);
}

/// <summary>
/// Detects questions and interview prompts in English and Portuguese transcripts.
/// Opening phrases also cover requests and questions that speech recognition punctuates as statements.
/// </summary>
public sealed class HeuristicQuestionDetector : IQuestionDetector
{
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "so", "okay", "ok", "well", "alright", "right", "and", "now", "um", "uh", "great", "cool",
        "entao", "bom", "bem", "ta", "beleza", "certo", "agora", "e", "ai", "olha", "legal", "perfeito",
    };

    private static readonly string[] PromptOpeners =
    [
        "tell me", "walk me through", "describe", "explain", "give me an example", "talk about",
        "talk me through", "share an example", "share a time", "can you tell me", "could you tell me",
        "me fale", "me fala", "me conte", "me conta", "me diga", "me explique", "me explica",
        "me de um exemplo", "fale sobre", "fala sobre", "fale um pouco", "fala um pouco",
        "conte sobre", "conta sobre", "conte um pouco", "conta um pouco", "descreva", "explique",
        "de um exemplo",
    ];

    private static readonly string[] QuestionOpeners =
    [
        "what", "whats", "why", "how", "when", "where", "who", "whom", "whose", "which",
        "can you", "could you", "would you", "will you", "do you", "did you", "does", "have you",
        "has", "are you", "were you", "is it", "is there", "are there", "should", "may i ask",
        "o que", "por que", "como", "quando", "onde", "aonde", "quem", "qual", "quais",
        "quanto", "quanta", "quantos", "quantas", "voce pode", "voce poderia", "voce ja",
        "voce tem", "voce sabe", "voce conhece", "voce acha", "voce gosta", "voce prefere",
        "e possivel", "tem como",
    ];

    // A recognizer may put a period after a direct question. These phrases retain their
    // interrogative/request meaning without relying on the generated punctuation.
    private static readonly string[] DirectQuestionOpeners =
    [
        "can you", "could you", "would you", "will you", "do you", "did you", "have you",
        "are you", "were you", "is it", "is there", "are there", "may i ask",
        "how do", "how does", "how would", "how can", "why do", "why does",
        "voce pode explicar", "voce pode descrever", "voce pode comparar", "voce pode detalhar", "voce pode me",
        "voce poderia explicar", "voce poderia descrever", "voce poderia comparar", "voce poderia me",
        "qual e", "quais sao", "qual seria", "quais seriam", "como funciona",
    ];

    public bool IsQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var words = Normalize(trimmed)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(Fillers.Contains)
            .ToArray();

        if (words.Length < 3)
        {
            return false;
        }

        if (trimmed.Contains('?'))
        {
            return true;
        }

        var phrase = string.Join(' ', words);
        if (StartsWithAny(phrase, PromptOpeners) || StartsWithAny(phrase, DirectQuestionOpeners))
        {
            return true;
        }

        var endsAsStatement = trimmed.EndsWith('.') || trimmed.EndsWith('!');
        return words.Length >= 4 && !endsAsStatement && StartsWithAny(phrase, QuestionOpeners);
    }

    private static bool StartsWithAny(string phrase, string[] openers)
    {
        foreach (var opener in openers)
        {
            if (phrase.StartsWith(opener, StringComparison.Ordinal)
                && (phrase.Length == opener.Length || phrase[opener.Length] == ' '))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return builder.ToString();
    }
}
