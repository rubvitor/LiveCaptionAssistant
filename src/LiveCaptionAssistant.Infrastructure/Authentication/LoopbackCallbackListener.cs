using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace LiveCaptionAssistant.Infrastructure.Authentication;

/// <summary>
/// One-shot HTTP listener on 127.0.0.1 that receives the OAuth redirect (RFC 8252 loopback flow).
/// A raw socket is used so no URL ACL or administrator rights are needed.
/// </summary>
internal sealed class LoopbackCallbackListener : IDisposable
{
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);
    private const int MaxRequestBytes = 32 * 1024;

    private readonly TcpListener _listener;

    private LoopbackCallbackListener(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => $"http://127.0.0.1:{Port}{ChatGptOAuthConstants.CallbackPath}";

    /// <summary>Listens on <paramref name="preferredPort"/>, or on any free port when it is taken.</summary>
    public static LoopbackCallbackListener Start(int preferredPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, preferredPort);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            listener.Dispose();
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }

        return new LoopbackCallbackListener(listener);
    }

    /// <summary>Waits for the browser to hit the callback path and returns its query parameters.</summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();

            string? target;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(RequestReadTimeout);
                try
                {
                    target = await ReadRequestTargetAsync(stream, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    continue;
                }
            }

            if (target is null)
            {
                await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", "Bad request.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var queryStart = target.IndexOf('?');
            var path = queryStart < 0 ? target : target[..queryStart];
            if (!string.Equals(path, ChatGptOAuthConstants.CallbackPath, StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 404, "Not Found", "text/plain", "Not found.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var parameters = ParseQuery(queryStart < 0 ? string.Empty : target[(queryStart + 1)..]);
            var page = BuildResultPage(succeeded: !parameters.ContainsKey("error"));
            await WriteResponseAsync(stream, 200, "OK", "text/html", page, cancellationToken).ConfigureAwait(false);
            return parameters;
        }
    }

    public void Dispose() => _listener.Dispose();

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var values = HttpUtility.ParseQueryString(query);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in values.AllKeys)
        {
            if (key is not null && values[key] is { } value)
            {
                parameters[key] = value;
            }
        }

        return parameters;
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var received = new StringBuilder();
        while (received.Length < MaxRequestBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var text = received.ToString();
        var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLine = lineEnd >= 0 ? text[..lineEnd] : text;
        var parts = requestLine.Split(' ');
        return parts.Length >= 3 && parts[0] == "GET" ? parts[1] : null;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, int status, string reason, string contentType, string content, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(content);
        var header = $"HTTP/1.1 {status} {reason}\r\n"
            + $"Content-Type: {contentType}; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildResultPage(bool succeeded)
    {
        var title = succeeded ? "You're signed in" : "Sign-in was not completed";
        var message = succeeded
            ? "Live Caption Assistant received your ChatGPT sign-in. You can close this tab."
            : "Return to Live Caption Assistant to see what happened and try again.";
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>Live Caption Assistant</title>
              <style>
                body { font-family: "Segoe UI", sans-serif; background: #111418; color: #e8eaed; display: grid; place-items: center; height: 100vh; margin: 0; }
                main { text-align: center; max-width: 28rem; }
                h1 { font-weight: 600; font-size: 1.5rem; }
                p { color: #aeb4bd; }
              </style>
            </head>
            <body><main><h1>{{title}}</h1><p>{{message}}</p></main></body>
            </html>
            """;
    }
}
