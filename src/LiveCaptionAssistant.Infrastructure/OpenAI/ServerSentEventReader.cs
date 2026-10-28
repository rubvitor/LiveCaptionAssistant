using System.Runtime.CompilerServices;
using System.Text;

namespace LiveCaptionAssistant.Infrastructure.OpenAI;

internal sealed record ServerSentEvent(string? EventName, string Data);

/// <summary>Minimal text/event-stream parser (only the fields the OpenAI API uses).</summary>
internal static class ServerSentEventReader
{
    public static async IAsyncEnumerable<ServerSentEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
        string? eventName = null;
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new ServerSentEvent(eventName, data.ToString());
                    data.Clear();
                }

                eventName = null;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    break;
            }
        }

        if (data.Length > 0)
        {
            yield return new ServerSentEvent(eventName, data.ToString());
        }
    }
}
