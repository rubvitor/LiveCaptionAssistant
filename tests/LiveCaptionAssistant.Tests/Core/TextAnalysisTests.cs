using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Transcription;

namespace LiveCaptionAssistant.Tests.Core;

public sealed class HeuristicQuestionDetectorTests
{
    private readonly HeuristicQuestionDetector _detector = new();

    [Theory]
    [InlineData("Can you tell me about your experience with Kubernetes?")]
    [InlineData("What would you do if a deployment failed in production?")]
    [InlineData("Tell me about a time you disagreed with your manager.")]
    [InlineData("So, how do you usually handle tight deadlines")]
    [InlineData("Walk me through the architecture of your last project.")]
    [InlineData("Você pode me falar sobre o seu último projeto?")]
    [InlineData("Me fale um pouco sobre você.")]
    [InlineData("Quais são os seus pontos fortes")]
    [InlineData("Então, como você lida com pressão no trabalho")]
    [InlineData("Por que você quer sair da empresa atual?")]
    [InlineData("Can you explain the difference between dependency injection and the service locator pattern.")]
    [InlineData("How do you handle concurrent database updates.")]
    [InlineData("Você pode explicar a diferença entre injeção de dependências e o padrão localizador de serviços no desenvolvimento de software.")]
    [InlineData("Qual é a diferença entre uma interface e uma classe abstrata.")]
    public void Recognizes_questions_and_interview_prompts(string text) => Assert.True(_detector.IsQuestion(text));

    [Theory]
    [InlineData("")]
    [InlineData("Okay.")]
    [InlineData("What?")]
    [InlineData("I worked at a startup for three years.")]
    [InlineData("How I see it, we need more automated tests.")]
    [InlineData("Que bom, obrigado pela resposta.")]
    [InlineData("Eu trabalhei com Java e Kotlin por cinco anos.")]
    [InlineData("Você pode usar injeção de dependências nesse projeto.")]
    [InlineData("Você tem razão sobre os testes.")]
    [InlineData("Como você disse, precisamos melhorar os testes.")]
    public void Ignores_statements(string text) => Assert.False(_detector.IsQuestion(text));
}

public sealed class HallucinationFilterTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("(music)")]
    [InlineData("♪ ♪")]
    [InlineData("Thanks for watching!")]
    [InlineData("Legendas pela comunidade Amara.org")]
    [InlineData("Inscreva-se no canal")]
    public void Removes_known_silence_artifacts(string text) => Assert.Equal(string.Empty, HallucinationFilter.Clean(text, noSpeechProbability: 0.1f));

    [Fact]
    public void Drops_short_filler_only_when_the_model_doubts_there_was_speech()
    {
        Assert.Equal(string.Empty, HallucinationFilter.Clean("Thank you.", noSpeechProbability: 0.6f));
        Assert.Equal("Thank you.", HallucinationFilter.Clean("Thank you.", noSpeechProbability: 0.05f));
    }

    [Fact]
    public void Collapses_looping_repetitions()
    {
        Assert.Equal("Obrigado.", HallucinationFilter.Clean("Obrigado. Obrigado. Obrigado.", noSpeechProbability: 0f));
        Assert.Equal("We shipped it. It worked.", HallucinationFilter.Clean("We shipped it. We shipped it. It worked.", noSpeechProbability: 0f));
    }

    [Fact]
    public void Strips_inline_tags_and_keeps_the_speech()
    {
        Assert.Equal("Hello there friend", HallucinationFilter.Clean("Hello there [inaudible] friend", noSpeechProbability: 0f));
    }
}

public sealed class LanguageSelectionPolicyTests
{
    [Fact]
    public void Fixed_mode_always_uses_the_selected_language()
    {
        var language = LanguageSelectionPolicy.ResolveForcedLanguage(
            LanguageMode.Portuguese, isPartial: false, TestAudio.Samples(5), languageDetectedForSegment: SpokenLanguage.English, SpokenLanguage.English);

        Assert.Equal(SpokenLanguage.Portuguese, language);
    }

    [Fact]
    public void Auto_mode_reuses_the_segment_language_for_partial_updates()
    {
        var language = LanguageSelectionPolicy.ResolveForcedLanguage(
            LanguageMode.AutoDetect, isPartial: true, TestAudio.Samples(3), SpokenLanguage.Portuguese, SpokenLanguage.English);

        Assert.Equal(SpokenLanguage.Portuguese, language);
    }

    [Fact]
    public void Auto_mode_keeps_the_session_language_for_clips_too_short_to_detect()
    {
        var language = LanguageSelectionPolicy.ResolveForcedLanguage(
            LanguageMode.AutoDetect, isPartial: false, TestAudio.Samples(0.6), null, SpokenLanguage.Portuguese);

        Assert.Equal(SpokenLanguage.Portuguese, language);
    }

    [Fact]
    public void Auto_mode_detects_the_language_of_longer_final_clips()
    {
        var language = LanguageSelectionPolicy.ResolveForcedLanguage(
            LanguageMode.AutoDetect, isPartial: false, TestAudio.Samples(3), SpokenLanguage.English, SpokenLanguage.English);

        Assert.Null(language);
    }
}
