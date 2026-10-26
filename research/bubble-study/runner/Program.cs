using TradingHub.Market;
using TradingHub.Strategies;

// Runs BubbleStudy over recorded ticks without opening the app, so a study can be re-run from a terminal.
//   dotnet run --project research/bubble-study/runner -- <feed> <symbol> <tickSize> <from> <to> [rowTicks]
//   dotnet run --project research/bubble-study/runner -- bybit-linear BTCUSDT 0.1 2026-10-19 2026-10-26 40

string feed = Arg(0, "bybit-linear");
string symbol = Arg(1, "BTCUSDT");
decimal tickSize = decimal.Parse(Arg(2, "0.1"));
var from = DateTime.SpecifyKind(DateTime.Parse(Arg(3, "2026-10-19")), DateTimeKind.Utc);
var to = DateTime.SpecifyKind(DateTime.Parse(Arg(4, "2026-10-26")), DateTimeKind.Utc);
int rowTicks = int.Parse(Arg(5, "1"));   // 1 = native rows; BTC needs ~40 ($4) to be a readable ladder

// Trading Hub keeps the tape under Application Support; the loader adds feed and symbol itself
string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "Trading Hub", "Ticks");

var instrument = new Instrument { FeedId = feed, Symbol = symbol, TickSize = tickSize, PointValue = 1m };
Console.WriteLine($"loading {symbol} from {root}");

var data = TickData.Load(root, instrument, from, to, null, default);
Console.WriteLine($"ticks: {data.Count:N0}   {data.FirstTime:u} -> {data.LastTime:u}");
if (data.Count == 0) { Console.Error.WriteLine("no ticks for that feed/symbol/range"); return 1; }

var strategy = new BubbleStudy { RowTicks = rowTicks };
var settings = new BacktestSettings
{
    FeedId = feed,
    Symbol = symbol,
    FromUtc = from,
    ToUtc = to,
    Period = "M5",
    InitialCapital = 10_000m,
    CommissionPercent = 0m,   // it never trades; costs would only be noise in the log
    SlippageTicks = 0,
};

var result = new BacktestEngine(strategy, data, settings).Run();
Console.WriteLine($"bars: {result.Bars}  -> CSV in ~/Documents/Trading Hub/Studies");
return 0;

static string Arg(int i, string fallback) =>
    Environment.GetCommandLineArgs() is var a && a.Length > i + 1 ? a[i + 1] : fallback;
