using LiveCaptionAssistant.Core.Transcription;

namespace LiveCaptionAssistant.Core.Conversation;

public enum Speaker
{
    /// <summary>Other participants, captured from the computer's audio output.</summary>
    Them,

    /// <summary>The user, captured from the microphone.</summary>
    Me,
}

public sealed record TranscriptSegment(
    Guid Id,
    Speaker Speaker,
    DateTimeOffset StartedAt,
    string Text,
    SpokenLanguage? Language,
    bool IsFinal,
    bool IsQuestion);

public enum TranscriptChangeKind
{
    Added,
    Updated,
    Removed,
    Cleared,
}

public sealed record TranscriptChange(TranscriptChangeKind Kind, TranscriptSegment? Segment);
