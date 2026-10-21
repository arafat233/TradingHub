[← Back to the index](README.md)

# Data

The **Data** section (⌘3) shows the history stored on your Mac. That history is used when opening charts (it loads
instantly), in the replay and in the backtests.

The screen has three parts: on the left, **Stored** (the instruments you have); in the middle, the **days** of the
chosen instrument; on the right, **Download history**, **CME futures with Databento** and **Import files**. At the top
you see the total («N instruments · size»), a button to refresh the list and **Open folder**.

![The Data section: what is stored, an instrument's days and the cards for downloading history and Databento](img/data.png)

## Where the history comes from

- **Whatever you watch live gets recorded automatically**, Rithmic included. It is controlled in **Settings → Data →
  Record incoming live data** (on by default).
- **Downloads** of complete days, from public files or from your connections' APIs.
- **Imported files** (CSV or TXT) for CFDs, forex and other markets.
- **Databento**, for years of CME futures.

Everything is stored as trades, in one file per connection, instrument and day (UTC).

## What is stored

In **Stored** each instrument shows its connection, how many days it has and how much space it takes. When you pick
one, its days appear in the middle, newest to oldest, with a bar proportional to the size (roughly, the number of
trades) and how much space each day takes. At the top you see the full range («dd/MM/yyyy → dd/MM/yyyy») and the total
size.

To select several days, use ⌘ or ⇧ while clicking.

### Deleting days

1. Pick the instrument in **Stored**.
2. Select one or more days.
3. Click **Delete** and confirm. Those days of that instrument are removed and it cannot be undone.

## Replay

The replay plays back stored days as if they were live and lets you trade with the simulated account.

1. Pick the instrument and a day. If you pick several, it plays from the first to the last.
2. Click **Play** (or double-click a day).
3. A new tab opens in Trading, marked with ▶, which starts at the first trade of the first chosen day and finishes at
   the end of the last one. It uses the active chart's period and copies its indicators; the history before the start is
   loaded from the same files.

The replay bar appears at the bottom:

| Control | What it does |
|---|---|
| Play / pause | Starts or stops the playback. |
| 1× to 300× | Speed: 1×, 2×, 5×, 10×, 30×, 60× or 300× real time. |
| Clock and progress | The instant being played back; «Finished» once it reaches the end. |
| +5 min, +30 min, +1 h | Jump forward. |

Drawings you make in a replay are not saved, and the replay is not kept in the workspace. The trades you make do go
into the simulated account's history.

## Download history

It downloads complete days of an instrument.

1. In **Instrument**, pick the instrument (if the active chart can be downloaded, it is already proposed).
2. In **How much (UTC days)**, click a shortcut (Yesterday, 7 days, 30 days, 90 days, 6 months, 1 year or 2 years) or
   change **From** and **To**. Today is not downloaded because it hasn't finished yet.
3. Read the summary below the buttons: how many days there are and where they come from.
4. Click **Download**. You see the day being downloaded, the progress and how much is left. **Cancel** stops the
   download.

| Source | What gets downloaded |
|---|---|
| Binance Futures and Spot | Binance's public daily files, no account. |
| Bybit | Bybit's public daily files, no account. |
| Rithmic | Tick by tick, with whichever contract was trading each day (for example ESU6 and then ESZ6). Rithmic only keeps data since the last expiry: it does not provide history for expired contracts. |
| Tradovate | Tradovate ticks (CME data enabled for API is required). |
| cTrader | The broker's (bid) ticks over the Open API. |
| OANDA | 5-second candles converted into trades (CFDs have no real trades). |
| Alpaca | Trades from every US exchange (SIP), free since 2016. |

- Days that are already complete are not downloaded again: if you cancel, next time it picks up where it left off.
- Over the APIs (Rithmic, Tradovate, cTrader, OANDA and Alpaca) Saturdays are not requested, and the downloaded day
  replaces whatever had been recorded live.
- An index like ES has more than a million trades a day: a long range takes a while.
- If a day brings nothing back, the final summary counts it separately (holidays, weekends or files not published yet).
  If Rithmic does not return an expired contract, it says so too.
- Connections that don't provide history to download record their data while you watch them live.

## Import files

For CFDs (US100, US500, DAX), forex, gold and other markets you don't have through a connection. You download the CSV
from Dukascopy or HistData, or export it from MetaTrader or TradingView, and Trading Hub turns it into history for
charts, replay and backtests. The format is recognized automatically: ticks with bid and ask, candles, or any CSV with a
date and a price or OHLC.

1. Download the files. The **Dukascopy** and **HistData** buttons open their download pages.
2. In **Instrument name**, type the symbol or leave it empty so it is inferred from the file name.
3. In **File time**, choose the time zone it comes in:

   | Option | For |
   |---|---|
   | UTC | Dukascopy and TradingView |
   | Fixed New York, −5 | HistData |
   | UTC+2 | MetaTrader in winter |
   | UTC+3 | MetaTrader in summer |

4. Click **Import files…** and choose one or more CSV or TXT files.
5. When it finishes, the summary says how many trades and days were imported, the range and the detected price step.
   The instrument appears in **Stored** with the «imported» connection and in the backtests.

The name is inferred like this: USATECH, NAS100, NSX, US100, USTEC or NDX → US100; USA500, SPX or US500 → US500; USA30,
US30, DJI or WS30 → US30; DEU, GER or DAX → GER40; XAU or GOLD → XAUUSD; and the pairs EURUSD, GBPUSD, USDJPY, AUDUSD,
USDCAD, USDCHF, NZDUSD, XAGUSD and BTCUSD by their name. If nothing is recognized, it uses the first word of the file.

CFDs have no real volume: each quote counts as a trade (tick volume) and the side comes from whether the price went up
or down. It is good for price, candles and price-based backtests; the footprint and the delta are approximate. Candles
are converted into synthetic trades.

## Databento

Databento sells tick-by-tick history for CME futures since 2010, with each trade's real aggressor side: exactly what
Rithmic does not keep. It is pay per use (new accounts come with free credits), so Trading Hub always computes the cost
first and only downloads if you confirm.

First, add the **Databento** connection with your API key in [Connections](connections.md#databento). The card's
**Connections** button takes you there.

1. In **Continuous contract**, pick the market: NQ, ES, MNQ, MES, YM, RTY, CL, GC, MCL, MGC, ZN or 6E. The
   volume-based continuous contract is used, which rolls like the charts do.
2. Pick **From** and **To** (the last three months out of the gate).
3. Decide whether to tick **Also download the days Rithmic already has**:
   - **unticked** (cheaper): the days Rithmic already has in full are skipped, and Databento only fills in the missing
     ones;
   - **ticked**: it downloads the whole range, so you have the complete Databento history.
4. Click **Compute cost**. It tells you how many days are left to download, the range and the approximate cost in
   dollars of your credits.
5. Click **Download** and confirm in the «Download Databento history» window. If the credits aren't enough, Databento
   charges the difference to your payment method at Databento.

**Download** is only enabled after computing the cost. If you change the market, the dates or the box, it has to be
computed again. Databento days you already have are not downloaded or charged again, Saturdays are skipped and the
range goes up to yesterday. Days with no trades (holidays) are counted separately. **Cancel** stops the download and
whatever was downloaded is kept.

The same card shows what you have already downloaded («Already from Databento: …», with each market's days and range).

### It is stored separately

The Databento data stays separate, as «Databento» in the **Stored** list, without mixing with what Rithmic recorded or
downloaded. In **Backtesting**, under «Instrument (stored data)», a future with history from both sources appears three
times (each option ends with the number of days):

| Option | What it uses |
|---|---|
| NQ · Rithmic + Databento | Both combined: Rithmic leads, and Databento fills in the minutes where Rithmic has no trade at all (for example, the hours the app was closed and recorded nothing). |
| NQ · Rithmic only | Only the Rithmic data. |
| NQ · Databento | Only the Databento data. |

The same goes for Tradovate: «Tradovate + Databento», «Tradovate only» and «Databento». When you pick the combined one,
the backtest summary says how many days each source contributes.

## Where the files live

The history lives in `~/Library/Application Support/Trading Hub/Ticks`, in one folder per connection («rithmic»,
«databento», «imported»…) and another per instrument, with one file per day. **Open folder** shows it in the Finder.
See also [Installation](installation.md#where-your-data-is-stored).
