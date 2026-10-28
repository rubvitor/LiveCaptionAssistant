namespace LiveCaptionAssistant.Core.Assistant;

/// <summary>Value of the Responses API <c>reasoning.effort</c> parameter.</summary>
public sealed record ReasoningEffort
{
    public ReasoningEffort(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim().ToLowerInvariant();
    }

    public static ReasoningEffort None { get; } = new("none");

    public static ReasoningEffort Minimal { get; } = new("minimal");

    public static ReasoningEffort Low { get; } = new("low");

    public static ReasoningEffort Medium { get; } = new("medium");

    public static ReasoningEffort High { get; } = new("high");

    public static ReasoningEffort ExtraHigh { get; } = new("xhigh");

    public static ReasoningEffort Max { get; } = new("max");

    public string Value { get; }

    public string DisplayName => Value switch
    {
        "none" => "None",
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra high",
        "max" => "Max",
        _ => string.Concat(char.ToUpperInvariant(Value[0]).ToString(), Value[1..]),
    };

    public override string ToString() => Value;
}

public sealed record ChatModelInfo(
    string Id,
    string DisplayName,
    IReadOnlyList<ReasoningEffort> SupportedEfforts,
    ReasoningEffort? DefaultEffort)
{
    public bool Supports(ReasoningEffort effort) => SupportedEfforts.Contains(effort);
}

/// <summary>
/// Reasoning capabilities of current OpenAI models (per the model pages, October 2026).
/// Used to describe models returned by the API and as an offline fallback list.
/// </summary>
public static class KnownModelCatalog
{
    public const string PreferredModelId = "gpt-6.1-sol";

    private static readonly ReasoningEffort[] NoneToMax =
        [ReasoningEffort.None, ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.ExtraHigh, ReasoningEffort.Max];

    private static readonly ReasoningEffort[] LowToMax =
        [ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.ExtraHigh, ReasoningEffort.Max];

    private static readonly ReasoningEffort[] NoneToExtraHigh =
        [ReasoningEffort.None, ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.ExtraHigh];

    private static readonly ChatModelInfo[] Models =
    [
        new("gpt-6-astra", "GPT-6 Astra", LowToMax, ReasoningEffort.Medium),
        new("gpt-6.1-sol", "GPT-6.1 Sol", LowToMax, ReasoningEffort.Medium),
        new("gpt-6-sol", "GPT-6 Sol", NoneToMax, ReasoningEffort.Medium),
        new("gpt-6-luna", "GPT-6 Luna", NoneToMax, ReasoningEffort.Medium),
        new("gpt-5.6-sol", "GPT-5.6 Sol", NoneToMax, ReasoningEffort.Medium),
        new("gpt-5.6-terra", "GPT-5.6 Terra", NoneToMax, ReasoningEffort.Medium),
        new("gpt-5.6-luna", "GPT-5.6 Luna", NoneToMax, ReasoningEffort.Medium),
        new("gpt-5.5", "GPT-5.5", NoneToExtraHigh, ReasoningEffort.Medium),
        new("gpt-5.4-mini", "GPT-5.4 Mini", NoneToExtraHigh, ReasoningEffort.None),
    ];

    public static IReadOnlyList<ChatModelInfo> All => Models;

    /// <summary>Finds a known model by exact id or by dated snapshot (e.g. <c>gpt-5.5-2026-04-23</c>).</summary>
    public static ChatModelInfo? Find(string modelId)
    {
        ChatModelInfo? best = null;
        foreach (var model in Models)
        {
            var matches = string.Equals(modelId, model.Id, StringComparison.OrdinalIgnoreCase)
                || modelId.StartsWith(model.Id + "-", StringComparison.OrdinalIgnoreCase);
            if (matches && (best is null || model.Id.Length > best.Id.Length))
            {
                best = model;
            }
        }

        return best;
    }

    /// <summary>Describes a model returned by the API, filling in reasoning capabilities when they are known.</summary>
    public static ChatModelInfo Describe(string modelId, string? displayName)
    {
        var known = Find(modelId);
        var name = string.IsNullOrWhiteSpace(displayName) ? known?.DisplayName ?? modelId : displayName;
        return known is null
            ? new ChatModelInfo(modelId, name, [], null)
            : known with { Id = modelId, DisplayName = name };
    }
}
