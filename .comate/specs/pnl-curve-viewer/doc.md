# PnL Curve Viewer (`pnl-curve-viewer`)

## 1. Requirement

The user imports Interactive Brokers trade history (SPY ETF + SPY options) into StockSharp's per-security storage via the `ib-trades-importer` feature. They now want to **visualize PnL over time**:

- **X-axis = date**, **Y-axis = cumulative PnL**.
- The user can **choose which instrument/security and its related options** to display (e.g. pick `SPY` and toggle its option series `SPY 250117C500`, `SPY 250117P500`, …).

This must work from **imported records only** (no live connection), reading the same local storage the importer writes to.

## 2. Architecture and Technical Approach

Two layers, mirroring the `ib-trades-importer` split (testable core + UI sample):

1. **Testable core — `Algo/PnL/PnlCurveCalculator.cs`** (new static class in `Algo`, namespace `StockSharp.Algo.PnL`):
   - Consumes per-security `ExecutionMessage` (transaction) streams.
   - Matches long/short FIFO per security with the existing `PnLQueue` (realized PnL only), applying the security's `LotMultiplier` (options = 100) so PnL is in dollars.
   - Aggregates cumulative realized PnL by **date**, ordered by date → a curve of `(date, cumulativePnL)` points.
   - Pure and unit-testable (no UI, no storage).

2. **WPF viewer sample — `Samples/05_Chart/04_PnlCurve/`**:
   - Uses `StockSharp.Xaml.Charting` (`ChartPanel`, `IChartLineElement`) for a real interactive line chart: X = date, Y = PnL.
   - Left panel: a checkbox `TreeView` of securities grouped as **underlying → its options** (derived from the importer's code convention); the user checks which securities/lines to show. Checking an underlying toggles its options too.
   - Right panel: the chart — **one colored line per checked security** (plus a combined "selected total" line).
   - Reads security ids from the local drive (`LocalMarketDataDrive.GetAvailableSecuritiesAsync`) and per-security transaction storages — the same `Paths.HistoryDataPath` the `06_ImportIbTrades` sample writes to.

Why this approach:
- **Cross-platform core**: the PnL math is in `Algo` → unit-tested in the `Tests` project (runs on macOS here).
- **WPF chart**: real interactive charting, consistent with the repo's `Samples/05_Chart` family and the user's existing Windows-VM workflow; `StockSharp.Xaml.Charting` is used by `03_Performance` and restores offline from NuGet.
- **Underlying/option grouping derived from codes** (the importer normalizes options to `ROOT YYMMDD[C/P]STRIKE`, e.g. `SPY 250117C500`): the viewer works on the importer's output without needing a persistent security registry. Non-option securities (e.g. `SPY`) have `LotMultiplier` 1; options 100.

## 3. Affected Files

| File | Type of change |
|---|---|
| `/Users/tino/GitHub/StockSharp/Algo/PnL/PnlCurveCalculator.cs` | **New** — cumulative-PnL-by-date calculator |
| `/Users/tino/GitHub/StockSharp/Tests/PnlCurveCalculatorTests.cs` | **New** — unit tests |
| `/Users/tino/GitHub/StockSharp/Samples/05_Chart/04_PnlCurve/04_Chart.PnlCurve.csproj` | **New** — WPF sample project |
| `/Users/tino/GitHub/StockSharp/Samples/05_Chart/04_PnlCurve/App.xaml` + `App.xaml.cs` | **New** — app entry |
| `/Users/tino/GitHub/StockSharp/Samples/05_Chart/04_PnlCurve/MainWindow.xaml` | **New** — security tree + chart layout |
| `/Users/tino/GitHub/StockSharp/Samples/05_Chart/04_PnlCurve/MainWindow.xaml.cs` | **New** — load data, build curves, draw chart |
| `/Users/tino/GitHub/StockSharp/Samples/05_Chart/04_PnlCurve/Properties/AssemblyInfo.cs` | **New** — assembly metadata (mirrors 03_Performance) |
| `/Users/tino/GitHub/StockSharp/StockSharp.slnx` | **Mod** — register `04_Chart.PnlCurve.csproj` under `/Samples/05_Chart/` |

No existing code is modified. `Algo` is already referenced by the `Tests` project.

## 4. Implementation Details

### 4.1 `PnlCurveCalculator` (Algo/PnL/PnlCurveCalculator.cs)

```csharp
namespace StockSharp.Algo.PnL;

/// <summary>
/// Point of a PnL curve: cumulative realized PnL as of a date.
/// </summary>
public readonly record struct PnLCurvePoint(DateTime Date, decimal CumulativePnL);

/// <summary>
/// Helper for building cumulative PnL curves over time from trades.
/// </summary>
public static class PnlCurveCalculator
{
	/// <summary>
	/// Build the cumulative realized PnL curve by date for the given trades.
	/// </summary>
	/// <param name="trades">Own trades (transactions) of one or more securities.</param>
	/// <param name="getMultiplier">Optional security lot-multiplier provider (options = 100). Defaults to 1.</param>
	/// <returns>Curve points ordered by date.</returns>
	public static IEnumerable<PnLCurvePoint> BuildCumulativeByDate(IEnumerable<ExecutionMessage> trades, Func<SecurityId, decimal?> getMultiplier = null)
	{
		ArgumentNullException.ThrowIfNull(trades);

		var queues = new Dictionary<SecurityId, PnLQueue>();
		var points = new List<(DateTime time, decimal pnl)>();

		foreach (var trade in trades)
		{
			if (trade.Side is not Sides)
				continue;

			var queue = queues.SafeAdd(trade.SecurityId, sid => new PnLQueue(sid));

			if (getMultiplier?.Invoke(trade.SecurityId) is decimal multiplier)
				queue.LotMultiplier = multiplier;

			var info = queue.Process(trade);
			points.Add((trade.ServerTime, info.PnL));
		}

		var cumulative = 0m;

		foreach (var group in points.OrderBy(p => p.time).GroupBy(p => p.time.Date))
		{
			cumulative += group.Sum(p => p.pnl);
			yield return new PnLCurvePoint(group.Key, cumulative);
		}
	}
}
```

Notes:
- `queues.SafeAdd` — `Ecng.Collections` dictionary extension (already used in `PnLManager`).
- `PnLQueue.Process(trade)` returns `PnLInfo(trade.ServerTime, closedVolume, tradePnL)`; `tradePnL` is 0 for a position-opening trade and the realized PnL when it closes (PnLQueue.cs:210). The multiplier formula is `(StepPrice ?? 1)/PriceStep * Leverage * LotMultiplier` — with defaults `StepPrice=null`, `PriceStep=1`, `Leverage=1` → `LotMultiplier`, so setting `LotMultiplier` to the security's multiplier gives dollar PnL (SPY ×1, SPY options ×100).
- Trades are deduplicated implicitly: `PnLQueue` ignores re-processed trade ids.

### 4.2 Tests (Tests/PnlCurveCalculatorTests.cs)

Mirror `ImportTests` style (`BaseTestClass`, `CancellationToken`, `AssertEqual`).

1. **`Spy_RoundTrip`** — buy 100 SPY @ 480 on day1, sell 100 @ 482 on day2 → points `(day1, 0)`, `(day2, 200)`.
2. **`Option_Multiplier`** — buy 1 SPY option @ 2.50, sell @ 3.00, `getMultiplier → 100` → final point 50; with multiplier 1 → 0.5.
3. **`CombinesSecurities`** — SPY (+200) + option (+50) together → cumulative by date sums both.
4. **`OrdersByDate`** — trades supplied out of chronological order still produce a correct date-ordered curve.

### 4.3 WPF sample

`04_Chart.PnlCurve.csproj` (mirrors `03_Performance`):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\..\common_samples_netwindows.props" />
  <ItemGroup>
    <PackageReference Include="StockSharp.Xaml.Charting" Version="$(StockSharpVer)" />
  </ItemGroup>
</Project>
```

`MainWindow.xaml` — plain WPF `Window` (no DevExpress chrome), two columns:
```xml
<Window x:Class="StockSharp.Samples.Chart.PnlCurve.MainWindow" ... Title="PnL Curve" Height="600" Width="1000">
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="260"/>
      <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>

    <DockPanel Grid.Column="0" Margin="5">
      <Button x:Name="RefreshButton" DockPanel.Dock="Top" Content="Reload" Click="OnRefreshClick"/>
      <TreeView x:Name="SecuritiesTree" ScrollViewer.VerticalScrollBarVisibility="Auto"/>
    </DockPanel>

    <charting:ChartPanel x:Name="Chart" Grid.Column="1" IsInteracted="True"/>
  </Grid>
</Window>
```
(`xmlns:charting="http://schemas.stocksharp.com/xaml"`)

`MainWindow.xaml.cs` — key flow:
1. **Load securities**: `var drive = new LocalMarketDataDrive(Paths.FileSystem, Paths.HistoryDataPath);` → `await foreach (var secId in drive.GetAvailableSecuritiesAsync()...)`. Skip ids without a code.
2. **Group**: option detection regex (matches the importer's normalized code `ROOT YYMMDD[C/P]STRIKE`, e.g. `SPY 250117C500`) → options grouped under their root; everything else is an underlying. Build `TreeViewItem` per underlying with child `TreeViewItem` per option; each item hosts a `CheckBox` (tag = `SecurityId`). Checking an underlying also checks its children.
3. **On selection change** → rebuild chart:
   ```csharp
   private void RebuildChart(IEnumerable<SecurityId> selected)
   {
       Chart.ClearAreas();
       var area = Chart.AddArea();
       area.YAxises.First().AutoRange = true;

       foreach (var secId in selected)
       {
           var line = area.Chart.CreateLineElement();
           line.FullTitle = secId.SecurityCode;
           line.Color = NextColor();
           line.StrokeThickness = 2;
           area.Chart.AddElement(area, line);

           var storage = _storage.GetTransactionStorage(secId);
           var trades = storage.LoadAsync(null, null).OfType<ExecutionMessage>()
               .Where(m => m.HasTradeInfo()).ToArray(); // buffered for reuse
           var curve = PnlCurveCalculator.BuildCumulativeByDate(trades, sid => IsOption(sid) ? 100m : 1m);

           var data = area.Chart.CreateData();
           foreach (var point in curve)
               data.Group(point.Date).Add(line, (double)point.CumulativePnL);
           area.Chart.Draw(data);
       }
   }
   ```
   A last "selected total" line sums the per-date curves of all checked securities.
4. `IsOption(SecurityId)` — regex match on `SecurityCode`; options → multiplier 100.

`App.xaml`/`App.xaml.cs`/`Properties/AssemblyInfo.cs` — copied from `03_Performance` (namespace `StockSharp.Samples.Chart.PnlCurve`).

## 5. Boundary Conditions and Exception Handling

- **No data / empty storage**: the tree shows an empty list and a message; no chart crash.
- **Security without trades**: skip (no line).
- **Code convention mismatch** (security imported by a non-IB source): treated as a non-option underlying, multiplier 1 — never fails.
- **Non-trade rows** (dividends/fees): filtered out via `HasTradeInfo()`.
- **Out-of-order / multi-day trades**: `BuildCumulativeByDate` sorts by `ServerTime` and groups by `Date`.
- **Very large histories**: curve built in a single pass; `PnLQueue` is O(1) amortized per trade. Loading uses the storage's async iterator.
- **Multiple underlyings** (e.g. SPY and QQQ): all listed; the user checks whichever to view. Options always attach to their parsed root.
- **Unrealized PnL**: out of scope — the chart shows **realized** PnL (matches the imported fills). Note this in the window title/legend.

## 6. Data Flow

```
06_ImportIbTrades sample (existing)  ── writes ──▶  LocalMarketDataDrive(Paths.HistoryDataPath)
                                                          per-SecurityId transaction storage
                                                                   │
04_PnlCurve viewer                                                ▼
  LocalMarketDataDrive.GetAvailableSecuritiesAsync()  ──▶  SecurityId list
  group underlying + options (code convention)  ──▶  checkbox TreeView (user selection)
  StorageRegistry.GetTransactionStorage(secId).LoadAsync()  ──▶  ExecutionMessage[]
  PnlCurveCalculator.BuildCumulativeByDate(trades, multiplier)  ──▶  (date, cumulative PnL) points
  ChartPanel: data.Group(date).Add(lineElement, (double)pnl)  ──▶  line per selected security
  (X = date, Y = cumulative PnL)
```

## 7. Expected Outcomes

- The user opens the viewer, sees all imported securities (SPY and each SPY option series) in a checkbox tree.
- Checking a security draws its cumulative-PnL-by-date line (X = date, Y = PnL); checking an underlying toggles its options; a combined "total" line summarizes the selection.
- PnL math is verified by unit tests (SPY round-trip, option ×100 multiplier, multi-security aggregation, date ordering).
- The sample builds offline (`StockSharp.Xaml.Charting` from NuGet, `EnableWindowsTargeting` on macOS for verification) and runs inside the user's Windows VM / any Windows machine in Rider.
- All changes are additive; no existing behavior is altered.
