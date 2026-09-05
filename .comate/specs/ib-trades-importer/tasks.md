# IB Trades Importer Implementation Plan

- [x] Task 1: Create `IbTradesCsvParser` in `Algo.Import`
    - 1.1: Add `Algo.Import/IbTradesCsvParser.cs` with configurable `ColumnSeparator`, `LineSeparator`, `BoardCode` (default `SMART`), `TradeIdStart`
    - 1.2: Implement header-name detection (`TryReadHeader`) that skips title rows and maps IB column names (Date, Account, Transaction Type, Symbol, Quantity, Price, Price Currency, Commission) to indices case-insensitively
    - 1.3: Implement the streaming `Parse(Stream)` async iterator using `CsvFileReader`, producing `Message`s
    - 1.4: Build `ExecutionMessage` (DataType.Transactions) per data row: ServerTime, PortfolioName, Side (Buy/Sell/BOT/SLD), SecurityId, TradePrice/TradeVolume, OrderPrice/OrderVolume/Balance, OrderType=Market, OrderState=Done, TradeId (sequential), Currency, Commission; skip non-trade rows
    - 1.5: Add IB option symbol normalization (`SPY   250117C00500000` → `SPY 250117C500`) and emit a `SecurityMessage` (Option, strike, expiry, Multiplier=100, underlying) once per option security
    - 1.6: Handle date parsing (yyyy-MM-dd / yyyyMMdd / MM/dd/yyyy) and defensive row skipping on malformed data

- [x] Task 2: Create `IbTradesImporter` in `Algo.Import`
    - 2.1: Add `Algo.Import/IbTradesImporter.cs` (BaseLogReceiver) with `IbTradesCsvParser`, `ISecurityStorage`, `IExchangeInfoProvider`, `Func<SecurityId, IMarketDataStorage>` dependencies and `SecurityUpdated` event
    - 2.2: Implement `Import(Stream, Action<int>, CancellationToken)` returning `(count, lastTime)` — iterate `_parser.Parse`, upsert `SecurityMessage`s into `ISecurityStorage`, buffer `ExecutionMessage`s and flush every 1000
    - 2.3: Implement `FlushBuffer`/`SaveSecurity` (group by SecurityId, order by ServerTime, create missing securities, save via `_getStorage`) mirroring `CsvImporter`
    - 2.4: Add progress reporting (percent of stream position) and count only trade rows

- [x] Task 3: Add unit tests in `Tests/IbTradesImporterTests.cs`
    - 3.1: Create the test file with an embedded sample IB CSV (exact user header, title row, ETF rows, option row, non-trade row)
    - 3.2: Test `Parse` — correct message count/order, option code conversion, side/price/qty/portfolio/currency/commission mapping, non-trade-row skip
    - 3.3: Test `Import` — returned count and lastTime, per-SecurityId storage separation (SPY vs SPY 250117C500), trades persisted
    - 3.4: Test header detection with an extra leading title row

- [x] Task 4: Create console sample `Samples/03_Storage/06_ImportIbTrades/`
    - 4.1: Add `06_Storage.ImportIbTrades.csproj` (common_samples_net.props, ProjectReference to `Algo.Import`)
    - 4.2: Add `Program.cs` — import IB CSV into `LocalMarketDataDrive`/`StorageRegistry`, load per-security transactions, compute per-instrument realized PnL with `PnLQueue` (applying stored `LotMultiplier`/`PriceStep`), print results table
    - 4.3: Add `sample_ib_trades.csv` fixture with the exact user header

- [x] Task 5: Register the sample in `StockSharp.slnx`
    - 5.1: Add `06_Storage.ImportIbTrades.csproj` under the `/Samples/03_Storage/` folder

- [x] Task 6: Build and run tests
    - 6.1: Build `Algo.Import` and the sample project to confirm compilation
    - 6.2: Run `IbTradesImporterTests` and the existing `ImportTests` to confirm nothing regressed
