using Microsoft.CognitiveServices.Speech;
using Wizard.Utility;

namespace Wizard.Head.Mouths
{
    public sealed class AzureTTS : IMouth
    {
        readonly SpeechSynthesizer synthesizer;
        
        readonly float tempo;
        readonly float pitch;
        readonly float similarity;

        public AzureTTS()
        {
            SpeechConfig config = SpeechConfig.FromSubscription(
                DotNetEnv.Env.GetString("AZURE_KEY"),
                DotNetEnv.Env.GetString("AZURE_REGION")
            );

            if(Settings.instance is null || Settings.instance.Speech is null)
            {
                config.SpeechSynthesisVoiceName = "en-US-CoraNeural";

                tempo      = 10;
                pitch      = -3;
                similarity = 0.5f;
            }
            else
            {
                SpeechSettings settings = Settings.instance.Speech;

                config.SpeechSynthesisVoiceName = settings.Voice;

                tempo      = settings.Tempo;
                pitch      = settings.Pitch;
                similarity = settings.Similarity;
            }

            synthesizer = new(config);
        }

        public async Task<byte[]> Speak(string text)
        {
            string ssml = @$"
            <speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis'
                xmlns:mstts='http://www.w3.org/2001/mstts' xml:lang='en-US'>
            <voice name='en-US-CoraNeural'>
                <mstts:express-as style='calm' styledegree='{similarity}'>
                <prosody rate='{tempo}%' pitch='{pitch}%'>
                    {text}
                </prosody>
                </mstts:express-as>
            </voice>
            </speak>";

            SpeechSynthesisResult result = await synthesizer.SpeakSsmlAsync(ssml);

            return result.AudioData;
        }
    }
}