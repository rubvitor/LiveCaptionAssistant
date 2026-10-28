using System.Globalization;
using System.Text;
using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Transcription;

namespace LiveCaptionAssistant.Core.Assistant;

public enum AnswerLanguagePreference
{
    SameAsQuestion,
    English,
    Portuguese,
}

public enum AnswerStyle
{
    Concise,
    Detailed,
}

public sealed record AnswerPreferences(AnswerLanguagePreference Language, AnswerStyle Style, string? UserBackground);

public sealed record AnswerPrompt(string Instructions, string Input);

public static class AnswerPromptBuilder
{
    public static AnswerPrompt Build(
        string question,
        SpokenLanguage? questionLanguage,
        IReadOnlyList<TranscriptSegment> context,
        AnswerPreferences preferences)
    {
        return new AnswerPrompt(BuildInstructions(questionLanguage, preferences), BuildInput(question, context));
    }

    internal static string ResolveAnswerLanguage(AnswerLanguagePreference preference, SpokenLanguage? questionLanguage) => preference switch
    {
        AnswerLanguagePreference.English => SpokenLanguage.English.ToPromptName(),
        AnswerLanguagePreference.Portuguese => SpokenLanguage.Portuguese.ToPromptName(),
        _ => questionLanguage?.ToPromptName() ?? "the same language as the question",
    };

    private static string BuildInstructions(SpokenLanguage? questionLanguage, AnswerPreferences preferences)
    {
        var language = ResolveAnswerLanguage(preferences.Language, questionLanguage);
        var shape = preferences.Style == AnswerStyle.Concise
            ? "Open with the direct answer in one or two sentences, then add at most three short bullet points with concrete details or an example."
            : "Open with the direct answer, then develop it with concrete details, an example, and the reasoning behind it in a few short paragraphs or bullet points.";
        var background = string.IsNullOrWhiteSpace(preferences.UserBackground)
            ? "(not provided)"
            : preferences.UserBackground.Trim();

        return $"""
            You are a real-time assistant for a live conversation, such as a job interview or a meeting.
            The user is a participant. In the transcript, "Them" lines come from the other participants and "Me" lines come from the user.
            Write the answer the user can say out loud right now to the latest question.

            Rules:
            - Reply in {language}.
            - {shape}
            - Speak in the first person, as the user, with a natural and confident tone.
            - Use the user's background when it is relevant. Never invent employers, dates, numbers or credentials that the background does not contain; when it does not cover the topic, give a solid general answer the user can adapt.
            - The transcript comes from automatic speech recognition and may contain mistakes; infer the intended meaning.
            - If the latest text is not really a question, suggest a short, useful reply or follow-up question.
            - Plain text only. Use "-" for bullet points. Do not use Markdown headings, bold text or tables. Include code only when the question asks for code.

            User background:
            {background}
            """.ReplaceLineEndings("\n");
    }

    private static string BuildInput(string question, IReadOnlyList<TranscriptSegment> context)
    {
        var builder = new StringBuilder();
        if (context.Count > 0)
        {
            builder.Append("Conversation transcript (oldest first):\n");
            foreach (var segment in context)
            {
                builder.Append('[')
                    .Append(segment.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append("] ")
                    .Append(segment.Speaker == Speaker.Me ? "Me" : "Them")
                    .Append(": ")
                    .Append(segment.Text)
                    .Append('\n');
            }

            builder.Append('\n');
        }

        builder.Append("Question to answer:\n").Append(question.Trim());
        return builder.ToString();
    }
}
