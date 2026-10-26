namespace TradingHub.App.Services;

/// <summary>The text of the Guide window: what each view shows and how to read it.</summary>
public static class ViewGuide
{
    public const string Text = """
        HOW TO READ THE VIEWS (Views button in the chart toolbar)

        Everything here is context measured from the tape and the book. None of it is a signal: it has not been shown to make money.

        CHART
          Candles. The last-price tag, the countdown under it and the price scale follow the price.

        FOOTPRINT
          Each candle split by price: volume bought at the ask against sold at the bid. Stacked one-sided cells show who was pushing.

        HEATMAP
          What rests in the order book at each price over time (bright = more size). Walls that vanish when price nears were never meant
          to trade. Bubbles are the trades that hit them. Needs a connection with depth.

        WHALES (Big Trades + Whale net)
          Bubbles are big market orders, labelled in money. Open the indicator's settings to choose what counts:
            Aggressive - any big market order.     Sweep - one order that ate 3+ price levels.
            Level - a big order near yesterday's levels or a remembered level.     Big print - one single fill that big.
          The sentence at the top says what the whales did in the view. "Two-sided" means buyers and sellers among them roughly cancel.
          Whale average price: where the big buys / big sells averaged. Whale zones: price bands where 3+ big orders of one side clustered.
          Whale net (lower pane): big buys minus big sells per candle. Turn on "Compare with everyone else" to see the crowd's running net
          next to the whales' (the crowd line is stretched to the whales' size, so compare its shape, not its height).

        VOLUME PROFILE
          Volume traded at each price in the session. POC = the busiest price; the value area holds most of the volume.

        GAMMA AND LIQUIDATION LEVELS (BTC, ETH)
          Options call wall, put wall, max pain and gamma flip from Deribit's open interest, plus where longs and shorts were liquidated
          (Bybit) and an ESTIMATE of where the biggest recent orders would be liquidated at 10x-100x leverage. The estimate is a guess.

        TAPE
          Every print as it happens.

        INSIGHTS PANEL (right side)
          Big bid / big ask: the largest resting order near the price and how far it is in basis points.
          Pulled N x: a price where big orders were cancelled N times lately (often a bluff).
          Walls real: of the big orders that left, the share that were traded through. Low = they mostly vanish before they are hit.
          Futures and sentiment (crypto): funding, open interest and its 24 h change, basis, long/short accounts, fear and greed.
        """;
}
