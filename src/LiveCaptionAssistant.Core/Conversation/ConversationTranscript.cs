namespace LiveCaptionAssistant.Core.Conversation;

/// <summary>
/// Thread-safe, chronologically ordered store of transcript segments.
/// <see cref="Changed"/> is raised while the store is locked so subscribers observe changes in order;
/// handlers must return quickly and must not block on other threads.
/// </summary>
public sealed class ConversationTranscript
{
    private readonly Lock _gate = new();
    private readonly List<TranscriptSegment> _segments = [];

    public event EventHandler<TranscriptChange>? Changed;

    public void Upsert(TranscriptSegment segment)
    {
        lock (_gate)
        {
            var index = _segments.FindIndex(existing => existing.Id == segment.Id);
            if (index >= 0)
            {
                var updated = segment with { StartedAt = _segments[index].StartedAt };
                _segments[index] = updated;
                Changed?.Invoke(this, new TranscriptChange(TranscriptChangeKind.Updated, updated));
                return;
            }

            _segments.Insert(FindInsertIndex(segment.StartedAt), segment);
            Changed?.Invoke(this, new TranscriptChange(TranscriptChangeKind.Added, segment));
        }
    }

    public bool Remove(Guid segmentId)
    {
        lock (_gate)
        {
            var index = _segments.FindIndex(existing => existing.Id == segmentId);
            if (index < 0)
            {
                return false;
            }

            var removed = _segments[index];
            _segments.RemoveAt(index);
            Changed?.Invoke(this, new TranscriptChange(TranscriptChangeKind.Removed, removed));
            return true;
        }
    }

    public void RemoveNonFinal()
    {
        lock (_gate)
        {
            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                if (!_segments[i].IsFinal)
                {
                    var removed = _segments[i];
                    _segments.RemoveAt(i);
                    Changed?.Invoke(this, new TranscriptChange(TranscriptChangeKind.Removed, removed));
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _segments.Clear();
            Changed?.Invoke(this, new TranscriptChange(TranscriptChangeKind.Cleared, null));
        }
    }

    public IReadOnlyList<TranscriptSegment> GetSnapshot()
    {
        lock (_gate)
        {
            return [.. _segments];
        }
    }

    /// <summary>Most recent final segments (oldest first) within the given limits.</summary>
    public IReadOnlyList<TranscriptSegment> GetRecentFinal(int maxSegments, int maxCharacters)
    {
        lock (_gate)
        {
            var selected = new List<TranscriptSegment>();
            var characters = 0;
            for (var i = _segments.Count - 1; i >= 0 && selected.Count < maxSegments; i--)
            {
                var segment = _segments[i];
                if (!segment.IsFinal)
                {
                    continue;
                }

                characters += segment.Text.Length;
                if (characters > maxCharacters && selected.Count > 0)
                {
                    break;
                }

                selected.Add(segment);
            }

            selected.Reverse();
            return selected;
        }
    }

    /// <summary>Latest final segments from <paramref name="speaker"/> (oldest first) that started after <paramref name="since"/>.</summary>
    public IReadOnlyList<TranscriptSegment> GetLatestFinal(Speaker speaker, DateTimeOffset since, int maxSegments)
    {
        lock (_gate)
        {
            var selected = new List<TranscriptSegment>();
            for (var i = _segments.Count - 1; i >= 0 && selected.Count < maxSegments; i--)
            {
                var segment = _segments[i];
                if (segment.StartedAt < since)
                {
                    break;
                }

                if (segment.IsFinal && segment.Speaker == speaker)
                {
                    selected.Add(segment);
                }
            }

            selected.Reverse();
            return selected;
        }
    }

    private int FindInsertIndex(DateTimeOffset startedAt)
    {
        var index = _segments.Count;
        while (index > 0 && _segments[index - 1].StartedAt > startedAt)
        {
            index--;
        }

        return index;
    }
}
