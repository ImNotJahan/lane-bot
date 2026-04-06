using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;
using ElevenLabs.Voices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wizard.Utility;

namespace Wizard.Head.Mouths
{
    public sealed class ElevenlabsTTS : IMouth
    {
        readonly ElevenLabsClient client;
        Voice?   voice;

        readonly string voiceID;

        readonly float stability, similarity, tempo, pitch, rate;
        readonly bool  tune;

        public ElevenlabsTTS()
        {
            if(Settings.instance is null || Settings.instance.Speech is null)
            {
                // default speech settings
                voiceID = "MEJe6hPrI48Kt2lFuVe3";

                stability  = 0.84f;
                similarity = 0.74f;
                tempo      = 25f;

                pitch = rate = 0;

                tune = true;
            }
            else
            {
                SpeechSettings settings = Settings.instance.Speech;

                voiceID = settings.Voice;

                stability  = settings.Stability;
                similarity = settings.Similarity;
                tempo      = settings.Tempo;
                pitch      = settings.Pitch;
                rate       = settings.Rate;
                tune       = settings.Tune;
            }

            client = new ElevenLabsClient(DotNetEnv.Env.GetString("ELEVENLABS_KEY"));
        }

        public async IAsyncEnumerable<byte[]> Speak(string text)
        {
            voice ??= await client.VoicesEndpoint.GetVoiceAsync(voiceID);

            TextToSpeechRequest request = new(
                voice: voice,
                text:  text,
                model: Model.FlashV2_5,
                voiceSettings: new VoiceSettings(
                    stability:       stability,
                    similarityBoost: similarity
                ),
                outputFormat: OutputFormat.PCM_22050
            );

            VoiceClip clip = await client.TextToSpeechEndpoint.TextToSpeechAsync(request);

            // we process and return the audio data as we receive it
            foreach (byte[] frame in ProcessClip(clip.ClipData.ToArray())) yield return frame;
        }

        private IEnumerable<byte[]> ProcessClip(byte[] rawPcm)
        {
            RawSourceWaveStream reader = new(
                new MemoryStream(rawPcm),
                new WaveFormat(22050, 16, 1)
            );

            ISampleProvider processed = new SoundTouchSampleProvider(
                reader.ToSampleProvider(),
                tempo:          tempo,
                pitchSemiTones: pitch,
                rate:           rate,
                tuneForSpeech:  tune
            );

            IWaveProvider waveProvider = new MonoToStereoSampleProvider(
                new WdlResamplingSampleProvider(processed, 48000)
            ).ToWaveProvider16();

            byte[] buffer = new byte[3840];
            int    bytesRead;

            while ((bytesRead = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytesRead < buffer.Length) Array.Clear(buffer, bytesRead, buffer.Length - bytesRead);

                byte[] frame = new byte[buffer.Length];
                
                Buffer.BlockCopy(buffer, 0, frame, 0, buffer.Length);

                yield return frame;
            }
        }
    }
}
