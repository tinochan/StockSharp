# PnL Curve Viewer Implementation Plan

- [x] Task 1: Create `PnlCurveCalculator` in `Algo/PnL`
    - 1.1: Add `Algo/PnL/PnlCurveCalculator.cs` with `PnLCurvePoint` record struct (Date, CumulativePnL)
    - 1.2: Implement `BuildCumulativeByDate(IEnumerable<ExecutionMessage>, Func<SecurityId, decimal?> getMultiplier = null)`: FIFO-match per SecurityId via `PnLQueue`, apply `LotMultiplier` from `getMultiplier`, collect per-trade realized PnL, cumulative-sum grouped by `ServerTime.Date`, ordered by date

- [x] Task 2: Add unit tests `Tests/PnlCurveCalculatorTests.cs`
    - 2.1: `Spy_RoundTrip` — buy 100 @ 480 day1, sell 100 @ 482 day2 → (day1, 0), (day2, 200)
    - 2.2: `Option_Multiplier` — option buy @ 2.50 / sell @ 3.00 with getMultiplier=100 → 50; without → 0.5
    - 2.3: `CombinesSecurities` — SPY (+200) + option (+50) aggregated by date
    - 2.4: `OrdersByDate` — out-of-order trades produce a date-ordered curve

- [x] Task 3: Create the WPF sample `Samples/05_Chart/04_PnlCurve/`
    - 3.1: Add `04_Chart.PnlCurve.csproj` (common_samples_netwindows.props + `StockSharp.Xaml.Charting` package)
    - 3.2: Add `App.xaml` / `App.xaml.cs` / `Properties/AssemblyInfo.cs` (mirror 03_Performance, namespace `StockSharp.Samples.Chart.PnlCurve`)
    - 3.3: Add `MainWindow.xaml` — checkbox `TreeView` (left) + `ChartPanel` (right)
    - 3.4: Implement `MainWindow.xaml.cs` — enumerate SecurityIds from `LocalMarketDataDrive(Paths.FileSystem, Paths.HistoryDataPath)`, group underlying/options by code convention, load per-security transaction storages, draw one `IChartLineElement` per checked security (X=date, Y=cumulative PnL) plus a selected-total line

- [x] Task 4: Register the sample in `StockSharp.slnx`
    - 4.1: Add `04_Chart.PnlCurve.csproj` under `/Samples/05_Chart/`

- [x] Task 5: Build and run tests
    - 5.1: Build `Algo` (with `PnlCurveCalculator`)
    - 5.2: Run `PnlCurveCalculatorTests` (and confirm existing `ImportTests`/`IbTradesImporterTests` still pass)
    - 5.3: Build the WPF sample with `-p:EnableWindowsTargeting=true` (macOS verification; no flag needed in the Windows VM)
