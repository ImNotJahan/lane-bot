using Microsoft.CognitiveServices.Speech;

namespace Wizard.Head.Mouths
{
    public sealed class AzureTTS : IMouth
    {
        readonly SpeechSynthesizer synthesizer;

        public AzureTTS()
        {
            SpeechConfig config = SpeechConfig.FromSubscription(
                DotNetEnv.Env.GetString("AZURE_KEY"),
                DotNetEnv.Env.GetString("AZURE_REGION")
            );
            config.SpeechSynthesisVoiceName = "en-US-CoraNeural";

            synthesizer = new(config);
        }

        public async Task<byte[]> Speak(string text)
        {
            string ssml = @$"
            <speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis'
                xmlns:mstts='http://www.w3.org/2001/mstts' xml:lang='en-US'>
            <voice name='en-US-CoraNeural'>
                <mstts:express-as style='calm' styledegree='0.5'>
                <prosody rate='20%' pitch='-3%'>
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