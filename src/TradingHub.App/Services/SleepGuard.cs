using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TradingHub.App.Services;

/// <summary>
/// No-sleep mode. While there are live strategies or Claude agents working, the Mac does not go to sleep from
/// inactivity: a macOS power assertion (like a video app does), with no password, released on its own when there
/// is nothing running. With the lid closed macOS sleeps anyway; to keep going like that you need "keep going with the lid closed":
/// it asks for your administrator password once per Trading Hub session and keeps the Mac from sleeping (pmset disablesleep) only
/// while Trading Hub is open. It is undone on its own when Trading Hub closes, even if it is killed, and optionally only on the
/// charger connected.
/// </summary>
internal sealed class SleepGuard : IDisposable
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    private const uint LevelOn = 255;

    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(IntPtr assertionType, uint level, IntPtr reason, out uint assertionId);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint assertionId);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    private uint _assertion;
    private bool _held;
    private Process? _helper;
    private DateTime _lastLidCheck;

    /// <summary>Which folder the flag for the helper is built on (it exists while the lid-closed mode has to work).</summary>
    public string FlagDirectory { get; set; } = AppPaths.Root;

    /// <summary>For the tests: instead of asking for permission and running the helper, it stores the AppleScript it would run.</summary>
    public Func<string, Process?>? RunAdminScript { get; set; }

    /// <summary>There is an active assertion: the Mac does not sleep from inactivity.</summary>
    public bool Awake => _held;

    /// <summary>Why it is awake (or null).</summary>
    public string? Reason { get; private set; }

    /// <summary>The lid-closed helper is running (permission has already been given).</summary>
    public bool LidHelperRunning => _helper is { HasExited: false } || (RunAdminScript != null && File.Exists(Flag));

    /// <summary>The helper did not start (for example, you cancelled the permission prompt).</summary>
    public string? LidError { get; private set; }

    private string Flag => Path.Combine(FlagDirectory, "lid-closed.active");
    private string AcOnlyFlag => Path.Combine(FlagDirectory, "lid-closed.only-on-charger");

    /// <summary>
    /// In tools and tests (data in another folder) the Mac's power settings are not touched and no permission is asked for,
    /// except with a fake helper (<see cref="RunAdminScript"/>).
    /// </summary>
    private static readonly bool Sandboxed =
        Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } || Environment.GetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD") == "off";

    /// <summary>The app calls it every few seconds with whatever is running.</summary>
    public void Update(bool keepAwake, bool lidClosed, bool onlyOnPower, string? reason)
    {
        if (Sandboxed && RunAdminScript == null) return;
        bool needed = keepAwake && reason != null;
        if (needed && !_held) Hold(reason!);
        else if (!needed && _held) Release();
        Reason = needed ? reason : null;

        // lid closed: it only starts once there is something that needs it and is kept while it stays enabled
        try
        {
            if (onlyOnPower) File.WriteAllText(AcOnlyFlag, "1");
            else if (File.Exists(AcOnlyFlag)) File.Delete(AcOnlyFlag);
        }
        catch
        {
            // no folder: it is retried
        }

        if (lidClosed && needed && !LidHelperRunning && (DateTime.UtcNow - _lastLidCheck).TotalSeconds > 30) StartLidHelper();
        else if (!lidClosed && LidHelperRunning) StopLidHelper();
    }

    private void Hold(string reason)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (Sandboxed)
        {
            _held = true; // in tests: it is simulated
            return;
        }

        IntPtr type = IntPtr.Zero, name = IntPtr.Zero;
        try
        {
            type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleSystemSleep", Utf8);
            name = CFStringCreateWithCString(IntPtr.Zero, "Trading Hub: " + reason, Utf8);
            _held = IOPMAssertionCreateWithName(type, LevelOn, name, out _assertion) == 0;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
        finally
        {
            if (type != IntPtr.Zero) CFRelease(type);
            if (name != IntPtr.Zero) CFRelease(name);
        }
    }

    private void Release()
    {
        if (!_held) return;
        if (Sandboxed)
        {
            _held = false;
            return;
        }

        try
        {
            IOPMAssertionRelease(_assertion);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        _held = false;
    }

    /// <summary>
    /// The helper with administrator permissions (macOS asks you for the password): while the flag exists and Trading Hub is
    /// open, the Mac does not sleep when the lid is closed (if chosen, only on the charger). When it finishes it leaves everything as it was.
    /// It executes nothing from files: it only checks whether they exist.
    /// </summary>
    internal string BuildScript(int pid)
    {
        static string Q(string path) => "'" + path.Replace("'", "'\\''") + "'";
        string sh =
            $"flag={Q(Flag)}; ac={Q(AcOnlyFlag)}; cur=x; " +
            $"while kill -0 {pid} 2>/dev/null && [ -f \"$flag\" ]; do " +
            "want=1; if [ -f \"$ac\" ] && ! /usr/bin/pmset -g ps | /usr/bin/head -1 | /usr/bin/grep -q 'AC Power'; then want=0; fi; " +
            "if [ \"$want\" != \"$cur\" ]; then /usr/bin/pmset -a disablesleep $want; cur=$want; fi; " +
            "sleep 5; done; /usr/bin/pmset -a disablesleep 0";
        string escaped = sh.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"do shell script \"{escaped}\" with prompt \"Trading Hub needs your permission to keep the Mac running with the lid closed while your strategies or agents are running. It is undone on its own when Trading Hub closes.\" with administrator privileges";
    }

    private void StartLidHelper()
    {
        _lastLidCheck = DateTime.UtcNow;
        LidError = null;
        try
        {
            Directory.CreateDirectory(FlagDirectory);
            File.WriteAllText(Flag, Environment.ProcessId.ToString());
            string script = BuildScript(Environment.ProcessId);
            if (RunAdminScript != null)
            {
                _helper = RunAdminScript(script);
                return;
            }

            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            var p = Process.Start(psi)!;
            _helper = p;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                // finished: either you cancelled the permission prompt (exit code other than 0) or the mode was switched off
                if (p.ExitCode != 0) LidError = "It was not enabled: your administrator permission is required.";
            };
        }
        catch (Exception ex)
        {
            LidError = ex.Message;
            CrashLog.Write(ex);
        }
    }

    private void StopLidHelper()
    {
        try
        {
            if (File.Exists(Flag)) File.Delete(Flag); // the helper sees it within 5 s and leaves the Mac as it was
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        _helper = null;
    }

    /// <summary>Does macOS have "do not sleep" set right now? (anyone can read it)</summary>
    public static bool SystemSleepDisabled()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/pmset", "-g") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return text.Split('\n').Any(l => l.Contains("SleepDisabled", StringComparison.Ordinal) && l.TrimEnd().EndsWith('1'));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Release();
        StopLidHelper();
    }
}
