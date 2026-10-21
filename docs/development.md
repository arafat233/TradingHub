[← Back to the index](README.md)

# Development

For anyone who wants to touch the code. Trading Hub is C# on .NET 10, with Avalonia 11.3 for the interface and SkiaSharp
2.88 for drawing the charts. The interface text and the code comments are in English.

## The solution

`TradingHub.slnx` holds ten projects:

| Project | What it does |
|---|---|
| `src/TradingHub.Core` | Market and data: instruments, trades, candles, tick files, CSV import, history sources (the combined Rithmic and Databento one), the connections (Binance, Bybit, Rithmic, Tradovate, OANDA, Alpaca, cTrader, Databento, the Interactive Brokers test, the simulator and the replay) and the trading side (accounts, simulated account, stop and targets, history). No external packages. |
| `src/TradingHub.Sdk` | The indicator API, with the same shape as the ATAS one: `Indicator`, series, drawing, attributes and text. |
| `src/TradingHub.Charting` | The Skia chart engine: model, candles, footprint, DOM, tape, drawings and themes. |
| `src/TradingHub.Scripting` | Compiles C# in memory with Roslyn (indicators and strategies) and adapts the ATAS namespaces. |
| `src/TradingHub.Strategies` | The strategy API, the backtest engine, the optimization, the smart walk-forward, the robustness and the validation, the portfolios and live execution with its accounting. It depends on Core only. |
| `src/TradingHub.Indicators.Native` | The included indicators. |
| `src/TradingHub.Indicators.Technical` | The home of the ported technical indicators; in the public version it only ships the editor types they use. |
| `src/TradingHub.App` | The app (`TradingHub` executable): `Views` (sections), `Windows` (windows), `Controls`, `Services` (connections and Keychain, Claude Code, Live, risk agent, market condition, menu bar, no-sleep mode…), `Lab` (MCP server and reports) and `Styles`. With `--mcp` it runs as the lab's server, with no windows. |
| `tests/TradingHub.Tests` | The tests, with xUnit. |
| `tools/TradingHub.UiSnapshot` | The app without a display, with scenes that verify and capture the interface. |

The Avalonia and SkiaSharp versions, the framework and the product version live in `Directory.Build.props`. The display
name comes from `TradingHub.Common.Product.Name` and the folders from `Product` and `AppPaths`.

## Building and running

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build TradingHub.slnx          # everything
dotnet test tests/TradingHub.Tests    # tests
./build-app.sh                        # builds dist/Trading Hub.app (ad hoc signed)
./build-app.sh --install              # and copies it to /Applications
```

`build-app.sh` publishes in Release for `osx-arm64`, self-contained, and assembles the bundle away from the Desktop
(iCloud adds attributes that break the signature). A `build.local.env` file, which is not uploaded, can define
`BUNDLE_ID` and `SIGN_IDENTITY`; `TH_OUT` and `TH_STAGE` change the output and staging folders. More in
[Installation](installation.md).

To run the app from source without touching your data or your Keychain:

```bash
TRADINGHUB_DATA_DIR=/tmp/th-dev TRADINGHUB_KEYCHAIN_FILE=/tmp/th-dev/keychain.json dotnet run --project src/TradingHub.App
```

With `TRADINGHUB_DATA_DIR`, the strategies and the indicators also go into subfolders of that folder, the menu-bar icon
does not appear and the no-sleep mode doesn't touch the Mac's power settings. Never run a second copy over the real data
with the installed app open: two processes writing the same files will corrupt them.

## Tests

`dotnet test tests/TradingHub.Tests` runs them all. They cover the backtest engine and its fills (`StrategyTests`),
stop, targets and OCO (`BracketTests`), the market context and the filter (`ContextTests`), the robustness and the
validation (`RobustnessTests`), the smart walk-forward (`SmartWalkForwardTests`), live execution (`LiveRunnerTests`,
`LivePortfolioTests`), portfolios and reports (`PortfolioTests`), the data (`CoreTests`, `DataIntegrityTests`,
`HistorySourcesTests`, `LongHistoryTests`, `CsvImportTests`), the accounts (`AccountTests`), the drawings and the legend
(`DrawingAndLegendTests`) and the connections (`RithmicTests`, `BrokerConnectionTests`, `DataSourceTests`,
`BinanceSignatureTests`).

The tests use temporary folders and fake servers with each service's real messages (`FakeRithmicServer`, and
`FakeHttpServer` with `FakeBrokers` for OANDA, Alpaca, Databento, cTrader and Tradovate): they never sign in to a real
account. To run one group: `dotnet test tests/TradingHub.Tests --filter FullyQualifiedName~SmartWalkForward`.

## Headless scenes

```bash
dotnet run --project tools/TradingHub.UiSnapshot -- <folder> 4 <scene> [<scene>…] [--open connection:SYMBOL] [--size 1600x950]
```

`<folder>` is where it stores the PNG captures and `4`, the seconds it waits for the app to load before the scenes
(with no scenes, it captures Trading). The ones with checks print `OK` or `FAIL` for each one, and the process exits with
the number of failures as its exit code.

| Scene | What it does |
|---|---|
| `trading`, `accounts`, `strategies`, `live`, `data`, `connections`, `settings` | Opens that section and captures it |
| `brokers` | Tradovate, cTrader, OANDA, Alpaca and Databento against fake servers: setup, test, download in Data, Databento cost, Tradovate accounts and OANDA live prices |
| `panel` | Trading's right panel (Chart Trader, DOM and tape), the DOM buttons and the separate windows |
| `footprint` | The footprint ladders with their own colors and the footprint editor |
| `market` | The backtest by market regime and the run filtered by condition |
| `theme` | Dark and light mode, with the chart settings |
| `choice`, `picker`, `corners` | The history button on the bar, the instrument picker and a rounded-corner diagnostic |
| `data+rithmic`, `data+import` | Data with Rithmic (fake server) and importing a test CSV |
| `accounts+demo` | The Account section with orders on the simulated one and on fake Rithmic |
| `connections+rithmic`, `connections+amp` | The Rithmic or AMP profile with its server list |
| `trading+…` | `layout2`, `layout2r`, `layout3`, `layout4`, `layout6`, `history<hours>[+period]`, `historyall`, `jitter`, `float`, `brackets`, `drawings`, `drawsettings`, `links`, `toolbar`, `trader`, `accounts`, `crossdemo`, `strategy`, `domfloat`, `domsettings` |
| `strategies+…` | `backtest`, `optimize` and `wfo` (they run and capture), `code`, `history`, `reports`, `portfolio`, `claudefloat`, `claude`, `claudelab`; with `+File` at the end it picks that strategy |

How they stay isolated from your data:

- It sets `TRADINGHUB_SLEEP_GUARD=off` and a temporary `TRADINGHUB_KEYCHAIN_FILE` with fake secrets: it never reads the
  Keychain.
- It copies your strategies and indicators into temporary folders (`TRADINGHUB_STRATEGIES_DIR`,
  `TRADINGHUB_INDICATORS_DIR`).
- It uses a temporary data folder (`TRADINGHUB_DATA_DIR`) with copies of `settings.json`, `workspace.json`,
  `connections.json` and of whatever was downloaded (`Ticks`, `Klines`, `Drawings`, except Rithmic), never the
  originals. It strips the API keys from the connections, turns off real orders and points Rithmic at a fake server with
  the user `demo`; the account history starts empty.
- Two don't run at once: a lock in the temporary folder makes the next one wait (up to 20 minutes).
- If you already defined any of those variables, yours is respected.

Careful: `strategies+claude` and `strategies+claudelab` send a real request to your Claude Code (they use up your
limit), unless `TRADINGHUB_CLAUDE_CLI` points at another executable. For diagnostics:
`TRADINGHUB_SNAP_INDICATORS="Name|Other"` adds those indicators to the active chart, `TRADINGHUB_SCRIPTS_DEBUG=1` shows
how your own indicators compiled and `TRADINGHUB_SNAP_DEBUG=1`, the connection's status.

## Environment variables

| Variable | What it changes |
|---|---|
| `TRADINGHUB_DATA_DIR` | The data folder (instead of `~/Library/Application Support/Trading Hub`) |
| `TRADINGHUB_STRATEGIES_DIR`, `TRADINGHUB_INDICATORS_DIR` | The strategies and indicators folders |
| `TRADINGHUB_KEYCHAIN_FILE` | A JSON with the secrets, instead of the Keychain |
| `TRADINGHUB_BACKTESTS_DIR`, `TRADINGHUB_LIVE_DIR` | The backtest history and the live book |
| `TRADINGHUB_SLEEP_GUARD=off` | It doesn't touch the Mac's power settings |
| `TRADINGHUB_CLAUDE_CLI` | The path to the Claude Code CLI |
| `TRADINGHUB_MCP_COMMAND` | The executable that starts the lab (by default, the app itself) |
| `TRADINGHUB_OANDA_REST`, `TRADINGHUB_OANDA_STREAM`, `TRADINGHUB_ALPACA_DATA`, `TRADINGHUB_ALPACA_TRADING`, `TRADINGHUB_ALPACA_STREAM`, `TRADINGHUB_TRADOVATE_REST`, `TRADINGHUB_TRADOVATE_WS`, `TRADINGHUB_TRADOVATE_MD`, `TRADINGHUB_CTRADER_URL`, `TRADINGHUB_DATABENTO_URL` | They point each connection at another server (the scenes with fake servers use them) |

## Adding a connection

1. In `src/TradingHub.Core/Feeds/<Name>/`, a class that implements `IMarketDataFeed`: `GetInstrumentsAsync`,
   `LoadTradesAsync` (history in batches) and `Subscribe`, which hands trades, depth and status to an
   `IMarketDataSink`. Depending on what the service provides, add `IKlineHistory` (candles for long history),
   `IDayDownloader` (downloading complete days in Data), `ISessionAwareFeed` (markets that close) or
   `ITickerStatsProvider` (price and 24-hour change in the picker).
2. If it can be traded, an account that implements `ITradingAccount` (market, limit, stop, cancel, modify, close,
   reverse, position and `IsReal`).
3. In `src/TradingHub.App/Services/Connections.cs`: a new value in `ConnectionKind`, its `MarketDataId`, the non-secret
   details in `ConnectionProfile`, the feed in `ConnectionManager.GetFeed` and the test in `TestAsync`. The secrets, with
   `SetSecret` and `GetSecret` (they go to the Keychain). Add a `TRADINGHUB_…` variable to change the server address in
   the tests.
4. The form in `src/TradingHub.App/Views/ConnectionsView.cs` and, if it trades, its accounts in
   `AppServices.AccountsFor` and `AccountFor`: the real ones only with «Allow real orders».
5. A fake server in `tests/TradingHub.Tests` (you can start from `FakeHttpServer`) with its tests.

## Adding an indicator or a lab tool

- **Included indicator**: a class in `src/TradingHub.Indicators.Native` that inherits from `Indicator`, with a
  parameterless constructor, `[DisplayName]` and `[Category(IndicatorCategories.…)]`. The app scans that assembly at
  startup. For one of your own you don't need to touch the code: it goes in the indicators folder (see
  [Indicators](indicators.md)).
- **Lab tool**: its schema in `LabServer.Tools()` and its case in `LabServer.CallAsync`
  (`src/TradingHub.App/Lab/LabServer.cs`), and its name in `ClaudeCodeBridge.LabTools`
  (`src/TradingHub.App/Services/ClaudeCode.cs`), which is the list of allowed tools.

## The validation from code

`ValidationGate.Evaluate` (in `TradingHub.Strategies`) applies the [Backtesting](backtesting.md) rules to a
walk-forward. `tests/TradingHub.Tests/RobustnessTests.cs` has examples.

```csharp
var gate = ValidationGate.Evaluate(new GateInput
{
    WalkForward = walkForward,   // the result of Optimizer.WalkForward
    Optimization = optimization, // optional: for the plateau and the robustness
    Trials = 12,                 // configurations tried, for the deflated Sharpe
    TrialVariance = 0.01,        // variance of their per-trade Sharpes
    MinTrades = 30,
});
Console.WriteLine($"{gate.StatusText} · deflated Sharpe {gate.Deflated:P0}");
foreach (var c in gate.Checks) Console.WriteLine($"{c.Status} · {c.Name}: {c.Text}");
```

`LiveStrategyRunner.Guard` also accepts an `ILiveOrderGuard`, a limiter that reviews every order that opens or grows a
position before sending it (the ones that shrink always go through); the app doesn't ship one, but the tests show how it
is used.

## Rules

- **Never write passwords, tokens or keys into repository files.** The app stores them in the Keychain (`Keychain` in
  `Connections.cs`); the test tools use `TRADINGHUB_KEYCHAIN_FILE` with fake data.
- **Never run tools against the real data** (the data folder and `~/Documents/Trading Hub`): use the variables above.
  Two processes writing the same files will corrupt them.
- **No confirmations that block the trading buttons.** Safety comes from the risk limits (size, daily loss, drawdown,
  Stop everything) and from having to enable real money on each connection, not from obstacles between the click and the
  order. The trading buttons must not move either: the `trading+jitter` scene measures whether the bar changes height or
  the buy button shifts when the day's change updates.
- Always test with the simulated account, and with fake servers when you touch a connection.
- The interface text goes in English.
