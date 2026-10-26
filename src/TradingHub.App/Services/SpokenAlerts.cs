namespace TradingHub.App.Services;

/// <summary>
/// Says the alerts out loud with Windows' built-in voice (SAPI). Nothing leaves the PC. Off by default; only on Windows.
/// </summary>
public static class SpokenAlerts
{
    private static dynamic? _voice;
    private static bool _failed;

    public static void Say(AppSettings settings, string text)
    {
        if (!settings.SpokenAlerts || _failed || !OperatingSystem.IsWindows()) return;
        try
        {
            _voice ??= Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")!);
            // 1 = asynchronous, 2 = purge what is still being said: a new alert replaces an old one
            _voice!.Speak(text.Replace("@", " at ").Replace("·", ",").Replace("x", "x"), 3);
        }
        catch (Exception ex)
        {
            _failed = true; // no voice on this PC: do not try again every alert
            CrashLog.Write(ex);
        }
    }
}
