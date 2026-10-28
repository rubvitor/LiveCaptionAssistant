# Live Caption Assistant

Aplicativo Windows (.NET 10 / WPF) para transcrever áudio em inglês e português com Whisper local e responder perguntas com a conta ChatGPT conectada.

## Executar

```powershell
dotnet run --project src/LiveCaptionAssistant.App
```

1. Deixe **Auto-detect (EN / PT)** para reconhecer os dois idiomas automaticamente.
2. Em **Audio settings**, use **Automatic — all Windows outputs**. Esse modo captura alto-falantes e headsets e acompanha dispositivos conectados durante a sessão. Uma saída escolhida manualmente precisa ser a mesma selecionada no Google Meet.
3. Marque **My microphone** antes de **Start** para incluir a própria voz, identificada como **Me**. Com essa opção desmarcada, apenas o áudio reproduzido pelo Windows é transcrito.
4. Clique em **Start** e espere a preparação do modelo terminar. O medidor mostra o sinal efetivamente recebido. Após dez segundos sem sinal, o aplicativo orienta a verificar a saída da reunião.
5. Conecte a conta ChatGPT no painel da direita e escolha modelo, esforço e velocidade **Standard / Fast**. O esforço padrão é **Medium**. As escolhas são salvas. Fast solicita processamento mais rápido quando disponível para o modelo e a conta e pode consumir mais do plano. Se o serviço aplicar Standard, o cartão informa isso explicitamente.
6. Com **Auto-answer questions** marcado, perguntas identificadas são respondidas automaticamente no painel da direita, com o texto chegando aos poucos. Isso inclui perguntas de qualquer assunto.
7. Use **Responder** ou **Ctrl+Shift+Space** para responder às últimas falas transcritas dos outros participantes, mesmo que a pergunta não tenha sido identificada. Também é possível digitar uma pergunta no campo **Ask** ou usar **Ask AI** em uma transcrição.

A captura de saídas usa WASAPI loopback: a voz de outro participante precisa estar sendo reproduzida no Windows. A detecção de perguntas é heurística; o botão Responder permite contornar perguntas não identificadas. O tempo total depende do áudio, do hardware e do serviço ChatGPT. Quando disponível, uma GPU Vulkan dedicada é escolhida antes da integrada; a preparação inicial ocorre antes de começar a escuta.

## Diagnóstico

- Medidor parado durante uma fala: confirme o som da reunião e a seleção em **Audio settings**. Prefira **Automatic** ao alternar entre fones e alto-falantes.
- Falta a própria voz: ative **My microphone** antes de iniciar. Use fones para reduzir eco.
- Há transcrição, mas falta resposta: confirme a conta, **Auto-answer questions** e use **Responder**. Erros de autenticação, limites e serviço aparecem no cartão da resposta.
- Os registros em `%LOCALAPPDATA%\LiveCaptionAssistant\logs` mostram dispositivos, sinal, backend/GPU e falhas. Abra a pasta por **Settings → Open logs folder**.

## Verificação

```powershell
dotnet build LiveCaptionAssistant.slnx
dotnet test --solution LiveCaptionAssistant.slnx
```

As configurações ficam em `%APPDATA%\LiveCaptionAssistant`; os modelos ficam em `%LOCALAPPDATA%\LiveCaptionAssistant\models`.

Referência da integração de velocidade: [OpenAI Fast mode](https://developers.openai.com/api/docs/guides/fast-mode).
