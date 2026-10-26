using System.Net.Http;
using System.Net.Http.Json;

namespace TradingHub.App.Services;

/// <summary>
/// Sends the indicator alerts to the user's own Telegram bot, so they reach the phone. Off unless the user turns it on and
/// gives a bot token (kept in the system credential store, never in a file) and a chat id. Nothing is sent otherwise.
/// </summary>
public static class TelegramNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string TokenAccount = "telegram:token";

    public static bool HasToken => Keychain.Get(TokenAccount).Length > 0;

    public static void SetToken(string token) => Keychain.Set(TokenAccount, token.Trim().Length == 0 ? null : token.Trim());

    /// <summary>Sends an alert if phone alerts are on and set up. Failures are only logged: an alert must never break the chart.</summary>
    public static void Notify(AppSettings settings, string text)
    {
        if (!settings.TelegramAlerts || settings.TelegramChatId.Trim().Length == 0) return;
        _ = Task.Run(async () =>
        {
            var (ok, error) = await SendAsync(settings.TelegramChatId, text);
            if (!ok) CrashLog.Write(new InvalidOperationException("Telegram alert not sent: " + error));
        });
    }

    public static async Task<(bool Ok, string Error)> SendAsync(string chatId, string text)
    {
        try
        {
            var token = Keychain.Get(TokenAccount);
            if (token.Length == 0) return (false, "no bot token saved");
            using var resp = await Http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", new { chat_id = chatId.Trim(), text });
            return resp.IsSuccessStatusCode ? (true, "") : (false, $"Telegram answered {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            // the message of an HttpRequestException can contain the request URL, which holds the token: say only what kind of failure it was
            return (false, ex.GetType().Name);
        }
    }
}
