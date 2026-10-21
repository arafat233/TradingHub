[← Back to the index](README.md)

# Claude in Trading Hub

Trading Hub uses **Claude Code**, the Claude CLI, with your own session and your Claude subscription: there are no API
keys and no separate accounts, and whatever it does counts against your Claude Code usage limit. Without Claude Code
the platform works just the same; you only lose the assistant, the lab and the risk agent.

| Where | What it does |
|---|---|
| **Backtesting → Code**, the Claude panel | Writes, explains and fixes strategies and indicators |
| The same panel with **Lab** | On top of that, runs backtests, optimizations, walk-forwards and Monte Carlo, and leaves a report with a verdict |
| **Live → Portfolios** | Each portfolio's risk agent (see [Live](live.md#the-risk-agent)) |
| **Live → Proposals** | «Ask Claude for analysis»: the lab reviews the live book and proposes sizes |

## How it finds Claude Code

Trading Hub looks for the `claude` executable in `/usr/local/bin`, `/opt/homebrew/bin`, `~/.claude/local`,
`~/.local/bin`, `~/.npm-global/bin`, in each folder of your `PATH` and in the copy that ships with the Claude desktop
app (`~/Library/Application Support/Claude/claude-code/…`). If it finds several, it uses the newest version. With the
`TRADINGHUB_CLAUDE_CLI` variable you can point it at another path. How to install it is covered in
[Installation](installation.md#claude-code-optional).

- The Claude panel shows «Claude Code *version* · your subscription» at the top, or warns you if it wasn't found.
- If Claude Code asks you to sign in, **Sign in to Claude Code** appears: it opens Terminal with `claude /login` so you
  can log in with your account. Then you repeat the request.
- If your version is too old for the model, the risk agent says so: update it (the Claude app updates itself, or use
  `claude update`).

## Model, effort and permissions

- The assistant and the lab use **Opus 5.5** (`claude-opus-5-5`) with **xhigh** effort («Extra»). If your Claude Code
  doesn't know the effort option, it is omitted. The risk agent starts the same way and is changed per portfolio (Opus
  5.5, Sonnet 5.5, Haiku 5.5 or Fable 5.1; Low, Medium, High, Extra or Maximum effort).
- It runs headless (`claude -p`), inside the strategies folder, with permission to edit files there, in the indicators
  folder and in the lab captures one. It only has file tools (`Read`, `Write`, `Edit`, `MultiEdit`, `Glob`, `Grep`): no
  terminal, no web.
- It does not load your other MCP servers (`--strict-mcp-config`): in lab mode, only Trading Hub's. It does not inherit
  `CLAUDE*` or `ANTHROPIC*` variables from another session either: it uses your normal sign-in.
- The risk agent runs more tightly: no tools, no configuration or hooks of yours when your version allows it, and
  without saving the session. It only receives the portfolio's numbers and returns a proposal.

## The strategy assistant

1. Open **Backtesting → Code**. The Claude panel is to the right of the editor; the window button pulls it into a
   separate window (and docks it back).
2. Write the request and click **Send** (⌘Enter). There are suggestions to get started: create a breakout of the first
   30 minutes' range, explain the open strategy, add a time filter, set an ATR stop…
3. While it works you see what it writes and what it touches («Reading», «Writing» or «Editing», with the file); the
   editor goes read-only and whatever changes in the open file appears live. **Stop** cuts it off.
4. When it finishes: if it created a new strategy, it opens on its own; Trading Hub compiles it and offers **Test in a
   backtest** or, with errors, **Ask it to fix them**. If it touched indicators, it compiles them and tells you whether
   they are already in «My indicators» or what is failing.

With each request Trading Hub hands it the open file, the indicators folder and the current compilation errors. Its
instructions carry the complete strategy and indicator APIs (those of [Strategies](strategies.md) and
[Indicators](indicators.md)) and these rules: C# code that compiles without external packages, no files, network,
threads or unseeded `Random`, short comments in English and, at the end, 2 to 5 lines of explanation.

The conversations are stored in `~/Library/Application Support/Trading Hub/Chats`. With the history button you pick one
back up where it left off (Claude carries on with everything it already knew); **New conversation** starts from
scratch.

## The lab

With **Lab** enabled, Claude Code receives Trading Hub's tools as an MCP server: the app's own executable launched with
`--mcp`, with no windows, speaking JSON-RPC over standard input and output. The configuration lives in
`~/Library/Application Support/Trading Hub/claude-lab-mcp.json` (server `tradinghub`). Only these tools are enabled, and
each one can take up to an hour (a long optimization):

| Tool | What it does |
|---|---|
| `list_strategies` | Lists the strategies with their compilation, their parameters (with the `[Optimize]` ranges) and their warm-up: the `OnInit` indicators and the candles they take to be ready |
| `compile_strategy` | Compiles a strategy and returns the errors (line and message), the parameters and the warm-up |
| `chart_snapshot` | Runs the strategy over a range and saves an image of the chart with its entries, exits and drawings, plus the candles, trades and log in local time |
| `list_indicators` · `compile_indicator` | Lists your own indicators and compiles one, with its errors |
| `list_data` | The tick-by-tick data stored per instrument (the only data the backtests use) |
| `download_data` | Downloads complete days from the public Binance Futures, Binance Spot and Bybit files (up to 62 days per request) |
| `run_backtest` | Tick-by-tick backtest with commissions and slippage; optionally with Monte Carlo |
| `run_optimization` | Grid optimization (up to 5,000 combinations by default); it returns the best ones, the % of combinations that win and the one recommended by plateau |
| `run_walk_forward` | Smart walk-forward (grid or Bayesian, plateau, PBO and CPCV paths); it can also just estimate how long it will take |
| `list_runs` | A strategy's stored runs |
| `analyze_portfolio` · `suggest_portfolio` | Combines runs into a portfolio with its correlation, or looks for the lightly correlated combination that improves the Sharpe the most |
| `live_portfolio` | The live book, read only: each strategy with its size, position, result, profit factor and drawdown |
| `propose_sizing` | Leaves pending size proposals; it applies nothing |
| `save_report` | Builds and stores the professional report |

Everything it runs ends up in the strategy's history with the note «Claude's lab», exactly as if you had run it. Its
instructions set out a method: review the strategy and the data (and download 30 to 90 days if it is crypto), a base
backtest, an optimization with reasonable ranges and at least 30 trades per combination, a walk-forward with the
optimization window 3 to 4 times the test one, Monte Carlo over whatever was validated, improving the code if it has
clear flaws (always comparing against the previous version), giving a verdict and finishing with `save_report`. It
cannot make numbers up: everything it writes has to come from a tool. The verdicts are:

| Verdict | When |
|---|---|
| **winner** | It wins out of sample, walk-forward efficiency ≥ 0.5, out-of-sample profit factor ≥ 1.2, at least 30 trades, a reasonable Monte Carlo p95 drawdown and stable windows |
| **promising** | Positive results but with a small sample or some instability |
| **loser** | It loses out of sample or only wins when overfitted |
| **discarded** | It doesn't compile, doesn't trade or the idea makes no sense with that data |

For the live book it uses `live_portfolio` and `propose_sizing`: more size (up to ×3) for the ones performing, less or a
pause for the ones degrading, always with a reason and never drawing conclusions from fewer than about 10 trades. The
proposals wait for your approval in **Live → Proposals**.

## Reports

`save_report` asks Trading Hub to build the report with every metric, table and chart from the runs (executive summary,
strategy, data and assumptions, performance, monthly, trade analysis, drawdowns, optimization with each parameter's
sensitivity, walk-forward, Monte Carlo, portfolio and glossary). Claude contributes the verdict, the summary, the
analysis, the risks and the next steps.

- They are stored in `~/Documents/Trading Hub/Strategies/Reports`: one `.md` per report and a `…_files` folder with the
  charts.
- They are viewed in **Backtesting → Reports**, as a sheet. **Export PDF / HTML** builds an HTML with the charts
  embedded and opens it in the browser, from where you print it or save it as a PDF; **Show in Finder** opens the
  folder.
- The verdict also appears next to each strategy in the strategy menu.
- Without Claude, **Report** in the History or in the Portfolio builds the same report, without the written analysis.

## What reaches Claude

What you write, the files it reads (your strategies and indicators) and what the tools return (metrics, trades, data
summaries, chart images and, if you ask for it, the state of the live book) travel to Anthropic through your Claude
Code, with your account. Your connections' keys are in the Keychain and no tool exposes them. More in
[Privacy](privacy.md).
