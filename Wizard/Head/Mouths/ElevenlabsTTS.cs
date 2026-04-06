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

        readonly float stability;
        readonly float similarity;
        readonly float tempo;
        readonly float pitch;
        readonly float rate;
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

        public async Task<byte[]> Speak(string text)
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

            Logger.LogDebug("Voice clip length: " + clip.ClipData.Length);

            RawSourceWaveStream reader = new(
                new MemoryStream(clip.ClipData.ToArray()),
                new WaveFormat(22050, 16, 1)
            );

            ISampleProvider samples = reader.ToSampleProvider();

            ISampleProvider processed = new SoundTouchSampleProvider(
                samples,
                tempo:          tempo,
                pitchSemiTones: pitch,
                rate:           rate,
                tuneForSpeech:  tune
            );

            ISampleProvider resampled = new WdlResamplingSampleProvider(processed, 48000);

            MonoToStereoSampleProvider stereo = new(resampled);

            var waveProvider = stereo.ToWaveProvider16();

            using var output = new MemoryStream();
            byte[]    buffer = new byte[3840];
            
            int bytesRead;
            while ((bytesRead = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytesRead < buffer.Length)
                {
                    // Pad the final frame with silence
                    Array.Clear(buffer, bytesRead, buffer.Length - bytesRead);
                }
                output.Write(buffer, 0, buffer.Length);
            }

            byte[] discordReady = output.ToArray();

            Logger.LogDebug($"discordReady length: {discordReady.Length}, remainder: {discordReady.Length % 3840}");

            Logger.LogDebug("Resampled PCM bytes: " + discordReady.Length);
            
            return discordReady;
        }
    }
}