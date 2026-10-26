using Avalonia.Input;

namespace TradingHub.App.Services;

/// <summary>Per-platform bits shared by the views: the shortcut modifier (⌘ on Mac, Ctrl elsewhere), the mono font, opening files.</summary>
public static class Plat
{
    public static readonly KeyModifiers Cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public const string Mono = "Cascadia Mono, Consolas, Menlo, monospace";

    /// <summary>Text for a modifier set, e.g. "⇧⌘K" on Mac and "Ctrl+Shift+K" on Windows.</summary>
    public static string Keys(KeyModifiers m, string key)
    {
        bool mac = OperatingSystem.IsMacOS();
        string Part(KeyModifiers f, string macText, string winText) => m.HasFlag(f) ? (mac ? macText : winText + "+") : "";
        var ctrl = mac ? Part(KeyModifiers.Control, "⌃", "Ctrl") : Part(KeyModifiers.Control, "", "Ctrl");
        return ctrl + Part(KeyModifiers.Alt, "⌥", "Alt") + Part(KeyModifiers.Shift, "⇧", "Shift") + Part(KeyModifiers.Meta, "⌘", "Win") + key;
    }

    /// <summary>Opens a file, folder or address with the default app.</summary>
    public static void Open(string target)
    {
        var psi = OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }
            : new System.Diagnostics.ProcessStartInfo("open", $"\"{target}\"") { UseShellExecute = false };
        System.Diagnostics.Process.Start(psi);
    }

    /// <summary>Shows a file selected in Finder / Explorer.</summary>
    public static void Reveal(string path) => System.Diagnostics.Process.Start(OperatingSystem.IsWindows()
        ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }
        : new System.Diagnostics.ProcessStartInfo("open", $"-R \"{path}\"") { UseShellExecute = false });
}
