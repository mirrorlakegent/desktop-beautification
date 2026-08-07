using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using DesktopSuite.Wallpaper;

namespace DesktopSuite;

/// <summary>
/// Thin client for mpv's JSON IPC. The renderer starts mpv with
/// --input-ipc-server=\\.\pipe\desktopsuite-wp, which lets us change mute/volume at runtime
/// without restarting the (deliberately detached) renderer. Fire-and-forget: a failed send
/// (renderer not running, pipe busy) is logged and swallowed — the caller still persists the
/// intended state to settings, so it takes effect on the next start if not right now.
/// </summary>
public static class MpvIpc
{
    public static bool SetMute(bool muted) => SendCore(Command("set_property", "mute", muted));
    public static bool SetVolume(int volume) => SendCore(Command("set_property", "volume", Math.Clamp(volume, 0, 100)));

    public static void SetAudio(bool enabled, int volume)
    {
        SetMute(!enabled);
        if (enabled) SetVolume(volume);
    }

    private static string Command(string op, string prop, object value) =>
        $"{{\"command\":[\"{op}\",\"{prop}\",{Format(value)}]}}\n";

    private static string Format(object v) => v switch
    {
        bool b => b ? "true" : "false",
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string s => $"\"{s}\"",
        _ => $"\"{v}\""
    };

    private static bool SendCore(string json)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", MpvHost.IpcPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(1500);

            byte[] bytes = Encoding.UTF8.GetBytes(json);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();

            // Drain one response line so mpv's outbound buffer never fills up across many toggles.
            // A timeout here is harmless — the command was already delivered.
            pipe.ReadTimeout = 1000;
            var buf = new byte[512];
            try { pipe.Read(buf, 0, buf.Length); } catch { /* timeout or no response — ignore */ }

            return true;
        }
        catch (Exception ex)
        {
            HostLog.Write($"MpvIpc send failed: {ex.Message}");
            return false;
        }
    }
}
