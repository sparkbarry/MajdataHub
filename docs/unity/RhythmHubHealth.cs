// Drop into a Unity game's Assets/ to support RhythmHub's launch health check (docs/update-protocol.md §7).
// Call RhythmHubHealth.ReportReady() once the game is genuinely usable, e.g. when the title screen is shown.
// Without RhythmHub (no environment variables) it does nothing.
using System;
using System.IO;

public static class RhythmHubHealth
{
    private static bool _reported;

    public static void ReportReady()
    {
        if (_reported) return;
        _reported = true;

        var path = Environment.GetEnvironmentVariable("RHYTHMHUB_HEALTH_FILE");
        var nonce = Environment.GetEnvironmentVariable("RHYTHMHUB_LAUNCH_NONCE");
        var version = Environment.GetEnvironmentVariable("RHYTHMHUB_GAME_VERSION");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(version)) return;

        try
        {
            // Write then rename so the launcher never reads a half-written file.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, "{\"nonce\":\"" + nonce + "\",\"version\":\"" + version + "\"}");
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("RhythmHub health marker not written: " + e.Message);
        }
    }
}
