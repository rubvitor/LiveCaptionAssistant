using System.Text.Json;
using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Settings;
using LiveCaptionAssistant.Infrastructure.Storage;

namespace LiveCaptionAssistant.Tests.Infrastructure;

public sealed class SettingsSerializationTests
{
    [Theory]
    [InlineData(ResponseSpeed.Standard)]
    [InlineData(ResponseSpeed.Fast)]
    public void Response_speed_survives_saving_settings(ResponseSpeed speed)
    {
        var settings = new AppSettings { ChatResponseSpeed = speed, ChatReasoningEffort = "high", ChatModelId = "gpt-6.1-sol" };

        var json = JsonSerializer.Serialize(settings, StorageJsonContext.Default.AppSettings);
        var restored = JsonSerializer.Deserialize(json, StorageJsonContext.Default.AppSettings);

        Assert.NotNull(restored);
        Assert.Equal(speed, restored.ChatResponseSpeed);
        Assert.Equal("high", restored.ChatReasoningEffort);
        Assert.Equal("gpt-6.1-sol", restored.ChatModelId);
    }

    [Fact]
    public void Existing_settings_without_a_speed_use_standard()
    {
        var settings = JsonSerializer.Deserialize("""{"systemAudioDeviceId":"default","autoAnswerQuestions":true}""",
            StorageJsonContext.Default.AppSettings);

        Assert.NotNull(settings);
        Assert.Equal(ResponseSpeed.Standard, settings.ChatResponseSpeed);
        Assert.Equal(ReasoningEffort.Medium, settings.GetEffectiveReasoningEffort());
        Assert.True(settings.AutoAnswerQuestions);
        Assert.Equal("default", settings.SystemAudioDeviceId);
    }

    [Fact]
    public void Medium_default_is_applied_once_to_the_existing_profile_and_later_choices_are_preserved()
    {
        var existing = new AppSettings { ChatReasoningEffort = "max", ChatResponseSpeed = ResponseSpeed.Fast };

        var upgraded = JsonSettingsStore.ApplyDefaultsUpgrade(existing);

        Assert.Equal("medium", upgraded.ChatReasoningEffort);
        Assert.Equal(ResponseSpeed.Fast, upgraded.ChatResponseSpeed);
        Assert.Equal(1, upgraded.ResponsePreferencesVersion);
        var changed = upgraded with { ChatReasoningEffort = "high" };
        Assert.Equal("high", JsonSettingsStore.ApplyDefaultsUpgrade(changed).ChatReasoningEffort);
    }
}
