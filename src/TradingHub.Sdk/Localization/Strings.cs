using System.Collections.Concurrent;

namespace TradingHub.Localization
{
    public static partial class Strings
    {
        private static readonly ConcurrentDictionary<string, string> Overrides = new();

        /// <summary>Allows replacing texts at run time (user translations).</summary>
        public static void Set(string key, string value) => Overrides[key] = value;

        public static string Get(string key, string fallback) => Overrides.TryGetValue(key, out var v) ? v : fallback;
    }
}
