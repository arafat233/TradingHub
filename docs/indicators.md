[← Back to the index](README.md)

# Indicators

Chart indicators are added from the **Indicators** window (the **Indicators** button on the chart bar, or
**Chart → Indicators…**, ⌘I). On the left is the **Library**, with a search box by name or category; **Add to the
chart** (or a double click) adds it, and picking one of the ones already on the chart lets you edit its properties on
the right. **Templates** saves an indicator's settings, applies a saved one or chooses which one new ones start with.
The rest of the chart handling is covered in [Charts](charts.md#indicators).

The strategy indicators (`Sma`, `Ema`, `Rsi`…) are a different, simpler API: see [Strategies](strategies.md).

## Included

| Indicator | Category | Main options |
|---|---|---|
| Moving average | Trend | Period, type (simple, exponential or weighted) and source |
| Bollinger Bands | Trend | Period and number of deviations |
| VWAP with deviations | Volume / Order flow | It resets every session, week or month; up to three volume-weighted deviation bands |
| Volume | Volume / Order flow | Colors; coloured by delta or by candle |
| Delta | Volume / Order flow | Histogram or candles with the delta's high and low |
| Cumulative delta (CVD) | Volume / Order flow | As candles; resets each session or not |
| Big Trades | Volume / Order flow | Bubbles on large aggressive orders (it adds up the executions of one order even if it sweeps several levels). Fixed or adaptive threshold (a percentile of recent orders), where it sits on the price, colors and sizes |
| Session volume profile | Volume / Order flow | Per session, week or all the visible history; volume, delta or bid/ask; value area (%), POC, VAH and VAL |
| RSI | Oscillators | Period |
| MACD | Oscillators | Fast and slow averages, and signal |

They ship in the `TradingHub.Indicators.Native` project and appear in the library grouped by category.

### Technical indicators

The `TradingHub.Indicators.Technical` project is meant for the ATAS collection of technical indicators (about 250).
Their code is public but does not carry an open license, so it is **not published** in this repository: the project only
ships the bare minimum to compile them (the editor types they use). You add them yourself in a minute, as explained in
[Adding the ATAS indicators](#adding-the-atas-indicators), and Trading Hub loads them on its own: they appear in the
library with the rest, by category.

## Your own indicators

You write your own in C# and Trading Hub compiles them automatically.

- They go in `~/Documents/Trading Hub/Indicators` (subfolders are allowed; `bin` and `obj` are ignored). With the folder
  empty, Trading Hub leaves an example: `DeltaPercentExample.cs` («Example: delta % per candle»).
- Trading Hub watches the folder: when you save, create, delete or rename a file, it recompiles everything as soon as
  0.6 seconds pass without changes. You can also force it with **Code → Recompile indicators**.
- Every file is compiled together. If there is an error, the ones from the last good compilation stay available and the
  status bar shows the first error; if it compiles, it reports «Own indicators compiled in N ms».
- They appear at the very top of the library, in the «My indicators» group (with a star) and with the «own» tag. If you
  changed the code of one that is already on a chart, remove it and add it again to use the new version.

### The editor

**Code → Indicator editor…** (⇧⌘E), or the code button in the Indicators window, opens an editor with the list of files
on the left. **New** creates an indicator from a template (it asks for the class name) and **Save and compile** (⌘S or
⌘B) saves it and recompiles; the errors are listed below and clicking one jumps to the line. The folder button opens it
in the Finder.

### Example

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Indicators;

[DisplayName("Average range")]
[Category("My indicators")]
public class AverageRange : Indicator
{
    private readonly ValueDataSeries _series = new("Range", "Average range") { Color = Color.Orange, Width = 2 };
    private int _period = 14;

    [Display(Name = "Period", GroupName = "Settings")]
    [Range(1, 1000)]
    public int Period
    {
        get => _period;
        set { _period = value; RecalculateValues(); }
    }

    public AverageRange()
    {
        Panel = IndicatorDataProvider.NewPanel; // in its own panel, below the price
        DataSeries[0] = _series;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < Period - 1) return; // at the start there aren't enough candles
        decimal sum = 0;
        for (int i = 0; i < Period; i++)
        {
            var candle = GetCandle(bar - i);
            sum += candle.High - candle.Low;
        }

        _series[bar] = sum / Period;
    }
}
```

### The API, in short

- A public class that inherits from `Indicator`, with a parameterless constructor, `[DisplayName("…")]` and
  `[Category("…")]`. `System`, `System.Linq` and `System.Collections.Generic` are already there; add
  `System.ComponentModel`, `System.ComponentModel.DataAnnotations`, `System.Drawing` and `TradingHub.Indicators` (and
  `TradingHub.Indicators.Drawing`, `TradingHub.Rendering.Context` or `TradingHub.Rendering.Tools` if you draw yourself).
- **Series**: `ValueDataSeries` (a line or histogram with `VisualType = VisualMode.Histogram`), `RangeDataSeries`
  (bands, with `new RangeValue { Upper = a, Lower = b }`) and `PaintbarsDataSeries` (it paints the candles). The main
  one goes in `DataSeries[0]` and the rest with `DataSeries.Add(…)`.
- **Panel**: over the price by default; `Panel = IndicatorDataProvider.NewPanel` puts it in its own.
- **Parameters**: public properties with `[Display(Name = "…", GroupName = "…")]` and, if you want,
  `[Range(min, max)]`; in the `set`, store the value and call `RecalculateValues()`.
- **Calculation**: `OnCalculate(int bar, decimal value)` is called for each candle (`bar` from 0, `value` the close).
  `GetCandle(bar)` gives `Open`, `High`, `Low`, `Close`, `Volume`, `Ask`, `Bid`, `Delta`, `MaxDelta`, `MinDelta` and
  `Time`; `CurrentBar` is the number of candles. Watch out for negative indexes at the start.
- **Your own drawing** (optional): in the constructor, `EnableCustomDrawing = true;` and
  `SubscribeToDrawingEvents(DrawingLayouts.Final);`, and override
  `OnRender(RenderContext context, DrawingLayouts layout)` using `ChartInfo.GetXByBar(bar)`,
  `ChartInfo.GetYByPrice(price)` and the `context` methods (`DrawLine`, `FillRectangle`, `DrawString`…). The visible
  candles run from `FirstVisibleBarNumber` to `LastVisibleBarNumber`.

Claude writes indicators too: ask it in the Backtesting panel and it leaves them in this folder (see
[Claude](claude-assistant.md)).

## Extra DLLs

If you have indicators already compiled into a DLL (classes that inherit from `TradingHub.Indicators.Indicator`), copy
it into `~/Library/Application Support/Trading Hub/Indicators` or into an `Indicators` folder next to the app's
executable. They are loaded when Trading Hub opens and appear in their category (their `[Category]`'s, or «Other») with
the «extra» tag. One that cannot be loaded leaves a notice and does not hold up the rest.

## Adding the ATAS indicators

ATAS publishes its indicators' code at [AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators). There are
two ways to use them in Trading Hub.

**All at once**, inside the app (from the repository folder):

```bash
git clone https://github.com/AtasPlatform/Indicators.git third_party/ATAS-Indicators
python3 tools/port_atas.py
./build-app.sh --install
```

`tools/port_atas.py` copies the indicators from the `Technical` folder into
`src/TradingHub.Indicators.Technical/Atas`, swaps the ATAS namespaces for the Trading Hub ones (the table below)
without touching the logic and applies a few fixes so they compile and don't divide by zero. It leaves out whatever
depends on ATAS internals (their window editors, the heatmap and DOM v3). If you want to exclude others, pass them
comma-separated in `TRADINGHUB_PORT_EXCLUDE=File1.cs,File2.cs`. To update them when ATAS publishes changes:

```bash
git -C third_party/ATAS-Indicators pull
python3 tools/port_atas.py
./build-app.sh --install
```

`third_party/` and `src/TradingHub.Indicators.Technical/Atas/` are in the `.gitignore`: the ATAS code stays in your copy
and is not uploaded if you fork.

**One by one**, without building the app: copy the `.cs` into `~/Documents/Trading Hub/Indicators` and it is compiled as
one of your own indicators (with the same namespace adaptation). The ones that use other ATAS indicators need you to
copy those too: for example, the ATAS `CCI.cs` and `Momentum.cs` use their `SMA.cs`. The same applies to C# indicators
from any other public source written for the ATAS API.

Those indicators belong to their authors: use them in line with each source's license.

## ATAS API compatibility

The Trading Hub indicator API has the same shape as the ATAS one (`Indicator`, `OnCalculate`, `DataSeries`, `OnRender`,
`GetCandle`, `ChartInfo`…), so you can paste in indicators written for ATAS. Before compiling, Trading Hub swaps the
namespaces on its own:

| In the ATAS code | It compiles as |
|---|---|
| `ATAS.Indicators` · `ATAS.Indicators.Drawing` · `ATAS.Indicators.Technical` | `TradingHub.Indicators` · `TradingHub.Indicators.Drawing` · `TradingHub.Indicators.Technical` |
| `ATAS.DataFeedsCore` | `TradingHub.Trading` |
| `OFT.Rendering` and its `Context`, `Context.GDIPlus`, `Settings`, `Tools`, `Control` | `TradingHub.Rendering` and the equivalents |
| `OFT.Attributes` · `OFT.Attributes.Editors` · `OFT.Localization` | `TradingHub.Attributes` · `TradingHub.Attributes.Editors` · `TradingHub.Localization` |
| `Utils.Common` · `Utils.Common.Logging` · `Utils.Common.Collections` | `TradingHub.Common` and the equivalents |

The aliases `CrossColor` and `CrossColors` (= `System.Drawing.Color`), `CrossPen`, `CrossPens`, `CrossSolidBrush`,
`CrossKey` and `CrossKeyEventArgs` are also defined, along with the `TRADINGHUB` and `CROSS_PLATFORM` preprocessor
symbols. With `#if TRADINGHUB … #else … #endif` the same file can hold what only compiles here and what only compiles in
ATAS. The adaptation applies to strategies too. Not everything from ATAS has an equivalent: if something doesn't
compile, the error says what is missing.
