using System.Text;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>
/// Live comparison, does nothing unless ICEBERG_AGREE_MINUTES is set (e.g. <c>$env:ICEBERG_AGREE_MINUTES=60; $env:ICEBERG_AGREE_SYMBOL="BTCUSD"</c>).
/// Feeds Bitfinex's raw order book (real order ids) into <see cref="OrderIcebergDetector"/> and the same trades and book pictures into
/// <see cref="IcebergDetector"/>, then counts how many tape flags a level-by-level order detector also sees. Result in %TEMP%\iceberg-agreement.txt.
/// Limits: Bitfinex does not say whether an order left the book because it was filled, so a Remove counts as a fill when a trade at that price on
/// the right side happened within 400 ms of it (both ways); that inference is the weak link. Spot has no native icebergs, so only synthetic ones can match.
/// </summary>
public class IcebergAgreementLive
{
    private static readonly TimeSpan Lag = TimeSpan.FromMilliseconds(400), Slack = TimeSpan.FromSeconds(15);

    private sealed class Sink(Instrument inst) : IMarketDataSink
    {
        public readonly object Gate = new();
        public readonly List<(DateTime At, Trade T)> Trades = new();
        public readonly List<(DateTime At, DepthSnapshot Book)> Books = new();
        public readonly Queue<(DateTime At, OrderEvent E)> Orders = new();
        public int TradeCount, OrderCount;
        private DateTime _lastBook;

        public void OnTrades(Instrument i, IReadOnlyList<Trade> trades)
        {
            lock (Gate) foreach (var t in trades) { Trades.Add((DateTime.UtcNow, t)); TradeCount++; }
        }

        public void OnDepth(Instrument i, DepthUpdate u)
        {
            var now = DateTime.UtcNow;
            lock (Gate) if (now - _lastBook >= TimeSpan.FromMilliseconds(250)) { Books.Add((now, u.Snapshot)); _lastBook = now; }
        }

        public void OnStatus(Instrument i, FeedStatus s, string? m) { }
        public void OnOrderEvents(Instrument i, IReadOnlyList<OrderEvent> events)
        {
            var now = DateTime.UtcNow;
            lock (Gate) foreach (var e in events) { Orders.Enqueue((now, e)); OrderCount++; }
        }
    }

    [Fact]
    public async Task Tape_flags_versus_the_order_level_detector_on_a_live_book()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("ICEBERG_AGREE_MINUTES"), out int minutes) || minutes <= 0) return;
        string symbol = Environment.GetEnvironmentVariable("ICEBERG_AGREE_SYMBOL") ?? "BTCUSD";
        using var feed = new TradingHub.Feeds.Bitfinex.BitfinexFeed();
        var inst = (await feed.GetInstrumentsAsync(CancellationToken.None)).First(i => i.Symbol == symbol);
        var sink = new Sink(inst);

        var keep = TimeSpan.FromDays(1);
        var ratioOnly = new IcebergDetector { MinRefills = 0, Keep = keep };
        var withRefills = new IcebergDetector { Keep = keep };
        var orders = new OrderIcebergDetector { Reload = TimeSpan.FromMilliseconds(500), Keep = keep };

        int tradeDone = 0, bookDone = 0, fills = 0, cancels = 0;
        var recent = new List<(DateTime At, long Ticks, bool Buy)>();
        void Step(DateTime now, bool final)
        {
            List<(DateTime, Trade)> newTrades;
            List<(DateTime At, OrderEvent E)> due = new();
            lock (sink.Gate)
            {
                newTrades = sink.Trades.Skip(tradeDone).ToList();
                tradeDone = sink.Trades.Count;
                while (sink.Orders.Count > 0 && (final || now - sink.Orders.Peek().At >= Lag)) due.Add(sink.Orders.Dequeue());
            }

            foreach (var (at, t) in newTrades)
            {
                long ticks = inst.ToTicks(t.Price);
                bool buy = t.Side == TradeSide.Buy;
                recent.Add((at, ticks, buy));
                DepthSnapshot? book;
                lock (sink.Gate) { int i = sink.Books.FindLastIndex(b => b.At <= at); book = i >= 0 ? sink.Books[i].Book : null; }
                ratioOnly.OnTrade(at, ticks, t.Volume, buy, book);
                withRefills.OnTrade(at, ticks, t.Volume, buy, book);
            }

            recent.RemoveAll(r => r.At < now - TimeSpan.FromSeconds(5));
            foreach (var (at, e) in due)
            {
                var kind = e.Kind;
                if (kind == OrderEventKind.Remove)
                {
                    // a resting sell is filled by buyers: a trade at that price on the aggressor's side within the lag, before or after
                    bool hit = recent.Any(r => r.Ticks == e.PriceTicks && r.Buy == e.Ask && Math.Abs((r.At - at).TotalMilliseconds) <= Lag.TotalMilliseconds);
                    kind = hit ? OrderEventKind.Fill : OrderEventKind.Cancel;
                    if (hit) fills++; else cancels++;
                }

                orders.OnEvent(new OrderEvent(at, kind, e.OrderId, e.Ask, e.PriceTicks, e.Size));
            }
        }

        var end = DateTime.UtcNow.AddMinutes(minutes);
        using (feed.Subscribe(inst, sink))
        {
            while (DateTime.UtcNow < end) { await Task.Delay(100); Step(DateTime.UtcNow, false); }
        }

        Step(DateTime.UtcNow, true);

        bool Overlaps(IcebergEvent a, IcebergEvent b) => a.Ask == b.Ask && a.Ticks == b.Ticks && a.Start <= b.Last + Slack && b.Start <= a.Last + Slack;
        var synth = orders.Events.Where(o => !o.Native).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"{symbol}, {minutes} min. trades {sink.TradeCount}, order events {sink.OrderCount}, removes read as fills {fills} / cancels {cancels}");
        sb.AppendLine($"order-level synthetic icebergs (refill chains, >= {orders.MinSyntheticRefills} refills): {synth.Count}");
        foreach (var (name, det) in new[] { ("tape, ratio rule only", ratioOnly), ("tape, ratio or refill rule", withRefills) })
        {
            int n = det.Events.Count, hit = det.Events.Count(e => synth.Any(o => Overlaps(e, o)));
            int found = synth.Count(o => det.Events.Any(e => Overlaps(e, o)));
            sb.AppendLine($"{name}: flagged {n}, also seen by the order detector {hit} ({(n == 0 ? 0 : 100.0 * hit / n):0}%); order icebergs the tape found {found} of {synth.Count} ({(synth.Count == 0 ? 0 : 100.0 * found / synth.Count):0}%)");
        }

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "iceberg-agreement.txt"), sb.ToString());
    }

}
