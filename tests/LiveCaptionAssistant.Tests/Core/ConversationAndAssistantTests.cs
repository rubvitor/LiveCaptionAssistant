using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Settings;
using LiveCaptionAssistant.Core.Transcription;
using Microsoft.Extensions.Time.Testing;

namespace LiveCaptionAssistant.Tests.Core;

public sealed class ConversationTranscriptTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Keeps_segments_in_chronological_order_and_preserves_start_time_on_update()
    {
        var transcript = new ConversationTranscript();
        var changes = new List<TranscriptChange>();
        transcript.Changed += (_, change) => changes.Add(change);
        var later = Segment(Speaker.Them, "second", Start.AddSeconds(10));
        var earlier = Segment(Speaker.Me, "first", Start);

        transcript.Upsert(later);
        transcript.Upsert(earlier);
        transcript.Upsert(later with { Text = "second, corrected", StartedAt = Start.AddMinutes(5) });

        var snapshot = transcript.GetSnapshot();
        Assert.Equal(["first", "second, corrected"], snapshot.Select(segment => segment.Text));
        Assert.Equal(Start.AddSeconds(10), snapshot[1].StartedAt);
        Assert.Equal([TranscriptChangeKind.Added, TranscriptChangeKind.Added, TranscriptChangeKind.Updated], changes.Select(change => change.Kind));
    }

    [Fact]
    public void Recent_context_contains_only_final_segments_within_limits()
    {
        var transcript = new ConversationTranscript();
        for (var i = 0; i < 10; i++)
        {
            transcript.Upsert(Segment(Speaker.Them, $"segment {i}", Start.AddSeconds(i)));
        }

        transcript.Upsert(Segment(Speaker.Them, "partial", Start.AddSeconds(20), isFinal: false));

        var recent = transcript.GetRecentFinal(maxSegments: 3, maxCharacters: 1_000);

        Assert.Equal(["segment 7", "segment 8", "segment 9"], recent.Select(segment => segment.Text));
    }

    [Fact]
    public void Latest_final_segments_are_filtered_by_speaker_and_time()
    {
        var transcript = new ConversationTranscript();
        transcript.Upsert(Segment(Speaker.Them, "old", Start));
        transcript.Upsert(Segment(Speaker.Them, "question", Start.AddMinutes(3)));
        transcript.Upsert(Segment(Speaker.Me, "my answer", Start.AddMinutes(4)));

        var latest = transcript.GetLatestFinal(Speaker.Them, since: Start.AddMinutes(1), maxSegments: 2);

        Assert.Equal("question", Assert.Single(latest).Text);
    }

    [Fact]
    public void Remove_non_final_discards_partial_segments()
    {
        var transcript = new ConversationTranscript();
        transcript.Upsert(Segment(Speaker.Them, "final", Start));
        transcript.Upsert(Segment(Speaker.Them, "partial", Start.AddSeconds(1), isFinal: false));

        transcript.RemoveNonFinal();

        Assert.Equal("final", Assert.Single(transcript.GetSnapshot()).Text);
    }

    internal static TranscriptSegment Segment(Speaker speaker, string text, DateTimeOffset startedAt, bool isFinal = true, bool isQuestion = false, SpokenLanguage? language = SpokenLanguage.English) =>
        new(Guid.NewGuid(), speaker, startedAt, text, language, isFinal, isQuestion);
}

public sealed class AnswerPromptBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Answers_in_the_language_of_the_question_by_default()
    {
        var prompt = AnswerPromptBuilder.Build(
            "Como você lida com prazos curtos?", SpokenLanguage.Portuguese, [],
            new AnswerPreferences(AnswerLanguagePreference.SameAsQuestion, AnswerStyle.Concise, UserBackground: null));

        Assert.Contains("Reply in Brazilian Portuguese.", prompt.Instructions, StringComparison.Ordinal);
        Assert.Contains("(not provided)", prompt.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_answer_language_and_background_are_respected()
    {
        var prompt = AnswerPromptBuilder.Build(
            "Como você lida com prazos curtos?", SpokenLanguage.Portuguese, [],
            new AnswerPreferences(AnswerLanguagePreference.English, AnswerStyle.Detailed, "Senior .NET engineer, 8 years."));

        Assert.Contains("Reply in English.", prompt.Instructions, StringComparison.Ordinal);
        Assert.Contains("Senior .NET engineer, 8 years.", prompt.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Input_lists_the_transcript_before_the_question()
    {
        IReadOnlyList<TranscriptSegment> context =
        [
            ConversationTranscriptTests.Segment(Speaker.Them, "Thanks for joining today.", Start),
            ConversationTranscriptTests.Segment(Speaker.Me, "Happy to be here.", Start.AddSeconds(5)),
        ];

        var prompt = AnswerPromptBuilder.Build(
            "What is your biggest strength?", SpokenLanguage.English, context,
            new AnswerPreferences(AnswerLanguagePreference.SameAsQuestion, AnswerStyle.Concise, null));

        var themIndex = prompt.Input.IndexOf("Them: Thanks for joining today.", StringComparison.Ordinal);
        var meIndex = prompt.Input.IndexOf("Me: Happy to be here.", StringComparison.Ordinal);
        var questionIndex = prompt.Input.IndexOf("Question to answer:\nWhat is your biggest strength?", StringComparison.Ordinal);
        Assert.True(themIndex >= 0 && meIndex > themIndex && questionIndex > meIndex, prompt.Input);
    }
}

public sealed class KnownModelCatalogTests
{
    [Fact]
    public void Dated_snapshots_resolve_to_their_model_family()
    {
        var model = KnownModelCatalog.Find("gpt-5.5-2026-04-23");

        Assert.NotNull(model);
        Assert.Equal("gpt-5.5", model.Id);
    }

    [Fact]
    public void Sol_variants_are_not_confused()
    {
        var sixOneSol = KnownModelCatalog.Find("gpt-6.1-sol");
        var sixSol = KnownModelCatalog.Find("gpt-6-sol");

        Assert.NotNull(sixOneSol);
        Assert.NotNull(sixSol);
        Assert.False(sixOneSol.Supports(ReasoningEffort.None));
        Assert.True(sixSol.Supports(ReasoningEffort.None));
        Assert.True(sixOneSol.Supports(ReasoningEffort.Max));
    }

    [Fact]
    public void Unknown_models_have_no_assumed_reasoning_efforts()
    {
        var model = KnownModelCatalog.Describe("gpt-7-nova", "GPT-7 Nova");

        Assert.Equal("GPT-7 Nova", model.DisplayName);
        Assert.Empty(model.SupportedEfforts);
        Assert.Null(model.DefaultEffort);
    }

    [Theory]
    [InlineData(null, "medium")]
    [InlineData(AppSettings.ModelDefaultEffort, null)]
    [InlineData("high", "high")]
    public void Settings_map_the_stored_effort_to_the_request_value(string? stored, string? expected)
    {
        var settings = new AppSettings { ChatReasoningEffort = stored };

        Assert.Equal(expected, settings.GetEffectiveReasoningEffort()?.Value);
    }
}

public sealed class QuestionDebouncerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Releases_a_question_after_the_quiet_period()
    {
        var time = new FakeTimeProvider(Start);
        using var debouncer = new QuestionDebouncer(time, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        PendingQuestion? released = null;
        debouncer.QuestionReady += (_, question) => released = question;

        debouncer.OnSegmentFinalized(ConversationTranscriptTests.Segment(Speaker.Them, "What is your experience with Azure?", Start, isQuestion: true));
        time.Advance(TimeSpan.FromMilliseconds(900));
        Assert.Null(released);

        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal("What is your experience with Azure?", released?.Text);
    }

    [Fact]
    public void Follow_up_sentences_are_merged_into_the_question()
    {
        var time = new FakeTimeProvider(Start);
        using var debouncer = new QuestionDebouncer(time, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        PendingQuestion? released = null;
        debouncer.QuestionReady += (_, question) => released = question;

        debouncer.OnSegmentFinalized(ConversationTranscriptTests.Segment(Speaker.Them, "How do you scale a service?", Start, isQuestion: true));
        time.Advance(TimeSpan.FromMilliseconds(500));
        debouncer.OnSegmentFinalized(ConversationTranscriptTests.Segment(Speaker.Them, "Especially under unpredictable load.", Start.AddSeconds(1)));
        time.Advance(TimeSpan.FromMilliseconds(1_100));

        Assert.NotNull(released);
        Assert.Equal("How do you scale a service? Especially under unpredictable load.", released.Text);
        Assert.Equal(2, released.SegmentIds.Count);
    }

    [Fact]
    public void Waits_while_the_other_participants_keep_talking_up_to_the_maximum()
    {
        var time = new FakeTimeProvider(Start);
        using var debouncer = new QuestionDebouncer(time, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(6));
        PendingQuestion? released = null;
        debouncer.QuestionReady += (_, question) => released = question;

        debouncer.OnSegmentFinalized(ConversationTranscriptTests.Segment(Speaker.Them, "Why do you want this job?", Start, isQuestion: true));
        debouncer.OnSpeechActivity(Speaker.Them, isActive: true);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(released);

        time.Advance(TimeSpan.FromSeconds(1.1));
        Assert.NotNull(released);
    }

    [Fact]
    public void Statements_and_my_own_speech_are_not_released()
    {
        var time = new FakeTimeProvider(Start);
        using var debouncer = new QuestionDebouncer(time);
        var releasedCount = 0;
        debouncer.QuestionReady += (_, _) => releasedCount++;

        debouncer.OnSegmentFinalized(ConversationTranscriptTests.Segment(Speaker.Them, "We use microservices here.", Start));
        debouncer.OnSegmentFinalized(ConversationTranscriptTests.Segment(Speaker.Me, "What should I prepare?", Start, isQuestion: true));
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(0, releasedCount);
    }
}
