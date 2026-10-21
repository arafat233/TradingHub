[← Back to the index](README.md)

# Privacy and security

Trading Hub runs entirely on your Mac: the app, your data, your strategies, the backtests and your connections' keys. It
has no servers of its own, it doesn't ask you to create an account and it sends no telemetry or usage statistics. It only
connects to what you configure, and only when you use it.

## What stays on your Mac

| Place | What is there |
|---|---|
| `~/Library/Application Support/Trading Hub` | Settings, connections (without secrets), workspace, tick-by-tick history, candles, drawings, templates, account history, backtests, live strategies and their portfolios |
| `~/Documents/Trading Hub` | Your strategies (with their reports in `Strategies/Reports`) and your indicators |
| macOS Keychain | Passwords, secrets, tokens and the Databento API key, under the name «Trading Hub» |

The detail of each file is in [Installation](installation.md#where-your-data-is-stored). Whatever has to do with Claude
also stays in the data folder: the conversations (`Chats`), the chart images the lab looked at (`Lab captures`, the last
60) and the lab's configuration (`claude-lab-mcp.json`). Unexpected errors are recorded in `crash.log`, which never
leaves your Mac.

## Your keys

- Passwords, API key secrets, tokens and the Databento API key go to the **macOS Keychain**, never to files. Trading Hub
  reads and writes them with the macOS `security` tool.
- `connections.json` holds only non-secret details: name, user, server, the public part of the API key (the Binance API
  key or the Alpaca Key ID) and the options. See [Connections](connections.md#where-your-keys-end-up).
- Deleting a connection also deletes its secrets from the Keychain.
- The keys are not in files Claude can read, and no lab tool reads them.

## What connects to the internet and when

| What | When | With whom |
|---|---|---|
| Market data | When you open a chart or an instrument from an enabled connection | That connection's exchange or broker (Binance, Bybit, Rithmic, Tradovate, OANDA, Alpaca, cTrader) |
| History | When you download days in Data, or Claude with `download_data` | The public Binance and Bybit files, or your connection's server |
| Exchange candle history | To load long history on a chart and for the market context of days with no stored ticks | The open market's exchange; in crypto, the public Binance or Bybit candles, without opening any session |
| Orders | When you or a strategy trade on a broker account | That broker. The simulated account sends nothing outside |
| Databento | When you test the connection (it queries the available range and the cost of a day of NQ), when you click **Compute cost** and on **Download**, which shows you the cost and asks you to confirm first | Databento, with your API key |
| Claude Code | When you use the assistant, the lab or the risk agent | Anthropic, with your account, through the Claude Code CLI |
| Interactive Brokers | Only when you test the connection | Your TWS or IB Gateway (by default, on your own Mac) |
| Links | Only if you click them (each service's sign-up pages, the cTrader authorization, the author credit) | They open in your browser |

Nothing else: there is no update check and no error reporting. Building the app does need internet and the NuGet
packages, but that happens when you install it, not when you use it.

## Claude

Whatever Claude does goes through your Claude Code and your account: what you write to it, the files it reads (your
strategies and indicators) and what the tools return to it (metrics, trades, data summaries, chart images and, if you
ask for it, the state of your live book) travel to Anthropic. Trading Hub launches it inside the strategies folder, with
permission to edit only there, in the indicators folder and in the captures one, with file tools (no terminal, no web)
and without your other MCP servers. The risk agent doesn't even have tools: it receives the portfolio's numbers and
returns a proposal. What Claude Code does with that data depends on your account and on Anthropic's policies. See
[Claude](claude-assistant.md).

## Real money

- Trading Hub starts with the **simulated account**. Real orders are off on every connection until you enable «Allow
  real orders» (see [Connections](connections.md#real-accounts-and-test-accounts)).
- Manual orders to real accounts ask for confirmation if you left it that way (see [Trading](trading.md#safety)), and
  launching or manually resuming a strategy on a real account is confirmed once. Automatic resume on open does not ask:
  that is why, for real accounts, it ships off.
- Live strategies have a size, a maximum daily loss and a maximum drawdown, and **Stop everything** halts them all
  (see [Live](live.md)). The risk agent never applies changes on its own if there is a real account in the portfolio.

## Permissions macOS may ask for

- **Administrator password**: only if you enable «Keep going with the lid closed» in Settings, so the Mac doesn't sleep
  with the lid closed while Trading Hub is open. It is undone on its own when it closes.
- **Controlling Terminal**: if you click «Sign in to Claude Code», Trading Hub opens Terminal with `claude /login`.
- The app is signed «ad hoc» when you build it on your Mac (see [Installation](installation.md#ad-hoc-signing)).

## The development tools don't touch your data

The tests and the headless scenes use temporary folders (`TRADINGHUB_DATA_DIR`, `TRADINGHUB_STRATEGIES_DIR`,
`TRADINGHUB_INDICATORS_DIR`) and a fake key file (`TRADINGHUB_KEYCHAIN_FILE`) instead of the Keychain. The scenes copy
your configuration and your history into a temporary folder, strip the API keys, turn off real orders and point Rithmic
at a fake server. See [Development](development.md).

## Deleting everything

1. Quit Trading Hub completely (⌘Q or «Quit Trading Hub» in the menu bar).
2. Delete `~/Library/Application Support/Trading Hub` and, if you want, `~/Documents/Trading Hub`.
3. In **Keychain Access**, search for «Trading Hub» and delete those passwords.
4. Delete the app from Applications (see [Installation](installation.md#uninstalling)).
