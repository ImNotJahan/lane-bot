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

        const string voiceID = "MEJe6hPrI48Kt2lFuVe3";

        public ElevenlabsTTS()
        {
            client = new ElevenLabsClient(DotNetEnv.Env.GetString("ELEVENLABS_KEY"));
        }

        public async Task<byte[]> Speak(string text)
        {
            voice ??= await client.VoicesEndpoint.GetVoiceAsync(voiceID);

            TextToSpeechRequest request = new(
                voice: voice,
                text: text,
                model: Model.FlashV2_5,
                voiceSettings: new VoiceSettings(
                    stability: 0.84f,
                    similarityBoost: 0.74f
                ),
                outputFormat: OutputFormat.PCM_22050
            );

            VoiceClip clip = await client.TextToSpeechEndpoint.TextToSpeechAsync(request);

            Logger.LogInformation("Voice clip length: " + clip.ClipData.Length);

            RawSourceWaveStream reader = new(
                new MemoryStream(clip.ClipData.ToArray()),
                new WaveFormat(22050, 16, 1)
            );

            WdlResamplingSampleProvider resampled = new (
                reader.ToSampleProvider(),
                48000
            );

            MonoToStereoSampleProvider stereo = new(resampled);

            var waveProvider = stereo.ToWaveProvider16();

            using var output = new MemoryStream();
            byte[] buffer = new byte[3840];
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

            Logger.LogInformation($"discordReady length: {discordReady.Length}, remainder: {discordReady.Length % 3840}");

            Logger.LogInformation("Resampled PCM bytes: " + discordReady.Length);
            
            return discordReady;
        }
    }
}