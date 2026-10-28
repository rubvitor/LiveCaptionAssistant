using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Authentication;

namespace LiveCaptionAssistant.Infrastructure.OpenAI;

/// <summary>Lists the models available to the signed-in ChatGPT account (<c>GET /v1/models</c>).</summary>
internal sealed class OpenAiModelCatalogClient : IModelCatalogClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccessTokenProvider _tokens;

    public OpenAiModelCatalogClient(IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
    }

    public async Task<IReadOnlyList<ChatModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.OpenAIApi);
        for (var attempt = 0; ; attempt++)
        {
            var accessToken = await _tokens.GetAccessTokenAsync(forceRefresh: attempt > 0, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, "models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ModelCatalogParser.Parse(body);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                continue;
            }

            throw OpenAiErrorParser.FromHttpResponse((int)response.StatusCode, body, HttpClientNames.GetRequestId(response));
        }
    }
}

internal static class ModelCatalogParser
{
    private static readonly string[] NonChatModelFragments =
        ["audio", "realtime", "transcribe", "tts", "image", "search", "embedding", "moderation", "live", "whisper", "translate", "diarize"];

    /// <summary>
    /// Parses the account model catalog (<c>models[]</c> with <c>slug</c>/<c>display_name</c>/<c>visibility</c>)
    /// or the standard list format (<c>data[]</c> with <c>id</c>), preserving the server's order.
    /// </summary>
    public static IReadOnlyList<ChatModelInfo> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var models = new List<ChatModelInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("models", out var catalog) && catalog.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in catalog.EnumerateArray())
            {
                var id = GetString(item, "slug") ?? GetString(item, "id");
                var visibility = GetString(item, "visibility");
                if (id is null
                    || (visibility is not null && !visibility.Equals("list", StringComparison.OrdinalIgnoreCase))
                    || !seen.Add(id))
                {
                    continue;
                }

                var described = KnownModelCatalog.Describe(id, GetString(item, "display_name"));
                var efforts = ReadEfforts(item);
                var defaultEffort = GetString(item, "default_reasoning_level") ?? GetString(item, "default_reasoning_effort");
                models.Add(described with
                {
                    SupportedEfforts = efforts.Count > 0 ? efforts : described.SupportedEfforts,
                    DefaultEffort = string.IsNullOrWhiteSpace(defaultEffort) ? described.DefaultEffort : new ReasoningEffort(defaultEffort),
                });
            }
        }
        else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = GetString(item, "id");
                if (id is not null && IsChatModel(id) && seen.Add(id))
                {
                    models.Add(KnownModelCatalog.Describe(id, displayName: null));
                }
            }
        }

        return models;
    }

    private static List<ReasoningEffort> ReadEfforts(JsonElement item)
    {
        foreach (var propertyName in (string[])["supported_reasoning_levels", "supported_reasoning_efforts"])
        {
            if (!item.TryGetProperty(propertyName, out var levels) || levels.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var efforts = new List<ReasoningEffort>();
            foreach (var level in levels.EnumerateArray())
            {
                var value = level.ValueKind switch
                {
                    JsonValueKind.String => level.GetString(),
                    JsonValueKind.Object => GetString(level, "effort") ?? GetString(level, "id"),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(value))
                {
                    efforts.Add(new ReasoningEffort(value));
                }
            }

            if (efforts.Count > 0)
            {
                return efforts;
            }
        }

        return [];
    }

    private static bool IsChatModel(string id) =>
        id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
        && !NonChatModelFragments.Any(fragment => id.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
