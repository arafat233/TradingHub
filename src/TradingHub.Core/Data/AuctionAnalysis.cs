namespace TradingHub.Data;

/// <summary>A run of rows, by the price of its lowest row (<see cref="Low"/>) and of its highest row (<see cref="High"/>).</summary>
public sealed record Zone(decimal Low, decimal High);

/// <summary>How a price row looks when time (TPO letters) and money (volume) are read together.</summary>
public enum RowCase
{
    /// <summary>Lots of time and lots of volume: real acceptance; in balance price rotates around it.</summary>
    Regulars,

    /// <summary>Little time and little volume: a confirmed imbalance that price moves through fast.</summary>
    Rush,

    /// <summary>Lots of time but little volume: price drifted there and nobody committed size. A weak level.</summary>
    Camper,

    /// <summary>Little time but heavy volume: big orders filled there and are often defended on the first retest.</summary>
    BigSpender,
}

/// <summary>One session's profile as plain numbers: for every price row (ascending) the TPO periods that traded there and the volume.</summary>
public sealed record AuctionInput(decimal[] Rows, int[][] Slots, decimal[] Volume, decimal RowSize);

public sealed record AuctionResult(
    decimal TpoPoc, decimal TpoVah, decimal TpoVal,
    decimal VolumePoc, decimal VolumeVah, decimal VolumeVal,
    decimal IbHigh, decimal IbLow, bool ExtendedUp, bool ExtendedDown,
    List<Zone> SinglePrints, Zone? TailHigh, Zone? TailLow, bool PoorHigh, bool PoorLow,
    string Shape, string ShapeMeaning,
    List<Zone> HighVolumeNodes, List<Zone> LowVolumeNodes,
    bool PocsAligned, Zone FairValue, Zone? CoreOfValue, string VolumeValueVersusTime,
    RowCase[] Cases);

/// <summary>
/// Reads a session's TPO (time) and volume (money) profiles together, the way a market-profile trader does: initial balance and range
/// extension, single prints, tails and poor highs / lows, the shape of the day, high and low volume nodes, how the two POCs and the two
/// value areas compare, and what each price row says when time and volume are read together.
/// </summary>
public static class AuctionAnalysis
{
    /// <param name="valueAreaPercent">Share of the TPOs / of the volume the value area holds.</param>
    /// <param name="poorLetters">A top or bottom row with at least this many letters and no tail is a poor high / low.</param>
    /// <param name="tailMinRows">A run of single prints at an extreme needs this many rows to count as a tail.</param>
    /// <param name="maxNodes">At most this many high and this many low volume nodes are kept: the thickest and the thinnest, not every wiggle.</param>
    /// <param name="ibPeriods">How many of the first periods make the initial balance (2 = the first hour with 30-minute periods).</param>
    public static AuctionResult Analyze(AuctionInput p, int valueAreaPercent = 70, int poorLetters = 3, int tailMinRows = 2, int ibPeriods = 2, int maxNodes = 4)
    {
        int n = p.Rows.Length;
        if (n == 0) throw new ArgumentException("An empty profile cannot be analysed.");
        var tpo = p.Slots.Select(s => (decimal)s.Length).ToArray();
        var (tpoPoc, tpoVah, tpoVal) = ValueArea(p.Rows, tpo, valueAreaPercent);
        var (volPoc, volVah, volVal) = ValueArea(p.Rows, p.Volume, valueAreaPercent);

        // initial balance: the rows traded in the first periods; range extension: rows traded beyond it
        decimal ibHigh = decimal.MinValue, ibLow = decimal.MaxValue;
        for (int i = 0; i < n; i++)
            if (p.Slots[i].Any(s => s < ibPeriods)) { ibHigh = Math.Max(ibHigh, p.Rows[i]); ibLow = Math.Min(ibLow, p.Rows[i]); }
        bool extUp = ibHigh != decimal.MinValue && p.Rows[^1] > ibHigh, extDown = ibLow != decimal.MaxValue && p.Rows[0] < ibLow;

        // single prints: runs of rows with exactly one letter; at the very top or bottom a run of enough rows is a tail
        var singles = new List<Zone>();
        Zone? tailHigh = null, tailLow = null;
        for (int i = 0; i < n;)
        {
            if (p.Slots[i].Length != 1) { i++; continue; }
            int j = i;
            while (j + 1 < n && p.Slots[j + 1].Length == 1) j++;
            int length = j - i + 1;
            var zone = new Zone(p.Rows[i], p.Rows[j]);
            if (j == n - 1 && length >= tailMinRows) tailHigh = zone;
            else if (i == 0 && length >= tailMinRows) tailLow = zone;
            else if (i > 0 && j < n - 1) singles.Add(zone); // inside the profile: the price moved through too fast to trade
            i = j + 1;
        }

        bool poorHigh = tailHigh == null && p.Slots[^1].Length >= poorLetters;
        bool poorLow = tailLow == null && p.Slots[0].Length >= poorLetters;

        // shape: a long thin profile is a trend day; otherwise where the TPO POC sits says balance (D), short covering (P) or long liquidation (b)
        int pocIndex = Array.IndexOf(p.Rows, tpoPoc);
        double pocPosition = n > 1 ? pocIndex / (double)(n - 1) : 0.5;
        decimal span = p.Rows[^1] - p.Rows[0] + p.RowSize, ibSpan = ibHigh == decimal.MinValue ? span : ibHigh - ibLow + p.RowSize;
        decimal maxCount = tpo.Max(), averageCount = tpo.Average();
        string shape, meaning;
        if (span >= 2.5m * ibSpan && maxCount <= Math.Max(3, averageCount * 1.6m)) { shape = "Trend"; meaning = "long thin profile: price never stopped long enough to build value"; }
        else if (pocPosition >= 0.62) { shape = "P"; meaning = "short covering: a push up, then price settles near the top"; }
        else if (pocPosition <= 0.38) { shape = "b"; meaning = "long liquidation: a push down, then price settles near the bottom"; }
        else { shape = "D"; meaning = "balance: both sides are happy trading in the middle"; }

        // nodes: thick zones (heavy volume, price slows and rotates) and thin ones inside the profile (rejected, price moves through fast)
        var traded = p.Volume.Where(v => v > 0).ToList();
        decimal meanVolume = traded.Count == 0 ? 0 : traded.Average();
        var hvn = Runs(p, i => p.Volume[i] >= 1.25m * meanVolume, minLength: 2);
        var lvn = Runs(p, i => p.Volume[i] <= 0.5m * meanVolume, minLength: 1).Where(z => z.Low > p.Rows[0] && z.High < p.Rows[^1]).ToList();
        decimal VolumeIn(Zone z) { decimal sum = 0; for (int i = 0; i < p.Rows.Length; i++) if (p.Rows[i] >= z.Low && p.Rows[i] <= z.High) sum += p.Volume[i]; return sum; }
        int RowsIn(Zone z) => (int)Math.Round((z.High - z.Low) / p.RowSize) + 1;
        hvn = hvn.OrderByDescending(VolumeIn).Take(maxNodes).OrderBy(z => z.Low).ToList();
        lvn = lvn.OrderByDescending(z => RowsIn(z) * (meanVolume - VolumeIn(z) / RowsIn(z))).Take(maxNodes).OrderBy(z => z.Low).ToList();

        // the two POCs and the two value areas
        bool aligned = Math.Abs(tpoPoc - volPoc) <= p.RowSize;
        var fair = new Zone(Math.Min(tpoPoc, volPoc), Math.Max(tpoPoc, volPoc));
        decimal coreLow = Math.Max(tpoVal, volVal), coreHigh = Math.Min(tpoVah, volVah);
        Zone? core = coreLow <= coreHigh ? new Zone(coreLow, coreHigh) : null;
        decimal midTpo = (tpoVah + tpoVal) / 2, midVol = (volVah + volVal) / 2;
        string versus = midVol - midTpo > p.RowSize / 2 ? "higher" : midTpo - midVol > p.RowSize / 2 ? "lower" : "same";

        // time against money, row by row (a row is "high" in either if it reaches 40% of the busiest row)
        decimal maxVolume = p.Volume.Max();
        var cases = new RowCase[n];
        for (int i = 0; i < n; i++)
        {
            bool time = maxCount > 0 && tpo[i] >= 0.4m * maxCount, money = maxVolume > 0 && p.Volume[i] >= 0.4m * maxVolume;
            cases[i] = time && money ? RowCase.Regulars : !time && !money ? RowCase.Rush : time ? RowCase.Camper : RowCase.BigSpender;
        }

        return new AuctionResult(tpoPoc, tpoVah, tpoVal, volPoc, volVah, volVal, ibHigh == decimal.MinValue ? 0 : ibHigh, ibLow == decimal.MaxValue ? 0 : ibLow, extUp, extDown,
            singles, tailHigh, tailLow, poorHigh, poorLow, shape, meaning, hvn, lvn, aligned, fair, core, versus, cases);
    }

    private static List<Zone> Runs(AuctionInput p, Func<int, bool> test, int minLength)
    {
        var zones = new List<Zone>();
        for (int i = 0; i < p.Rows.Length;)
        {
            if (!test(i)) { i++; continue; }
            int j = i;
            while (j + 1 < p.Rows.Length && test(j + 1)) j++;
            if (j - i + 1 >= minLength) zones.Add(new Zone(p.Rows[i], p.Rows[j]));
            i = j + 1;
        }

        return zones;
    }

    /// <summary>POC (the row with most, the one nearest the middle on a tie) and the value area that grows from it towards the side with more.</summary>
    private static (decimal Poc, decimal Vah, decimal Val) ValueArea(decimal[] rows, decimal[] weight, int percent)
    {
        int n = rows.Length;
        decimal middle = (rows[0] + rows[^1]) / 2;
        int poc = 0;
        for (int i = 1; i < n; i++)
            if (weight[i] > weight[poc] || (weight[i] == weight[poc] && Math.Abs(rows[i] - middle) < Math.Abs(rows[poc] - middle))) poc = i;
        int up = poc, dn = poc;
        decimal acc = weight[poc], target = weight.Sum() * percent / 100m;
        while (acc < target && (up < n - 1 || dn > 0))
        {
            decimal wu = up < n - 1 ? weight[up + 1] : -1, wd = dn > 0 ? weight[dn - 1] : -1;
            acc += wu >= wd ? weight[++up] : weight[--dn];
        }

        return (rows[poc], rows[up], rows[dn]);
    }
}

/// <summary>Has price been accepted beyond a level (outside value), or is it only drifting there?</summary>
public sealed record AcceptanceResult(string State, int PeriodsBeyond, double VolumeShare);

public static class Acceptance
{
    /// <summary>
    /// In market profile, price is accepted beyond a level when it holds there for at least two TPO periods (one hour); with volume
    /// building out there too, acceptance is confirmed. Time without volume is drifting, which often slides back inside.
    /// </summary>
    /// <param name="periods">Per TPO period, oldest first: the close of the period and the volume traded beyond the level during it.</param>
    /// <param name="totalVolume">All the volume of the session so far.</param>
    public static AcceptanceResult Evaluate(bool above, decimal level, IReadOnlyList<(decimal Close, decimal VolumeBeyond)> periods, decimal totalVolume, int minPeriods = 2, double minVolumeShare = 0.15)
    {
        bool Beyond(decimal close) => above ? close > level : close < level;
        int streak = 0;
        for (int i = periods.Count - 1; i >= 0 && Beyond(periods[i].Close); i--) streak++;
        double share = totalVolume <= 0 ? 0 : (double)(periods.Sum(x => x.VolumeBeyond) / totalVolume);
        if (streak == 0) return new AcceptanceResult(periods.Any(x => Beyond(x.Close)) ? "rejected" : "inside", 0, share);
        if (streak < minPeriods) return new AcceptanceResult("testing", streak, share);
        return new AcceptanceResult(share >= minVolumeShare ? "accepted" : "drifting", streak, share);
    }
}
