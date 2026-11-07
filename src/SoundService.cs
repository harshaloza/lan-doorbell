using System.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LanDoorbell;

/// <summary>Plays the doorbell sound (built-in or custom file) at a chosen volume.</summary>
internal sealed class SoundService
{
    private static readonly TimeSpan MaxSoundLength = TimeSpan.FromSeconds(20);
    private static readonly Lazy<byte[]> builtInSound = new(() => AppInfo.ReadResource("LanDoorbell.ding-dong.wav"));
    private int playing;

    public sealed record Options(
        string? SoundFile,
        int VolumePercent,
        bool UnmuteIfMuted,
        bool RaiseVolumeIfLow,
        int MinimumVolumePercent)
    {
        public static Options From(AppSettings s) => new(
            s.SoundFile, s.SoundVolumePercent, s.TemporarilyUnmuteForDoorbell, s.RaiseVolumeIfLow, s.MinimumVolumePercent);
    }

    /// <summary>Starts playing in the background. If a sound is already playing, the new one is skipped.</summary>
    public void Play(Options options)
    {
        if (Interlocked.Exchange(ref playing, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try { PlayCore(options); }
            finally { Volatile.Write(ref playing, 0); }
        });
    }

    private static void PlayCore(Options o)
    {
        MMDevice? device = null;
        bool restoreMute = false;
        float? restoreVolume = null;
        try
        {
            if (o.UnmuteIfMuted || o.RaiseVolumeIfLow)
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    var endpoint = device.AudioEndpointVolume;
                    if (o.UnmuteIfMuted && endpoint.Mute)
                    {
                        endpoint.Mute = false;
                        restoreMute = true;
                    }
                    float minimum = Math.Clamp(o.MinimumVolumePercent, 5, 100) / 100f;
                    if (o.RaiseVolumeIfLow && endpoint.MasterVolumeLevelScalar < minimum)
                    {
                        restoreVolume = endpoint.MasterVolumeLevelScalar;
                        endpoint.MasterVolumeLevelScalar = minimum;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not change the Windows volume/mute for the doorbell", ex);
                }
            }

            if (o.SoundFile != null && TryPlay(o.SoundFile, o.VolumePercent)) return;
            if (TryPlay(null, o.VolumePercent)) return;
            SystemSounds.Beep.Play();
        }
        finally
        {
            if (device != null)
            {
                try
                {
                    if (restoreVolume is float v) device.AudioEndpointVolume.MasterVolumeLevelScalar = v;
                    if (restoreMute) device.AudioEndpointVolume.Mute = true;
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not restore the Windows volume/mute", ex);
                }
                device.Dispose();
            }
        }
    }

    /// <param name="file">A .wav or .mp3 file, or null for the built-in sound.</param>
    private static bool TryPlay(string? file, int volumePercent)
    {
        try
        {
            using WaveStream reader = file == null
                ? new WaveFileReader(new MemoryStream(builtInSound.Value, writable: false))
                : new AudioFileReader(file);
            var provider = new VolumeSampleProvider(reader.ToSampleProvider())
            {
                Volume = Math.Clamp(volumePercent, 0, 100) / 100f
            };

            using var output = new WaveOutEvent();
            using var finished = new ManualResetEventSlim();
            output.PlaybackStopped += (_, _) => finished.Set();
            output.Init(provider);
            output.Play();
            if (!finished.Wait(MaxSoundLength)) output.Stop();   // cap very long custom sounds
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not play sound {file ?? "(built-in)"}", ex);
            return false;
        }
    }
}
