# PnL Curve Viewer — Summary

## What was built

A **PnL-over-time viewer** for imported IB trade records: X-axis = date, Y-axis = cumulative realized PnL, with a security selector so the user can choose which instrument (underlying, e.g. SPY) and its related options to show.

### New files
| File | Purpose |
|---|---|
| `Algo/PnL/PnlCurveCalculator.cs` | Testable core: `BuildCumulativeByDate(trades, getMultiplier)` FIFO-matches long/short per security via `PnLQueue` (applying option ×100 `LotMultiplier`), then returns cumulative realized PnL points ordered by date (`PnLCurvePoint`). |
| `Tests/PnlCurveCalculatorTests.cs` | 4 unit tests: SPY round-trip (+200), option ×100 multiplier (50 vs 0.5), multi-security aggregation (+250), date-ordering with non-monotonic input. |
| `Samples/05_Chart/04_PnlCurve/` | WPF sample (`04_Chart.PnlCurve.csproj`, `App.xaml(.cs)`, `Properties/AssemblyInfo.cs`, `MainWindow.xaml(.cs)`): checkbox `TreeView` of underlying → its options (derived from the importer's code convention) on the left, `StockSharp.Xaml.Charting` `ChartPanel` on the right — one colored `IChartLineElement` per checked security (X=date, Y=cumulative PnL) plus a black "Total" line. Reads `Paths.HistoryDataPath` (same storage as `06_ImportIbTrades`). |

### Modified files
- `StockSharp.slnx` — registered `Samples/05_Chart/04_PnlCurve/04_Chart.PnlCurve.csproj` under `/Samples/05_Chart/`.

## Verification
- `Algo` builds: 0 warnings / 0 errors.
- New tests: **4/4 passed**.
- Existing import tests: **24/24 passed** (`ImportTests` + `IbTradesImporterTests` — no regression).
- WPF sample builds: **0 warnings / 0 errors** (verified on macOS with `-p:EnableWindowsTargeting=true`; no flag needed on Windows). Uses the `StockSharp.Xaml.Charting` NuGet package, same as the existing `03_Performance` chart sample.

## Notes / deviations from doc.md
- None functionally; the option/underlying grouping and ×100 multiplier are derived from the importer's normalized code convention (`ROOT YYMMDD[C/P]STRIKE`), so the viewer works directly on `06_ImportIbTrades` output without a persistent security registry.
- The chart shows **realized** PnL (matches imported fills); unrealized PnL is out of scope.
- Sample was verified by build + unit tests only (a WPF window cannot be executed on macOS here); run it on Windows (the UTM VM) or any Windows machine in Rider.

## Usage
1. Import an IB "Transaction History" CSV first: `dotnet run --project Samples/03_Storage/06_ImportIbTrades <trades.csv>`.
2. Run the viewer (Windows / VM): `dotnet run --project Samples/05_Chart/04_PnlCurve`.
3. In the window: check `SPY` (and/or its option series), lines appear — X = date, Y = cumulative PnL; checking an underlying toggles its options; a black "Total" line summarizes the selection.
