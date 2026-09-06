using NAudio.Wave;

namespace Beamcast.Audio;

/// <summary>
/// Plays the short notification sounds shipped in Assets/Sounds through WASAPI shared mode. Each
/// play opens its own output and releases it when the clip ends, so clips can overlap and nothing
/// stays open between them. Failures are swallowed: a missing sound must never break a broadcast.
/// </summary>
public static class SoundEffects
{
    public const string ViewerIn = "viewer-in";
    public const string ViewerOut = "viewer-out";

    public static string PathFor(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", name + ".wav");

    public static void Play(string name, float volume = 0.8f)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
            return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var reader = new AudioFileReader(path) { Volume = Math.Clamp(volume, 0f, 1f) };
                var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 80);
                output.PlaybackStopped += (_, _) =>
                {
                    SafeTry.Run(() => output.Dispose());
                    SafeTry.Run(() => reader.Dispose());
                };
                output.Init(reader);
                output.Play();
            }
            catch (Exception ex)
            {
                Diag.Log("sound: " + name + " failed: " + ex.Message);
            }
        });
    }
}
