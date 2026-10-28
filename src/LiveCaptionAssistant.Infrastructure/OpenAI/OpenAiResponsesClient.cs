using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Authentication;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.Infrastructure.OpenAI;

internal static class HttpClientNames
{
    public const string OpenAIApi = "OpenAI.Api";
    public const string OpenAIAuth = "OpenAI.Auth";

    public static string? GetRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values) || response.Headers.TryGetValues("openai-request-id", out values)
            ? values.FirstOrDefault()
            : null;
}

/// <summary>Streams answers from <c>POST /v1/responses</c> using the ChatGPT OAuth access token.</summary>
internal sealed class OpenAiResponsesClient : IChatResponseClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccessTokenProvider _tokens;
    private readonly ILogger<OpenAiResponsesClient> _logger;

    public OpenAiResponsesClient(IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens, ILogger<OpenAiResponsesClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _logger = logger;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> StreamResponseAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var requestId = HttpClientNames.GetRequestId(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var completed = false;
        await foreach (var serverEvent in ServerSentEventReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (serverEvent.Data == "[DONE]")
            {
                break;
            }

            var update = ResponsesProtocol.ParseEvent(serverEvent.Data, requestId);
            if (!string.IsNullOrEmpty(update.TextDelta))
            {
                yield return new ChatResponseUpdate(update.TextDelta);
            }

            if (update.IsCompleted)
            {
                completed = true;
                if (update.ServiceTier is not null)
                {
                    yield return new ChatResponseUpdate(null, update.ServiceTier);
                }

                break;
            }
        }

        if (!completed)
        {
            throw new ChatServiceException("The answer stream ended before it was completed.", "stream_incomplete", statusCode: null, requestId);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var body = ResponsesProtocol.CreateRequestBody(request);

        // The client is not disposed: disposing it would abort the response stream still being read.
        var client = _httpClientFactory.CreateClient(HttpClientNames.OpenAIApi);
        for (var attempt = 0; ; attempt++)
        {
            var accessToken = await _tokens.GetAccessTokenAsync(forceRefresh: attempt > 0, cancellationToken).ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Post, "responses")
            {
                Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _logger.LogInformation("The Responses API returned 401; retrying with a refreshed token.");
                    continue;
                }

                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw OpenAiErrorParser.FromHttpResponse((int)response.StatusCode, errorBody, HttpClientNames.GetRequestId(response));
            }
        }
    }
}
