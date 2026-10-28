using LiveCaptionAssistant.Core.Transcription;

namespace LiveCaptionAssistant.Tests.Core;

public sealed class SpeechSegmenterTests
{
    [Fact]
    public void Speech_between_silences_produces_one_padded_segment()
    {
        var segmenter = new SpeechSegmenter();
        var audio = TestAudio.Concat(TestAudio.Silence(1), TestAudio.Tone(1.5), TestAudio.Silence(1.5));

        var events = FeedInChunks(segmenter, audio);

        var started = Assert.Single(events.OfType<SpeechSegmentStarted>());
        var completed = Assert.Single(events.OfType<SpeechSegmentCompleted>());
        Assert.Equal(started.SegmentId, completed.SegmentId);
        Assert.InRange(TestAudio.Seconds(completed.Samples.Length), 1.5, 2.2);
        Assert.Contains(events, segmentEvent => segmentEvent is SpeechSegmentUpdated);
    }

    [Fact]
    public void Short_noise_burst_is_discarded()
    {
        var segmenter = new SpeechSegmenter();
        var audio = TestAudio.Concat(TestAudio.Silence(0.5), TestAudio.Tone(0.2), TestAudio.Silence(1));

        var events = FeedInChunks(segmenter, audio);

        Assert.Single(events.OfType<SpeechSegmentDiscarded>());
        Assert.Empty(events.OfType<SpeechSegmentCompleted>());
    }

    [Fact]
    public void Clicks_shorter_than_the_start_threshold_never_start_a_segment()
    {
        var segmenter = new SpeechSegmenter();
        var audio = TestAudio.Concat(TestAudio.Silence(0.5), TestAudio.Tone(0.06), TestAudio.Silence(1));

        Assert.Empty(FeedInChunks(segmenter, audio));
    }

    [Fact]
    public void Long_speech_is_split_without_losing_audio()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions { MaxSegmentMilliseconds = 15_000, PartialIntervalMilliseconds = 0 });
        var audio = TestAudio.Concat(TestAudio.Silence(0.5), TestAudio.SpeechLike(20), TestAudio.Silence(1.5));

        var events = FeedInChunks(segmenter, audio);

        var completed = events.OfType<SpeechSegmentCompleted>().ToList();
        Assert.Equal(2, completed.Count);
        Assert.Equal(2, events.OfType<SpeechSegmentStarted>().Count());
        Assert.InRange(TestAudio.Seconds(completed.Sum(segment => segment.Samples.Length)), 20, 21);
        Assert.Empty(events.OfType<SpeechSegmentUpdated>());
    }

    [Fact]
    public void Flush_completes_the_segment_in_progress()
    {
        var segmenter = new SpeechSegmenter();
        FeedInChunks(segmenter, TestAudio.Concat(TestAudio.Silence(0.5), TestAudio.Tone(1)));
        Assert.True(segmenter.IsInSpeech);

        var flushed = segmenter.Flush();

        Assert.Single(flushed.OfType<SpeechSegmentCompleted>());
        Assert.False(segmenter.IsInSpeech);
    }

    [Fact]
    public void Steady_tone_such_as_hold_music_is_treated_as_background()
    {
        var segmenter = new SpeechSegmenter();
        var audio = TestAudio.Concat(TestAudio.Silence(0.5), TestAudio.Tone(20));

        var events = FeedInChunks(segmenter, audio);

        var completed = Assert.Single(events.OfType<SpeechSegmentCompleted>());
        Assert.True(TestAudio.Seconds(completed.Samples.Length) < 8);
        Assert.False(segmenter.IsInSpeech);
    }

    [Fact]
    public void Steady_background_noise_stops_triggering_segments_once_the_noise_floor_adapts()
    {
        var segmenter = new SpeechSegmenter();
        var events = new List<(double Time, SpeechSegmentEvent Event)>();
        var noise = TestAudio.Noise(8, rms: 0.01f);
        const int chunkSize = 1_600;

        for (var offset = 0; offset < noise.Length; offset += chunkSize)
        {
            var chunk = noise.AsSpan(offset, Math.Min(chunkSize, noise.Length - offset));
            foreach (var segmentEvent in segmenter.Process(chunk))
            {
                events.Add((TestAudio.Seconds(offset), segmentEvent));
            }
        }

        Assert.DoesNotContain(events, entry => entry.Time > 4 && entry.Event is SpeechSegmentStarted);
        Assert.False(segmenter.IsInSpeech);
    }

    private static List<SpeechSegmentEvent> FeedInChunks(SpeechSegmenter segmenter, float[] audio, int chunkSize = 1_600)
    {
        var events = new List<SpeechSegmentEvent>();
        for (var offset = 0; offset < audio.Length; offset += chunkSize)
        {
            events.AddRange(segmenter.Process(audio.AsSpan(offset, Math.Min(chunkSize, audio.Length - offset))));
        }

        return events;
    }
}
