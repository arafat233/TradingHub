# Porting Trading Hub to Windows with Claude

Trading Hub is written in C#/.NET 10 with Avalonia and SkiaSharp, which work the same on Windows. Most of the code
(data, feeds, charts, backtesting, strategies, indicators) does not depend on the operating system. What is
macOS-specific lives in a handful of clearly identified places, and this guide gets everything ready for **Claude
Code** to do the work on a Windows PC: the inventory of what has to change, the stage-by-stage plan and the full
prompt.

> Requirements on the PC: 64-bit Windows 10/11 (x64 or ARM64), [Git](https://git-scm.com), the
> [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Claude Code](https://claude.com/claude-code) signed in
> (`npm install -g @anthropic-ai/claude-code` or the official installer).

## What depends on macOS (inventory)

| Where | What it does on macOS | What to do on Windows |
|---|---|---|
| `build-app.sh` | Publishes for `osx-arm64`, builds `Trading Hub.app`, signs it with `codesign` and copies it with `ditto` | A `build-windows.ps1` that publishes `win-x64` (and `win-arm64`) self-contained, with a `.ico` icon; optionally, an installer (Inno Setup or MSIX) |
| `src/TradingHub.App/Services/Connections.cs` (`Keychain` class) | Stores the keys in the Keychain with `/usr/bin/security` | Windows Credential Manager (`CredWrite`/`CredRead`/`CredDelete` from `advapi32.dll`) or DPAPI (`ProtectedData`) with a per-user encrypted file. Keep the same `Get`/`Set` interface |
| `src/TradingHub.App/Services/SleepGuard.cs` | Keeps the Mac from sleeping (IOKit `IOPMAssertion`) and, with the lid closed, `pmset` with administrator permission via `osascript` | `SetThreadExecutionState(ES_CONTINUOUS \| ES_SYSTEM_REQUIRED)` from `kernel32.dll`. The closed-lid option is hidden or replaced with an explanation of the Windows power settings |
| `src/TradingHub.App/Program.cs` | `CGGetActiveDisplayList` (waits for a display before starting) | Skip that wait on Windows (`OperatingSystem.IsMacOS()`) |
| `src/TradingHub.App/Services/MenuBar.cs` | Menu-bar icon (Avalonia `TrayIcon`) and the mouse position via `objc_msgSend` | `TrayIcon` works in the Windows tray; the mouse position comes from `GetCursorPos` (`user32.dll`) and the panel opens above the taskbar |
| `src/TradingHub.App/Windows/MainWindow.cs` (`Shell.OpenUrl`, `OpenFolder`…) and `Views/StrategiesView.Reports.cs` | Opens links, folders and reports with `open` | `Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })`; to show a file in its folder, `explorer.exe /select,"path"` |
| `src/TradingHub.App/Views/TradingView.cs` | Sounds with `afplay` | `System.Media.SoundPlayer` (WAV) or an Avalonia player; keep the same sound files |
| `src/TradingHub.App/Services/ClaudeCode.cs` | Looks for the `claude` CLI in Mac paths, builds the `PATH` with `/usr/local/bin`, `/opt/homebrew/bin`… and opens Terminal with `osascript` for `/login` | Look for `claude.exe`/`claude.cmd` in `%USERPROFILE%\.local\bin`, `%APPDATA%\npm`, `%LOCALAPPDATA%\Programs` and the `PATH` (with `where claude`); open `cmd.exe /k claude /login` or Windows Terminal (`wt.exe`) |
| `MainWindow` (title bar) | Navigation sits inside the macOS title bar (`ExtendClientAreaToDecorationsHint`), with the traffic lights on the left | On Windows the window buttons go on the right: leave room on the right instead of the left and check that dragging and maximizing work |
| Shortcuts (`KeyModifiers.Meta`, ~30 uses) | ⌘ | `Ctrl` on Windows: a `Keys.Command` helper that returns `Meta` on Mac and `Control` on Windows, and help text that says «Ctrl» |
| Fonts (`"Menlo, monospace"`, ~12 uses) | Mac monospaced font | `"Cascadia Mono, Consolas, monospace"` on Windows (a shared `Ui.MonoFont`) |
| `TradingHub.Common.Product` | Data in `~/Library/Application Support/Trading Hub` and files in `~/Documents/Trading Hub` | It already uses `Environment.SpecialFolder`: on Windows that becomes `%APPDATA%\Trading Hub` and `Documents\Trading Hub` with no changes. Review comments and interface text that say «Mac» |
| Interface text | Mentions «the Mac», «macOS Keychain», «menu bar», ⌘Q | Per-platform text (`OperatingSystem.IsWindows()`): «the PC», «Credential Manager», «system tray», Alt+F4 |

Everything else (Rithmic, Tradovate, Binance, Bybit, OANDA, Alpaca, cTrader, Databento, the backtest engine, the
walk-forward, the Skia charts, the Roslyn strategy compiler and the MCP lab) is cross-platform.

## Stage-by-stage plan

1. **Make it build and run.** `dotnet build TradingHub.slnx` and `dotnet test tests/TradingHub.Tests` on Windows.
   Isolate the Mac parts behind `OperatingSystem.IsMacOS()` or behind interfaces with two implementations (for example
   `ISecretStore`, `ISleepGuard`, `IShell`), without breaking the Mac version.
2. **Secure keys.** The Windows key store, with tests that do not touch the user's real credentials (as on Mac, an
   environment variable points at a test file).
3. **System integrations.** Sleep, system tray, opening links and folders, sounds, Claude Code.
4. **Interface.** Title bar, Ctrl shortcuts, fonts, per-platform text. Review each section with the headless scenes
   (`dotnet run --project tools/TradingHub.UiSnapshot -- <folder> 4 <scene>`).
5. **Packaging.** `build-windows.ps1` with a self-contained publish and icon, and optionally an installer.
6. **Verification.** Unit tests, scenes and a manual test with the simulated account and free Binance data.

## The prompt for Claude Code

Clone the repository on the Windows PC, open a terminal in the folder, run `claude` and paste this:

```
I want to port Trading Hub to Windows without breaking the macOS version. It is a C#/.NET 10 desktop app with
Avalonia and SkiaSharp (solution TradingHub.slnx). Read CLAUDE.md, README.md, docs/development.md and
PORTING_TO_WINDOWS.md first (it has the inventory of what depends on macOS and the stage-by-stage plan).

Rules:
- The macOS version has to keep building and working exactly as before. Separate platform-specific code with
  OperatingSystem.IsMacOS()/IsWindows() or, better, with small interfaces (ISecretStore, ISleepGuard, IShell, ISound)
  and one implementation per platform. Do not copy whole files.
- Never store keys, tokens or passwords in text files: on Windows use Credential Manager
  (advapi32 CredWrite/CredRead/CredDelete) or DPAPI. The tests must not touch real credentials: use an environment
  variable (like TRADINGHUB_KEYCHAIN_FILE on Mac) to point at a test store.
- Tools and tests never use the user's real data: the headless scenes already use temporary folders
  (TRADINGHUB_DATA_DIR, TRADINGHUB_STRATEGIES_DIR, TRADINGHUB_INDICATORS_DIR); keep it that way.
- Do not add confirmations that block clicks on the trading buttons; safety comes from risk limits.
- The interface is in English: keep new text in English too.

Do it in stages and at the end of each one build (dotnet build TradingHub.slnx), run the tests
(dotnet test tests/TradingHub.Tests) and tell me what you changed:
1. Make it build and start on Windows (Program.cs without the macOS splash wait, paths, fonts).
2. The Windows key store behind the same interface as the Keychain (Connections.cs).
3. Integrations: SleepGuard with SetThreadExecutionState, system tray (TrayIcon + GetCursorPos to position the
   panel), opening links/folders/reports with UseShellExecute, sounds without afplay, and Claude Code (find claude.exe
   or claude.cmd with `where`, %USERPROFILE%\.local\bin and %APPDATA%\npm; open /login in cmd or Windows Terminal).
4. Interface: title bar with the Windows buttons on the right, Ctrl shortcuts (one shared helper instead of
   KeyModifiers.Meta) and per-platform wording (Mac/PC, Keychain/Credential Manager, menu bar/tray).
5. A build-windows.ps1 that publishes self-contained for win-x64 and win-arm64 with the icon, and leaves the app ready
   in a dist\Trading Hub folder. If you can, an optional Inno Setup script.
6. Final check: unit tests, the relevant headless scenes
   (dotnet run --project tools/TradingHub.UiSnapshot -- captures 4 trading brokers panel footprint market)
   and a list of what is worth testing by hand with the simulated account.

If something cannot work the same way as on Mac (for example, keeping going with the lid closed), hide that option on
Windows and explain it in the Settings text. Show the real errors if something fails; do not carry on as if it had
worked.
```

## Afterwards

- If you want to share the result, open a *pull request* on the repository with the changes staged.
- Keep the tests green on both platforms: a change on one should not break the other.
- Linux follows the same path (Secret Service/libsecret for the keys, `systemd-inhibit` for sleep, `xdg-open` for
  links).
