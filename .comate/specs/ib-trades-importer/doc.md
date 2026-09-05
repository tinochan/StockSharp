# IB Trades Importer (`ib-trades-importer`)

## 1. Requirement

The user trades through Interactive Brokers (SPY ETF and SPY options) and wants to load the broker trade history into StockSharp to compute per-instrument PnL over time.

Interactive Brokers exports a **"Transaction History" CSV** with this header:

```
Transaction History,Header,Date,Account,Description,Transaction Type,Symbol,Quantity,Price,Price Currency,Gross Amount,Commission,Net Amount
```

- The first cells (`Transaction History,Header,…`) are a title prefix on the header row.
- Each data row holds one filled trade: date, account, description, buy/sell, symbol (for options it is the padded IB symbol, e.g. `SPY   250117C00500000`), quantity, price, currency, gross amount, commission, net amount.

StockSharp currently has only a **generic** CSV importer (`CsvImporter`/`CsvParser` + `FieldMappingRegistry`) that requires hand-mapping every IB column to StockSharp fields — there is **no IB-specific parser** (no `flex`, no IB column mapping in `Algo.Import`). The user must manually map columns every time.

**Goal:** add a dedicated, reusable **IB Transaction History CSV importer** to `Algo.Import` that parses this exact format into StockSharp transaction `ExecutionMessage`s and writes them into per-security storage, so they can later be fed to the PnL engine / report generators (per-instrument PnL for SPY ETF + options). No manual column mapping.

## 2. Architecture and Technical Approach

Two new classes in the `StockSharp.Algo.Import` namespace (project `Algo/Algo.Import/Algo.Import.csproj`), mirroring the existing `CsvParser`/`CsvImporter` split but **header-name-driven** instead of field-order-driven:

1. **`IbTradesCsvParser`** — parses the IB CSV stream into an `IAsyncEnumerable<ExecutionMessage>`.
   - Locates the header row by column names (skips title rows such as `Transaction History,Header,…`), maps column **names → indices** case-insensitively. Robust to IB layout variations (title line + header line, or combined single header line).
   - For each data row builds a `DataType.Transactions` `ExecutionMessage` and, for option symbols, also emits a `SecurityMessage` (option metadata: `SecurityType=Option`, `Strike`, `ExpiryDate`, `Multiplier=100`, underlying code) so security metadata is stored correctly.
   - Converts IB option symbols (`SPY   250117C00500000`) into unique readable StockSharp codes (`SPY 250117C500`) — required so that SPY and each option series get **separate `SecurityId`s** (PnL grouping key).
2. **`IbTradesImporter`** — wraps the parser and writes parsed messages into per-security storage, mirroring `CsvImporter.Import`/`FlushBuffer`/`SaveSecurity`:
   - `SecurityMessage` → upsert into `ISecurityStorage` (skip duplicates).
   - `ExecutionMessage` → group by `SecurityId`, order by `ServerTime`, flush into per-security transaction storage via `Func<SecurityId, IMarketDataStorage>`.
   - Same `(count, lastTime)` return and progress callback contract as `CsvImporter`.

Reuse decisions:
- Do **not** retrofit `CsvImporter`/`FieldMappingRegistry` (their parsing is field-order-based and would need invasive changes). Standalone classes keep existing behavior untouched.
- The importer uses the same storage wiring (`ISecurityStorage`, `IExchangeInfoProvider`, `getStorage`) as `CsvImporter` so it plugs into the same test/memory-fs harness and the same local storage in samples.

Deliverables:
- Parser + importer in `Algo.Import`.
- Unit tests in the existing `Tests` project (mirroring `ImportTests.cs` style).
- A small console sample `Samples/03_Storage/06_ImportIbTrades/` demonstrating: import IB CSV → local storage → per-security realized PnL report. Registered in `StockSharp.slnx`.

## 3. Affected Files

| File | Type of change |
|---|---|
| `/Users/tino/GitHub/StockSharp/Algo.Import/IbTradesCsvParser.cs` | **New** — IB CSV → `ExecutionMessage`/`SecurityMessage` parser |
| `/Users/tino/GitHub/StockSharp/Algo.Import/IbTradesImporter.cs` | **New** — parser + storage flush (mirrors `CsvImporter`) |
| `/Users/tino/GitHub/StockSharp/Tests/IbTradesImporterTests.cs` | **New** — parse + import tests |
| `/Users/tino/GitHub/StockSharp/Samples/03_Storage/06_ImportIbTrades/06_Storage.ImportIbTrades.csproj` | **New** — console sample project |
| `/Users/tino/GitHub/StockSharp/Samples/03_Storage/06_ImportIbTrades/Program.cs` | **New** — import → per-security PnL demo |
| `/Users/tino/GitHub/StockSharp/Samples/03_Storage/06_ImportIbTrades/sample_ib_trades.csv` | **New** — sample IB CSV (exact user header) |
| `/Users/tino/GitHub/StockSharp/StockSharp.slnx` | **Mod** — register `06_ImportIbTrades.csproj` under `/Samples/03_Storage/` |

No existing code is modified (importer/parser are additive). `Algo.Import` is already referenced by both `StockSharp.slnx` (line 449) and `StockSharp_Tests.slnx` (line 7), so no solution changes for the library or tests.

## 4. Implementation Details

### 4.1 `IbTradesCsvParser`

Namespace `StockSharp.Algo.Import`. Mirrors `CsvParser` style (tab indentation, `CsvFileReader`, local async iterator with `[EnumeratorCancellation]`).

```csharp
namespace StockSharp.Algo.Import;

using System.Runtime.CompilerServices;

/// <summary>
/// Parser of Interactive Brokers "Transaction History" CSV file into StockSharp transaction messages.
/// </summary>
public class IbTradesCsvParser
{
	private const string _dateColumn = "Date";
	private const string _accountColumn = "Account";
	private const string _transactionTypeColumn = "Transaction Type";
	private const string _symbolColumn = "Symbol";
	private const string _quantityColumn = "Quantity";
	private const string _priceColumn = "Price";
	private const string _currencyColumn = "Price Currency";
	private const string _commissionColumn = "Commission";

	private static readonly Regex _optionSymbolRegex = new(
		@"^\s*(?<root>[A-Za-z0-9]+?)\s*(?<exp>\d{6})(?<cp>[CP])(?<strike>\d+)\s*$",
		RegexOptions.Compiled | RegexOptions.IgnoreCase);

	private string _columnSeparator = ",";

	/// <summary>Column separator.</summary>
	public string ColumnSeparator
	{
		get => _columnSeparator;
		set => _columnSeparator = value.ThrowIfEmpty(nameof(value));
	}

	private string _lineSeparator = StringHelper.RN;

	/// <summary>Line separator.</summary>
	public string LineSeparator
	{
		get => _lineSeparator;
		set => _lineSeparator = value.ThrowIfEmpty(nameof(value));
	}

	/// <summary>Board code assigned to imported securities. Defaults to SMART (IB routing).</summary>
	public string BoardCode { get; set; } = "SMART";

	/// <summary>First trade id used for imported trades. Defaults to 1.</summary>
	public long TradeIdStart { get; set; } = 1;

	/// <summary>Parse the stream into messages.</summary>
	public IAsyncEnumerable<Message> Parse(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);

		return Impl(this, stream);

		static async IAsyncEnumerable<Message> Impl(IbTradesCsvParser parser, Stream stream,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			using var reader = new CsvFileReader(stream, parser.LineSeparator) { Delimiter = parser.ColumnSeparator[0] };
			var cells = new List<string>();
			var columns = default(Dictionary<string, int>);
			var tradeId = parser.TradeIdStart;
			var optionSecIds = new HashSet<SecurityId>();
			var dateFormats = new[] { "yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy" };

			while (await reader.ReadRowAsync(cells, cancellationToken))
			{
				if (cells.IsEmpty())
					continue;

				// Skip title rows until a header row containing Date + Symbol is found.
				if (columns == null)
				{
					columns = TryReadHeader(cells);
					continue;
				}

				if (columns.Count == 0 || cells.Count < columns.Count)
					continue; // malformed or trailing row

				string Get(string name) => columns.TryGetValue(name, out var i) && i < cells.Count ? cells[i].Trim() : null;

				// --- Side ---
				var type = Get(_transactionTypeColumn);
				var side = type?.ToLowerInvariant() switch
				{
					"buy" or "bought" or "bot" => Sides.Buy,
					"sell" or "sold" or "sld" => Sides.Sell,
					_ => (Sides?)null,
				};
				if (side == null)
					continue; // non-trade rows (dividends, fees, etc.)

				// --- Security ---
				var symbol = Get(_symbolColumn);
				if (symbol.IsEmpty())
					continue;

				var secId = new SecurityId
				{
					SecurityCode = symbol,
					BoardCode = parser.BoardCode,
				};

				SecurityMessage secMsg = null;
				var match = _optionSymbolRegex.Match(symbol);

				if (match.Success)
				{
					var root = match.Groups["root"].Value;
					var exp = match.Groups["exp"].Value;
					var cp = match.Groups["cp"].Value.ToUpperInvariant();
					var strike = match.Groups["strike"].Value.To<decimal>() / 1000m;

					secId.SecurityCode = $"{root} {exp}{cp}{strike.ToString(CultureInfo.InvariantCulture)}";
					secId.BoardCode = parser.BoardCode;

					if (optionSecIds.Add(secId))
					{
						secMsg = new SecurityMessage
						{
							SecurityId = secId,
							SecurityType = SecurityTypes.Option,
							OptionType = cp == "C" ? OptionTypes.Call : OptionTypes.Put,
							Strike = strike,
							ExpiryDate = DateTimeOffset.ParseExact(exp, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
							Multiplier = 100,
							UnderlyingSecurityCode = root,
							Currency = Get(_currencyColumn)?.To<CurrencyTypes>(),
						};
						yield return secMsg;
					}
				}
				else
				{
					// Plain security (e.g. ETF/stock).
					secId.SecurityCode = symbol;
					secId.BoardCode = parser.BoardCode;
				}

				// --- ExecutionMessage (trade) ---
				var date = Get(_dateColumn);
				if (date.IsEmpty())
					continue;

				var time = date.To<DateTimeOffset>(dateFormats);
				if (time == default)
					continue;

				var price = Get(_priceColumn).To<decimal?>();
				var volume = Get(_quantityColumn).To<decimal?>();
				if (price == null || volume == null)
					continue;

				var execMsg = new ExecutionMessage
				{
					DataTypeEx = DataType.Transactions,
					SecurityId = secId,
					ServerTime = time,
					PortfolioName = Get(_accountColumn),
					Side = side,
					TransactionId = tradeId,
					OrderId = tradeId,
					OrderPrice = price.Value,
					OrderVolume = volume.Value,
					Balance = 0,
					OrderType = OrderTypes.Market,
					OrderState = OrderStates.Done,
					TradeId = tradeId,
					TradePrice = price.Value,
					TradeVolume = volume.Value,
					Currency = Get(_currencyColumn)?.To<CurrencyTypes>(),
					HasOrderInfo = true,
				};

				var commission = Get(_commissionColumn).To<decimal?>();
				if (commission != null)
					execMsg.Commission = commission;

				tradeId++;
				yield return execMsg;
			}
		}
	}

	private Dictionary<string, int> TryReadHeader(List<string> cells)
	{
		var idx = cells.IndexOf(_dateColumn, StringComparer.InvariantCultureIgnoreCase);

		if (idx < 0 || cells.IndexOf(_symbolColumn, StringComparer.InvariantCultureIgnoreCase) < 0)
			return new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase); // not a header

		return cells
			.Skip(idx)
			.Select((c, i) => (c, i))
			.ToDictionary(p => p.c, p => p.i + idx, StringComparer.InvariantCultureIgnoreCase);
	}
}
```

Notes on the snippet (final code may adjust to compile against the exact helper APIs):
- `cells.IndexOf(string, StringComparer)` — if not available in the `List<string>` used here, use `cells.FindIndex(c => c.EqualsIgnoreCase(name))`.
- `To<DateTimeOffset>(dateFormats)` — if no such helper exists, parse manually: `DateTimeOffset.TryParseExact(date, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)`.
- `StringHelper.RN`, `CsvFileReader`, `IsEmpty()`, `ThrowIfEmpty`, `To<T>()`, `EqualsIgnoreCase` come from `Ecng.Common` (same as `CsvParser`).
- `ExecutionMessage.OrderState = OrderStates.Done` (filled) — must be a valid enum value used by StockSharp (`Done`).
- `OrderStates`/`OrderTypes`/`OptionTypes`/`SecurityTypes`/`Sides`/`CurrencyTypes` are in `StockSharp.Messages`.
- The header `Dictionary` keys use the IB column names verbatim (`"Transaction Type"`, `"Price Currency"`), matched case-insensitively.

### 4.2 `IbTradesImporter`

Mirrors `CsvImporter` (`Import` loop + `FlushBuffer` + `SaveSecurity`), but messages come from `IbTradesCsvParser`:

```csharp
namespace StockSharp.Algo.Import;

/// <summary>
/// Importer of Interactive Brokers "Transaction History" CSV file into storage.
/// </summary>
public class IbTradesImporter : BaseLogReceiver
{
	private readonly IbTradesCsvParser _parser;
	private readonly ISecurityStorage _securityStorage;
	private readonly IExchangeInfoProvider _exchangeInfoProvider;
	private readonly Func<SecurityId, IMarketDataStorage> _getStorage;

	public IbTradesImporter(IbTradesCsvParser parser, ISecurityStorage securityStorage,
		IExchangeInfoProvider exchangeInfoProvider, Func<SecurityId, IMarketDataStorage> getStorage)
	{
		_parser = parser ?? throw new ArgumentNullException(nameof(parser));
		_securityStorage = securityStorage ?? throw new ArgumentNullException(nameof(securityStorage));
		_exchangeInfoProvider = exchangeInfoProvider ?? throw new ArgumentNullException(nameof(exchangeInfoProvider));
		_getStorage = getStorage ?? throw new ArgumentNullException(nameof(getStorage));
	}

	/// <summary>Security updated event.</summary>
	public event Action<Security, bool> SecurityUpdated;

	public async ValueTask<(int count, DateTime? lastTime)> Import(Stream stream, Action<int> updateProgress, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(updateProgress);

		var count = 0;
		var lastTime = default(DateTime?);
		var buffer = new List<Message>();

		async ValueTask Flush()
		{
			count += buffer.Count;
			if (buffer.LastOrDefault() is IServerTimeMessage timeMsg)
				lastTime = timeMsg.ServerTime;
			await FlushBuffer(buffer, cancellationToken);
		}

		var canProgress = stream.CanSeek;
		var len = canProgress ? stream.Length : -1;
		var prevPercent = 0;

		await foreach (var msg in _parser.Parse(stream).WithCancellation(cancellationToken))
		{
			if (msg is SecurityMessage secMsg)
			{
				// Upsert security metadata (e.g. options with multiplier 100).
				var security = await _securityStorage.LookupByIdAsync(secMsg.SecurityId, cancellationToken);

				if (security == null)
					security = secMsg.ToSecurity(_exchangeInfoProvider);
				else
					security.ApplyChanges(secMsg, _exchangeInfoProvider, true);

				await _securityStorage.SaveAsync(security, true, cancellationToken);
				SecurityUpdated?.Invoke(security, false);
			}
			else
			{
				buffer.Add(msg);

				if (buffer.Count > 1000)
					await Flush();
			}

			if (!canProgress)
				continue;

			var percent = (int)(((double)stream.Position / len) * 100).Round();
			if (percent <= prevPercent)
				continue;

			prevPercent = percent;
			updateProgress(prevPercent);
		}

		if (buffer.Count > 0)
			await Flush();

		if (canProgress && prevPercent < 100)
			updateProgress(100);

		return (count, lastTime);
	}

	// FlushBuffer + SaveSecurity copied from CsvImporter (group by SecurityId, order by ServerTime,
	// create missing securities) — see CsvImporter.cs:136-190.
}
```

`count` counts **trade rows only** (the `SecurityMessage` enrichment is not counted), so `Import` returns the number of imported trades — matching user expectation.

### 4.3 Tests — `Tests/IbTradesImporterTests.cs`

New test class in the existing `Tests` project, mirroring `ImportTests` (uses `Helper.MemorySystem`, `fs.GetStorage(...)`, `ServicesRegistry.SecurityStorage`, `ServicesRegistry.ExchangeInfoProvider`).

Sample CSV (exact user header), including a title row, ETF rows, an option row, and a non-trade row:

```csv
Transaction History,Header,Date,Account,Description,Transaction Type,Symbol,Quantity,Price,Price Currency,Gross Amount,Commission,Net Amount
2024-01-10,DU1234567,"BOT 100 SPY @ 480.00",Buy,SPY,100,480.00,USD,48000.00,-1.00,47999.00
2024-01-10,DU1234567,"SLD 100 SPY @ 482.00",Sell,SPY,100,482.00,USD,48200.00,-1.00,48199.00
2024-01-11,DU1234567,"BOT 1 SPY 250117C00500000 @ 2.50",Buy,SPY   250117C00500000,1,2.50,USD,250.00,-0.65,249.35
2024-01-12,DU1234567,Dividends,Other,SPY,0,0.00,USD,0.00,0.00,10.00
```

Tests:
1. **`Parse_ReadsHeaderAndRows`** — `Parse` yields 3 messages: 2 `ExecutionMessage` (SPY) + 1 `SecurityMessage` + 1 `ExecutionMessage` (option) in the correct order. The option `SecurityCode` equals `SPY 250117C500`, `Multiplier` = 100 on the `SecurityMessage`, `Side`/`TradePrice`/`TradeVolume`/`PortfolioName`/`Currency`/`Commission` verified; the non-trade row (`Transaction Type=Other`) is skipped.
2. **`Import_WritesPerSecurity`** — `Import` returns `count == 3`, `lastTime == 2024-01-11`. Then load transactions per security from storage and assert SPY and the option have separate storages with the expected number of trades.
3. **`Parse_HandlesLeadingTitleRows`** — a file where the header is preceded by an extra title line still parses correctly.

### 4.4 Sample — `Samples/03_Storage/06_ImportIbTrades/`

Console app (`common_samples_net.props`, `OutputType=Exe`) that:
1. Takes the IB CSV path (arg) or uses bundled `sample_ib_trades.csv`.
2. Creates `LocalMarketDataDrive(Paths.FileSystem, Paths.HistoryDataPath)` + `StorageRegistry { DefaultDrive = ... }`.
3. Runs `IbTradesImporter.Import(...)` with `getStorage = secId => storage.GetStorage(secId, DataType.Transactions)`.
4. Loads per-security transaction storages, computes **per-instrument realized PnL** with a `PnLQueue` per `SecurityId` (applying `LotMultiplier`/`PriceStep` read from the stored `Security` when present), and prints a table:

```
Security         Trades  RealizedPnL
SPY              2       200.00
SPY 250117C500   1       -250.00
```

`csproj` adds `<ProjectReference Include="..\..\..\Algo.Import\Algo.Import.csproj" />` (plus the existing `common_samples.props` references to `Algo.Indicators`/`Algo.Strategies` which pull in `Algo`).

## 5. Boundary Conditions and Exception Handling

- **Non-trade rows** (dividends, fees, interest — `Transaction Type` other than Buy/Sell): skipped (not imported, not counted).
- **Unknown/empty symbol, missing date, missing price/quantity**: row skipped (defensive `continue`), no exception.
- **Malformed numeric cells**: rely on `To<decimal>()` conversion; on failure the row is skipped (wrap per-row in try/catch, log via `BaseLogReceiver.LogError`, do not abort the whole import).
- **Leading/trailing/title rows** (`Transaction History,Header,…`): handled by header-name detection; rows before the header are skipped; short/trailing rows ignored.
- **Duplicate securities** (re-import): `SecurityMessage` upsert uses `ApplyChanges` with `UpdateDuplicateSecurities=true`; trades are appended to existing storage.
- **Option code parsing**: only symbols matching the IB option pattern are rewritten; everything else (ETF/stock/futures) is used as-is. Strikes are divided by 1000 (IB formats strike×1000).
- **Commission sign**: stored as-is from the CSV. PnL computation treats commission separately (out of scope for the importer).
- **Performance**: parser is streaming (`IAsyncEnumerable`); storage flush batches 1000 messages, mirroring `CsvImporter`.
- **`IB` date formats**: `yyyy-MM-dd`, `yyyyMMdd`, `MM/dd/yyyy` attempted before falling back to invariant `DateTime` parse.

## 6. Data Flow

```
IB Client Portal "Transaction History" CSV
   │
   ▼
IbTradesCsvParser.Parse(Stream)
   │  header-name → column index mapping, option symbol normalization
   ├─ SecurityMessage (options: type=Option, strike, expiry, multiplier=100, underlying)
   └─ ExecutionMessage (DataType.Transactions: secId, time, side, price, qty, portfolio, currency, commission)
   │
   ▼
IbTradesImporter.Import(...)
   │  SecurityMessage → ISecurityStorage (upsert)
   │  ExecutionMessage → group by SecurityId, sort by ServerTime, flush batches
   ▼
per-security IMarketDataStorage<ExecutionMessage>   (storage.GetStorage(secId, DataType.Transactions))
   │
   ▼
(consumer) PnLQueue per SecurityId / PnLManager / ExcelReportGenerator
   → per-instrument realized PnL, equity curve over time (SPY + each option series)
```

## 7. Expected Outcomes

- A single `IbTradesImporter` call imports an IB "Transaction History" CSV into per-security storage with **no manual column mapping**.
- SPY ETF and each SPY option series land in **separate `SecurityId` storages** (correct basis for per-instrument PnL over time).
- Options get stored security metadata (`SecurityType=Option`, `Strike`, `ExpiryDate`, `Multiplier=100`) enabling correct PnL scaling.
- Unit tests verify parsing (header detection, option code conversion, side/price/qty/commission mapping, non-trade-row skip) and the storage write path.
- A runnable sample demonstrates the full pipeline: CSV → local storage → per-instrument realized PnL table.
- All changes are additive; no existing `Algo.Import` behavior or tests are altered.
