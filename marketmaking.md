# Market Making in StockSharp

StockSharp has no class literally named `MarketMakingStrategy`, but it ships a complete **quoting / market-making** model in two layers: the current `QuotingProcessor` + `IQuotingBehavior` architecture, and the legacy (obsolete) `QuotingStrategy` family. Two-sided market making is achieved by running two quoting processors (one Buy, one Sell), as demonstrated by the `MqSpreadStrategy` sample.

---

## 1. The New Architecture (Recommended): `QuotingProcessor` + `IQuotingBehavior`

Located in `Algo.Strategies/Quoting/`:

- **`QuotingProcessor`** (`QuotingProcessor.cs`) — the passive quoting engine: subscribes to the filtered order book / ticks / position, asks the behavior for a price, then registers/cancels/modifies orders and handles order life-cycle rules.
- **`IQuotingBehavior`** (`IQuotingBehavior.cs`) — the pure quoting-logic contract (`CalculateBestPrice`, `NeedQuoting`) with concrete behaviors:
  - `MarketQuotingBehavior` — quotes at the market price; with `MarketPriceTypes.Middle` it computes the **mid-price**: `bestBid + (bestAsk - bestBid) / 2` (`IQuotingBehavior.cs@82-87`).
  - `BestByPriceQuotingBehavior` — quotes at best bid/ask with an offset.
  - `BestByVolumeQuotingBehavior` — quotes at the price where the cumulative volume ahead exceeds a threshold.
  - `LevelQuotingBehavior` — quotes at a specified order-book depth level (can create its own `OwnLevel`).
  - `LimitQuotingBehavior` — quotes at a fixed limit price.
  - `LastTradeQuotingBehavior` — quotes at the last trade price (offset adjustable).
  - `VWAPQuotingBehavior`, `TWAPQuotingBehavior` — volume/time-weighted execution.
  - `TheorPriceQuotingBehavior` / `VolatilityQuotingBehavior` — options quoting around Black-Scholes theoretical price / implied-volatility range.
- **`QuotingEngine`** (`QuotingEngine.cs`) — pure functional engine that computes the recommended `QuotingAction` (Register / Cancel / Modify / Finish / None) from a `QuotingInput` (`QuotingAction.cs`, `QuotingInput.cs`).
- **`QuotingBehaviorAlgo`** (`QuotingBehaviorAlgo.cs`) — `IPositionModifyAlgo` that slices a large volume into chunks using an `IQuotingBehavior` for pricing.

> Note: `QuotingStrategy` is marked `[Obsolete("Use QuotingProcessor.")]` — the processor/behavior split is the current way to do quoting.

---

## 2. Legacy (Obsolete) `QuotingStrategy` Family

All marked `[Obsolete("Use QuotingProcessor.")]`, base class `QuotingStrategy : Strategy`:

| Class | Behavior |
|---|---|
| `QuotingStrategy` | Abstract base; drives a `QuotingProcessor`. |
| `BestByPriceQuotingStrategy` | Quotes at best bid/ask, offset via `BestPriceOffset`. |
| `BestByVolumeQuotingStrategy` | Quotes where cumulative volume ahead exceeds `VolumeExchange`. |
| `MarketQuotingStrategy` | Quotes by market price (Following / Opposite / **Middle**) via `MarketPriceTypes`. |
| `LevelQuotingStrategy` | Quotes at a depth level (midpoint between min/max levels). |
| `LimitQuotingStrategy` | Quotes at a fixed `LimitPrice`. |
| `LastTradeQuotingStrategy` | Quotes at the last trade price (offset adjustable). |
| `TheorPriceQuotingStrategy` | Options quoting around Black-Scholes theoretical-price offset range. |
| `VolatilityQuotingStrategy` | Options quoting within an implied-volatility range using `IBlackScholes`. |

---

## 3. Grid / Two-Sided Market Making

There is no dedicated multi-level "grid" market-making model class. The closest concepts:

- `MarketQuotingBehavior` with `MarketPriceTypes.Middle` quotes around **mid-price**.
- `LevelQuotingBehavior` quotes at the midpoint between order-book levels and can create its own level.
- A true two-sided book is built by running **two `QuotingProcessor`s (one Buy, one Sell)** — see `MqSpreadStrategy` below.

---

## 4. Samples Demonstrating Market Making

| Sample | File | What it shows |
|---|---|---|
| **Two-sided market making** | `Samples/06_Strategies/07_LiveSpread/MqSpreadStrategy.cs` | `MqSpreadStrategy : Strategy` — "Market quoting spread strategy using QuotingProcessor. Creates buy and sell quotes to maintain a spread." Instantiates a Buy and a Sell `QuotingProcessor` with `MarketQuotingBehavior` (`MqSpreadStrategy.cs@130-210`). |
| One-sided market quoting | `Samples/06_Strategies/07_LiveSpread/MqStrategy.cs` | `MqStrategy : Strategy` — picks the side based on current position. |
| Countertrend quoting | `Samples/06_Strategies/07_LiveSpread/StairsCountertrendStrategy.cs` | Countertrend strategy using `QuotingProcessor` + `MarketQuotingBehavior`. |
| Terminal quoting | `Samples/06_Strategies/10_LiveTerminal/MarketQuotingProcessorStrategy.cs` | Single-sided market-price quoting (registered in `StrategiesWindow.xaml.cs@57-101`). |
| History quoting | `Samples/06_Strategies/06_HistoryQuoting/StairsCountertrendStrategy.cs` | History-based quoting demo (0.1% re-quote threshold). |
| Options MM + hedging | `Samples/06_Strategies/09_LiveOptionsQuoting/Strategies/DeltaHedgeStrategy.cs` | `DeltaHedgeStrategy : HedgeStrategy` — options market making with delta hedging; `HedgeStrategy.cs` creates a `QuotingProcessor` per re-hedge order. |

The same samples exist in the Avalonia twin suite under `SamplesAvalonia/06_Strategies/`.

---

## 5. What Is NOT in This Repo

- **No Designer app** — Designer is a separate closed-source product; `Designer.Templates/` only holds empty strategy/diagram templates (no market-making template).
- **No "MarketMaking" naming** — the only "MarketMaker" hits in the codebase are FIX/CTP protocol tags and localization strings (e.g. `Order.IsMarketMaker`, `MarketMakerProtection` in `Localization/strings.json@3396-3399`), not strategy classes.

---

## Recommendation

To build a market-making bot today, use the recommended pattern: **`QuotingProcessor` + `MarketQuotingBehavior`**, with two processors (one per side), as demonstrated by `MqSpreadStrategy`. Use the obsolete `QuotingStrategy` family only as reference, not for new code.
