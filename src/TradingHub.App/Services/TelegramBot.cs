using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using TradingHub.Indicators.Native;

namespace TradingHub.App.Services;

/// <summary>
/// Answers a few read-only commands sent to the user's own Telegram bot from the user's own chat, so the platform can be checked from a
/// phone: /status, /levels SYMBOL, /today, /mute 30 (minutes, or 2h), /unmute, /help. Messages from any other chat are ignored. Off unless
/// phone alerts and "commands" are both on; it needs the app to be open. Nothing here can place or change an order.
/// </summary>
internal static class TelegramBot
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static long _offset;

    public static void Start(AppServices svc) => _ = Task.Run(async () =>
    {
        bool primed = false;
        while (true)
        {
            try
            {
                var s = svc.Settings;
                string token = Keychain.Get("telegram:token");
                if (!s.TelegramAlerts || !s.TelegramCommands || token.Length == 0 || s.TelegramChatId.Trim().Length == 0)
                {
                    primed = false;
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    continue;
                }

                // the first time, skip whatever was sent while the app was closed: old commands must not run now
                var url = $"https://api.telegram.org/bot{token}/getUpdates?timeout={(primed ? 25 : 0)}&offset={_offset}";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                foreach (var u in doc.RootElement.GetProperty("result").EnumerateArray())
                {
                    _offset = u.GetProperty("update_id").GetInt64() + 1;
                    if (!primed || !u.TryGetProperty("message", out var m)) continue;
                    if (m.GetProperty("chat").GetProperty("id").ToString() != s.TelegramChatId.Trim()) continue; // not the user's chat
                    var text = m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    string reply = await Dispatcher.UIThread.InvokeAsync(() => Answer(svc, text.Trim()));
                    var (ok, error) = await TelegramNotifier.SendAsync(s.TelegramChatId, reply.Length > 3800 ? reply[..3800] : reply);
                    if (!ok) CrashLog.Write(new InvalidOperationException("Telegram reply not sent: " + error));
                }

                primed = true;
            }
            catch (Exception ex)
            {
                // the exception text can hold the request address (which has the token): log only its kind
                CrashLog.Write(new InvalidOperationException("Telegram commands: " + ex.GetType().Name));
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
    });

    private static string Answer(AppServices svc, string text)
    {
        var parts = text.TrimStart('/').Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant().Split('@')[0] : "";
        string arg = parts.Length > 1 ? parts[1] : "";
        switch (cmd)
        {
            case "status":
            {
                var sb = new StringBuilder("Trading Hub\n");
                foreach (var d in svc.Hub.Items.Where(i => i.Instrument.FeedId != "sim").OrderBy(i => i.Instrument.Key))
                {
                    var ago = DateTime.UtcNow - d.LastDataAt;
                    var lat = svc.Latency.Of(d.Instrument.Key);
                    sb.AppendLine($"{d.Instrument.Symbol}: {d.Status}, last tick {(d.LastDataAt.Year < 2000 ? "never" : $"{ago.TotalSeconds:0} s ago")}, last price {d.LastPrice}{(lat is { } l ? $", delay {l.Median:0.0} s" : "")}");
                }

                sb.AppendLine(AlertControl.IsMuted ? $"Alerts muted until {AlertControl.MutedUntil:HH:mm}" : "Alerts on");
                return sb.ToString();
            }
            case "levels":
            {
                var d = svc.Hub.Items.Where(i => i.Instrument.FeedId != "sim")
                    .FirstOrDefault(i => arg.Length == 0 || string.Equals(i.Instrument.Symbol, arg, StringComparison.OrdinalIgnoreCase));
                if (d == null) return "No open instrument" + (arg.Length > 0 ? " called " + arg : "") + ". Open it on a chart first.";
                var rows = TodayReport.Load().FirstOrDefault(l => string.Equals(l.Instrument, d.Instrument.Symbol, StringComparison.OrdinalIgnoreCase)).Levels?.Where(r => !r.Touched && !r.Hidden).ToList();
                if (rows == null || rows.Count == 0) return $"{d.Instrument.Symbol}: no saved levels price has not come back to.";
                decimal last = d.LastPrice, tick = d.Instrument.TickSize > 0 ? d.Instrument.TickSize : 1;
                var sb = new StringBuilder($"{d.Instrument.Symbol} last {last}\n");
                foreach (var r in rows.Where(r => r.Price > last).OrderBy(r => r.Price).Take(3).Reverse()) sb.AppendLine($"above {(r.Buy ? "buy " : "sell")} {r.Volume:0.##} @ {r.Price} ({(r.Price - last) / tick:0} ticks)");
                foreach (var r in rows.Where(r => r.Price < last).OrderByDescending(r => r.Price).Take(3)) sb.AppendLine($"below {(r.Buy ? "buy " : "sell")} {r.Volume:0.##} @ {r.Price} ({(last - r.Price) / tick:0} ticks)");
                return sb.ToString();
            }
            case "today":
                return TodayReport.Build();
            case "mute":
            {
                double minutes = 60;
                string a = arg.Trim().ToLowerInvariant();
                if (a.Length > 0)
                {
                    bool hours = a.EndsWith('h');
                    if (!double.TryParse(a.TrimEnd('h', 'm'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out minutes)) return "Use /mute 30 (minutes) or /mute 2h.";
                    if (hours) minutes *= 60;
                }

                AlertControl.MutedUntil = DateTime.Now.AddMinutes(Math.Clamp(minutes, 1, 24 * 60));
                return $"Alerts muted until {AlertControl.MutedUntil:HH:mm} (sound, voice and phone; they still show in the app).";
            }
            case "unmute":
                AlertControl.MutedUntil = default;
                return "Alerts on again.";
            default:
                return "Commands: /status, /levels GC.F, /today, /mute 30 (or 2h), /unmute. They only read or mute; nothing here can place or change an order.";
        }
    }
}
