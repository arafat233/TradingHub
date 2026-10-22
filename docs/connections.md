[← Back to the index](README.md)

# Connections

In **Connections** (⌘4) you configure where the market data comes from and which accounts you trade with. On the left
is the list of connections; on the right, the details of the one you picked.

In the list, each connection shows a summary («public data · no account», «data + testnet account», the Rithmic user and
system…) and a coloured dot: grey if it is disabled, green or red depending on the last test, and neutral if you
haven't tested it yet.

## Adding and testing a connection

1. Click **New connection**, below the list, and choose the type.
2. Fill in the details it asks for (they vary by type; they are listed below).
3. Click **Test connection**. It saves first and then tests; the result appears above the form.
4. If you change something, click **Save**.

Above the form you also find:

- the connection's **name** (you can change it) and its type;
- the **Enabled / Disabled** switch: a disabled connection does not appear in the instrument search and does not provide
  accounts to trade;
- **Delete**, which removes the connection and its secrets from the Keychain. The recorded data is left untouched. The
  Simulator, Binance, Bybit and Rithmic types can only be deleted if there is more than one of the same type; otherwise
  disable them.

Almost every type also has **History when opening a chart** (from 30 minutes to 7 days): how much history is requested
when you open an instrument. Whatever is already recorded or downloaded in [Data](data.md) loads instantly; the rest is
requested from the connection.

## Where your keys end up

Passwords, API key secrets, tokens and the Databento API key are stored in the **macOS Keychain**, never in files. In
**Keychain Access** they appear as passwords under the name «Trading Hub». Once saved, the field no longer shows the
value: it says «•••••••• (stored in the Keychain)». To replace it, type the new one and save; if you leave it empty, the
previous one stays.

In `connections.json`, inside the data folder, only non-secret details remain: the name, the user, the server, the API
key's public identifier (for example, the Binance API key or the Alpaca Key ID) and the options.

## What each type gives you

| Connection | What it gives | What you need | Orders |
|---|---|---|---|
| Simulator | Simulated market without internet | Nothing | Simulated account |
| Binance Futures / Binance Spot | Free crypto data and history | Nothing for data; API key and secret to trade | Testnet or real |
| Bybit | Data for the USDT perpetuals | Nothing | No (data only) |
| Rithmic | CME futures: data, history and orders | User, password, system and server | Practice or real |
| AMP Futures | Same as Rithmic | The Rithmic credentials AMP sends you | Practice or real |
| Tradovate | CME futures: trading accounts and data | User, password, CID and API key secret | Demo or real |
| cTrader | CFDs and forex from cTrader brokers | An Open API application and authorizing your account | No (data only) |
| OANDA | Index, gold and forex CFDs | An API token | No (data only) |
| Alpaca | US stocks and ETFs | API key ID and secret key | No (data only) |
| Databento | CME futures history since 2010 | API key (pay per use) | No (history only) |
| London Strategic Edge | Free stocks, ETFs, forex, crypto, indices: live ticks, 24 h of ticks, older history as candles | A free API key | No (data only) |
| Interactive Brokers | For now, only a connection test | TWS or IB Gateway with the API enabled | No |

Any connection with data can be traded with the **simulated account**.

## Simulator

A simulated market with a realistic book and trades, ideal for testing offline. It ships SIM-BTC, SIM-ETH, SIM-ES and
SIM-NQ. No configuration needed.

## Binance Futures and Binance Spot

Binance market data is public: with no account you already get charts, DOM, tape and history. To trade, the **Trading
account** section asks for:

1. Choosing **Testnet (test money)** or **Real account**. It defaults to testnet.
2. Pasting the **API key** and the **API secret**. Create the key with trading permission only, no withdrawals. The
   «How do I create an API key?» link opens the testnet page or your real account's page, depending on what you chose.
3. With a real account, ticking **Allow REAL orders from Trading Hub**. Without that box the account stays as «real
   (read only)» and does not show up for trading.
4. Optional: **Ask for confirmation before each real order** (ticked by default) and **Maximum quantity per order**
   (0 = no limit).

## Bybit

Public data for the USDT linear perpetuals. Trading with a Bybit account is not available yet.

## Rithmic

A direct connection over R|Protocol, Rithmic's official API: you don't need R|Trader Pro open. It ships disabled; enable
it once you enter your details.

- **User** and **Password** (the eye button shows what you type). Your broker or prop firm gives them to you.
- **Server**: when you open the section, Trading Hub queries the Rithmic servers and lists every system-and-region
  combination with its latency in milliseconds; the fastest ones show in green. The search box filters (paper, 01,
  Frankfurt, your prop firm's name…) and the reload button requests the list again. Colo 75 only works from the CME
  datacenter.
- **Use a server that isn't in the list**: to enter the address (`wss://…`) and the system by hand.
- **Aggregated quotes**: Rithmic merges book updates that arrive at the same instant. Below it tells you whether the
  chosen system offers them; if not, it connects without them.
- **Connection timeout (ms)**: 10,000 by default.
- **Advanced**: the **Application name** (some systems require an approved one) and the **Products for the search**
  (ES, NQ, MES, MNQ, CL…). For each product the front-month contract is looked up, for example ES → ESZ6.

Your Rithmic accounts appear in the Chart Trader on Rithmic charts and in Account once the connection is active and the
order session finishes connecting. Rithmic allows only one session per user: if you sign in with the same user from
another program, one of the two sessions is dropped.

## AMP Futures

AMP runs on Rithmic: they send you a Rithmic user and password. The connection is created disabled, with the Chicago
server and the «Rithmic Paper Trading» system (the demo). For the real account choose «Rithmic 01» on the nearest
server. The rest is the same as Rithmic.

## Tradovate

A direct connection to the official Tradovate API.

1. Choose **Demo (Tradovate's simulated)** or **Real account**. It defaults to demo.
2. Fill in your Tradovate **User** and **Password**.
3. Fill in the **CID** and the **Secret** of your API key. The «Where do I get the API key? (Settings → API Access)»
   link opens Tradovate.
4. Review the **Products for the search** (the front-month contract is used for each one).
5. To trade a real account, tick **Allow orders on real Tradovate accounts**. Optional: **Maximum quantity per order**.

Your accounts appear in the Chart Trader and in Account, and can be traded from the CME futures charts of either
Tradovate or Rithmic. Tradovate only provides market data over the API if your account has CME data for API enabled (a
separate license); without it you can still trade while watching another connection's charts. Tradovate allows only one
data session per user: if you open Tradovate Trader with the same user, it may drop the Trading Hub one.

## cTrader

Free with any broker that has cTrader and a demo account: index, forex, gold and oil CFDs, with tick-by-tick history.

1. **Your application (just once)**: create an application at openapi.ctrader.com (sign in with your cTrader ID) and
   add the redirect address the screen shows (`http://127.0.0.1:53682/ctrader`; the «Copy» button copies it). Then paste
   the **Client ID** and the **Secret** here.
2. **Authorize your account**: choose **Demo account** or **Real account** and click **Connect with cTrader**. The
   browser opens; you sign in with your cTrader ID and authorize. You have 5 minutes. You can also paste an access token
   (the one from your application's «Playground»).
3. **Account number**: if the token has several accounts, the login of the one you want to use (0 = the first).

Trading Hub stores the token in the Keychain and renews it on its own. CFDs have no real trades: each quote (bid) counts
as a trade with volume 1, so the footprint and the delta are approximate.

## OANDA

Free with a practice account: index CFDs (NAS100_USD, SPX500_USD, US30_USD, DE30_EUR…), gold, oil and forex, with years
of history in 5-second candles.

- **Practice account (free)** or **Real account**.
- **Token**: you generate it in «Manage API Access» (there are links to create the account and the token).
- **Account**: the 101-001-… number; empty uses the first one on the token.

Trading Hub uses the token for data only; it does not send orders to OANDA. As with cTrader, each quote (mid price)
counts as a trade with volume 1.

## Alpaca

With the practice account (free, from any country) you get tick-by-tick history for every US exchange since 2016,
except the last 15 minutes. It is useful for studying the indices through SPY, QQQ, IWM and DIA.

- **API key ID** and **Secret key** from your practice account.
- **Live data**: IEX (free, one exchange) or SIP (all exchanges, paid plan).

The keys are used for data only; no orders are sent to Alpaca.

## Databento

Tick-by-tick history for CME futures (NQ, ES, MNQ, MES, CL, GC…) since 2010, with each trade's real aggressor side. It
is pay per use; new accounts come with free credits.

- It only asks for the **API key** (`db-…`). There are links to create the account and see the prices.
- **Test connection** shows the first and last date with data and how much a day of NQ tick by tick costs. It downloads
  nothing.
- It does not provide live data and does not appear in the instrument search: it is used from **Data → CME futures with
  Databento**. See [Data](data.md#databento).

## London Strategic Edge

A free market data service ([londonstrategicedge.com/data](https://londonstrategicedge.com/data)) with stocks, ETFs, forex, crypto,
commodities, indices and futures. It only asks for a free **API key**, stored in the Keychain (Windows Credential Manager on Windows).

- **Live**: ticks over a websocket (one key streams up to 16 symbols).
- **History**: the last 24 hours as real ticks (the service replays them), and anything older as candles (US stocks back to 2003,
  forex to 2009, crypto to 2017), the way Binance's long history works. The free plan allows 10 downloads an hour.
- **What it does not give**: the order book (no heatmap, recorded book or icebergs on this connection), the aggressor side of each
  trade (it is inferred with the tick rule, because the service's "bid" is just the trade price, so the footprint, delta and CVD are estimates),
  a trade id, and the volume of some markets (each tick of those counts as 1).
- **What it carries** (checked with a free key): about 4,200 symbols: US and international stocks, ETFs, 62 forex pairs, 58 cryptos,
  commodities, 19 indices (NAS100/USD, SPX500/USD…) and 68 futures. `ES.F`, `NQ.F` and `GC.F` (S&P 500, Nasdaq 100 and gold futures)
  stream live and have candles back to 2016 and ticks since September 2025; the server may call the symbol by an alias (ES.F ticks say
  SP500.F), which is fine. Some Eurex futures are history only (the picker says so).
- Tick sizes are not in the service's catalog, so they are a sensible one per class (forex 0.00001, JPY pairs 0.001, crypto 0.0001, the
  rest 0.01), with the real ones for ES, NQ, GC, SI and a few Eurex futures. The service's "bid" is always the trade price, so no quote
  or book is shown.
- **One websocket at a time**: the free plan allows a single websocket per key («Max 1 concurrent websocket(s) for this API key»). Trading Hub
  shares that one connection between the live chart and the history replay (the live chart steps aside for a second while the history
  loads, then reconnects by itself), so one London Strategic Edge chart works fully. A **second chart** (or another program using the key, for
  example the service's own Python client) has to wait: it shows «the free plan allows only 1 websocket per key at a time» and keeps retrying
  until the first one is closed.
- **Test connection** checks the key against the service's usage endpoint and uses none of your allowance.

## Interactive Brokers

Open TWS or IB Gateway and enable the API (Configure → API → Settings). Fill in **Host**, **Port** (the usual ones: 7497
TWS paper, 7496 TWS real, 4002 Gateway paper) and **Client ID**. For now Trading Hub only verifies the connection: it
provides neither data nor orders.

## Real accounts and test accounts

Every trading account appears in the Chart Trader's **Account** picker. The **Simulated account** is always there. The
rest depend on the connection:

| Connection | Test account | Real account |
|---|---|---|
| Binance | Testnet: trades directly | With «Allow REAL orders from Trading Hub» |
| Rithmic and AMP | «Rithmic Paper Trading» and «Rithmic Test» systems: trade directly | Any other system (Rithmic 01, prop firms…), with «Allow orders on real accounts» |
| Tradovate | Demo: trades directly | With «Allow orders on real Tradovate accounts» |

Without that authorization, a real account does not appear in the Chart Trader picker. The Rithmic and Tradovate ones
stay **read only** in the Account section: you see the balance, the positions and the history, but you cannot send them
orders. Real accounts are marked with «— REAL» in the picker and with the «REAL ACCOUNT» warning.

### Enabling real money

1. Test everything first with the simulated account or the broker's test account.
2. In the connection, switch to the real account: on Binance, with the **Testnet / Real account** toggle; on Tradovate,
   with **Demo / Real account**; on Rithmic and AMP, by choosing the real system in **Server**.
3. Tick that connection's real-orders box.
4. Set a **Maximum quantity per order**: Trading Hub blocks any larger order.
5. Save and test the connection.

How real orders are confirmed and what limits exist is covered in [Trading](trading.md#safety).
