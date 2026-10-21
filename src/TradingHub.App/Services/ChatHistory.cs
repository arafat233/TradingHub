using System.Text.Json;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services;

/// <summary>A stored message of a conversation with Claude.</summary>
public sealed class ChatMessage
{
    /// <summary>"user", "assistant" or "note" (app notices: errors, files touched).</summary>
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
}

/// <summary>Conversation with Claude: the messages and the session id to continue where it left off (--resume).</summary>
public sealed class ChatConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? SessionId { get; set; }
    public bool Lab { get; set; }

    /// <summary>Strategy file open when the chat started.</summary>
    public string? Strategy { get; set; }

    public List<ChatMessage> Messages { get; set; } = new();
}

/// <summary>History of conversations with Claude (one JSON per conversation in the data folder).</summary>
public static class ChatHistory
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Root => Path.Combine(AppPaths.Root, "Chats");

    public static IReadOnlyList<ChatConversation> List()
    {
        var res = new List<ChatConversation>();
        if (!Directory.Exists(Root)) return res;
        foreach (var f in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<ChatConversation>(File.ReadAllText(f), Json) is { } c) res.Add(c);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }
        }

        return res.OrderByDescending(c => c.UpdatedUtc).ToList();
    }

    public static void Save(ChatConversation c)
    {
        if (c.Messages.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Root);
            c.UpdatedUtc = DateTime.UtcNow;
            File.WriteAllText(Path.Combine(Root, c.Id + ".json"), JsonSerializer.Serialize(c, Json));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    public static void Delete(string id)
    {
        try
        {
            File.Delete(Path.Combine(Root, id + ".json"));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }
}
