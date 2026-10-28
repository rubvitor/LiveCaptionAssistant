using System.Text.Json.Serialization;
using LiveCaptionAssistant.Core.Settings;
using LiveCaptionAssistant.Infrastructure.Authentication;

namespace LiveCaptionAssistant.Infrastructure.Storage;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ChatGptAuthState))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
