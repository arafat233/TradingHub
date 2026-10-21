namespace TradingHub.Common;

/// <summary>The app's name and its folders.</summary>
public static class Product
{
    public const string Name = "Trading Hub";

    /// <summary>The app's folder inside <paramref name="baseDir"/>.</summary>
    public static string Folder(string baseDir) => Path.Combine(baseDir, Name);

    /// <summary>App data (~/Library/Application Support/Trading Hub).</summary>
    public static string AppData => Folder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>The user's visible folder (~/Documents/Trading Hub): their own strategies and indicators.</summary>
    public static string Documents => Folder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
}
