using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Wizard.Body;

namespace Wizard.Head.Ears
{
    public sealed class AzureSTT : IEar
    {
        readonly SpeechRecognizer recognizer;

        public AzureSTT(DiscordAudioStream stream)
        {
            SpeechConfig speechConfig = SpeechConfig.FromSubscription(
                DotNetEnv.Env.GetString("AZURE_KEY"),
                DotNetEnv.Env.GetString("AZURE_REGION")
            );
            speechConfig.SpeechRecognitionLanguage = "en-US";

            speechConfig.SetProperty(
                PropertyId.Speech_SegmentationSilenceTimeoutMs,
                "300"
            );

            AudioConfig audioConfig = AudioConfig.FromStreamInput(stream.PushStream);

            recognizer = new(speechConfig, audioConfig);
        }

        public async Task<string> Listen()
        {
            SpeechRecognitionResult result = await recognizer.RecognizeOnceAsync();
            return result.Text;
        }
    }
}
