using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Beamcast.Audio;

/// <summary>Lists the process ids that currently own an audio session on any active output device.</summary>
public static class AudioSessionScanner
{
    public static IReadOnlyList<int> SessionProcessIds()
    {
        var pids = new HashSet<int>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        try
                        {
                            if (session.State == AudioSessionState.AudioSessionStateExpired)
                                continue;
                            var pid = (int)session.GetProcessID;
                            if (pid > 4)
                                pids.Add(pid);
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception) { }

        return pids.ToList();
    }
}
