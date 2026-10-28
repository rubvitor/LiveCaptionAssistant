using System.Text;
using System.Text.Json;
using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Infrastructure.OpenAI;

namespace LiveCaptionAssistant.Tests.Infrastructure;

public sealed class ServerSentEventReaderTests
{
    [Fact]
    public async Task Parses_events_comments_and_multi_line_data()
    {
        const string stream = ": keep-alive\n\nevent: response.output_text.delta\ndata: {\"a\":1}\n\ndata: line one\ndata: line two\n\ndata: [DONE]\n\n";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(stream));

        var events = new List<ServerSentEvent>();
        await foreach (var serverEvent in ServerSentEventReader.ReadAsync(input, TestContext.Current.CancellationToken))
        {
            events.Add(serverEvent);
        }

        Assert.Equal(3, events.Count);
        Assert.Equal(new ServerSentEvent("response.output_text.delta", "{\"a\":1}"), events[0]);
        Assert.Equal("line one\nline two", events[1].Data);
        Assert.Equal("[DONE]", events[2].Data);
    }
}

public sealed class ResponsesProtocolTests
{
    [Theory]
    [InlineData(ResponseSpeed.Standard, "default")]
    [InlineData(ResponseSpeed.Fast, "priority")]
    public void Response_speed_is_sent_independently_of_reasoning_effort(ResponseSpeed speed, string tier)
    {
        var body = ResponsesProtocol.CreateRequestBody(new ChatRequest("gpt-6.1-sol", ReasoningEffort.High, "Be concise.", "Hello", speed));

        using var document = JsonDocument.Parse(body);
        Assert.Equal(tier, document.RootElement.GetProperty("service_tier").GetString());
        Assert.Equal("high", document.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public void Request_body_is_valid_for_chatgpt_plan_usage()
    {
        var body = ResponsesProtocol.CreateRequestBody(new ChatRequest("gpt-6.1-sol", ReasoningEffort.Low, "Be concise.", "Hello"));

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("gpt-6.1-sol", root.GetProperty("model").GetString());
        Assert.Equal("Be concise.", root.GetProperty("instructions").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
        var message = Assert.Single(root.GetProperty("input").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("Hello", message.GetProperty("content").GetString());
        Assert.False(root.TryGetProperty("max_output_tokens", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void Reasoning_is_omitted_when_the_model_default_is_used()
    {
        var body = ResponsesProtocol.CreateRequestBody(new ChatRequest("gpt-6-luna", null, "x", "y"));

        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void Text_deltas_and_completion_are_recognized()
    {
        var delta = ResponsesProtocol.ParseEvent("""{"type":"response.output_text.delta","delta":"Hi"}""", requestId: null);
        var completed = ResponsesProtocol.ParseEvent("""{"type":"response.completed","response":{}}""", requestId: null);
        var other = ResponsesProtocol.ParseEvent("""{"type":"response.created"}""", requestId: null);

        Assert.Equal(new ResponseStreamUpdate("Hi", false), delta);
        Assert.True(completed.IsCompleted);
        Assert.Equal(default, other);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("priority")]
    [InlineData("fast")]
    public void Completion_keeps_the_actual_service_tier_including_a_standard_fallback(string tier)
    {
        var json = JsonSerializer.Serialize(new { type = "response.completed", response = new { service_tier = tier } });

        var update = ResponsesProtocol.ParseEvent(json, null);

        Assert.True(update.IsCompleted);
        Assert.Equal(tier, update.ServiceTier);
    }

    [Fact]
    public void Usage_limit_failures_raise_a_friendly_error()
    {
        const string json = """{"type":"response.failed","response":{"error":{"code":"subscription_sharing_usage_limit_exceeded","message":"limit"}}}""";

        var exception = Assert.Throws<ChatServiceException>(() => ResponsesProtocol.ParseEvent(json, "req_1"));

        Assert.True(exception.IsUsageLimit);
        Assert.Equal("req_1", exception.RequestId);
        Assert.Contains("usage limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Error_events_and_incomplete_responses_raise_errors()
    {
        var error = Assert.Throws<ChatServiceException>(() =>
            ResponsesProtocol.ParseEvent("""{"type":"error","code":"subscription_sharing_unsupported_capability","message":"no","param":"tools"}""", null));
        var incomplete = Assert.Throws<ChatServiceException>(() =>
            ResponsesProtocol.ParseEvent("""{"type":"response.incomplete","response":{"incomplete_details":{"reason":"content_filter"}}}""", null));

        Assert.Equal("subscription_sharing_unsupported_capability", error.ErrorCode);
        Assert.Contains("(tools)", error.Message, StringComparison.Ordinal);
        Assert.Contains("content_filter", incomplete.Message, StringComparison.Ordinal);
    }
}

public sealed class OpenAiErrorParserTests
{
    [Fact]
    public void Admission_errors_with_a_detail_body_keep_the_message()
    {
        var exception = OpenAiErrorParser.FromHttpResponse(403, """{"detail":"Region not supported"}""", "req_2");

        Assert.Equal("Region not supported", exception.Message);
        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public void Unauthorized_responses_ask_the_user_to_sign_in_again()
    {
        var exception = OpenAiErrorParser.FromHttpResponse(401, string.Empty, null);

        Assert.Contains("Sign in again", exception.Message, StringComparison.Ordinal);
    }
}

public sealed class ModelCatalogParserTests
{
    [Fact]
    public void Account_catalog_keeps_listed_models_in_server_order()
    {
        const string json = """
            {"models":[
              {"slug":"gpt-6.1-sol","display_name":"GPT-6.1 Sol","visibility":"list"},
              {"slug":"gpt-6-astra","display_name":"GPT-6 Astra","visibility":"list",
               "supported_reasoning_levels":[{"effort":"low","description":"Fast"},{"effort":"high"}],
               "default_reasoning_level":"low"},
              {"slug":"internal-preview","display_name":"Hidden","visibility":"hide"},
              {"slug":"gpt-7-nova","display_name":"GPT-7 Nova"}
            ]}
            """;

        var models = ModelCatalogParser.Parse(json);

        Assert.Equal(["gpt-6.1-sol", "gpt-6-astra", "gpt-7-nova"], models.Select(model => model.Id));
        Assert.Equal(5, models[0].SupportedEfforts.Count);
        Assert.Equal(["low", "high"], models[1].SupportedEfforts.Select(effort => effort.Value));
        Assert.Equal(ReasoningEffort.Low, models[1].DefaultEffort);
        Assert.Empty(models[2].SupportedEfforts);
    }

    [Fact]
    public void Standard_model_list_keeps_only_chat_models()
    {
        const string json = """{"object":"list","data":[{"id":"gpt-6-luna"},{"id":"gpt-live-transcribe"},{"id":"text-embedding-3-small"},{"id":"gpt-image-2"}]}""";

        var models = ModelCatalogParser.Parse(json);

        Assert.Equal("gpt-6-luna", Assert.Single(models).Id);
    }
}
