namespace StockSharp.Algo.Import;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Ecng.Logging;

/// <summary>
/// Parser of Interactive Brokers "Transaction History" CSV file into StockSharp transaction messages.
/// </summary>
/// <remarks>
/// The IB CSV has a header row that starts with a title prefix:
/// <code>Transaction History,Header,Date,Account,Description,Transaction Type,Symbol,Quantity,Price,Price Currency,Gross Amount,Commission,Net Amount</code>
/// The parser locates the header by column names, so leading title rows and layout variations are ignored.
/// </remarks>
public class IbTradesCsvParser : BaseLogReceiver
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
		RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private static readonly string[] _dateFormats = ["yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy"];

	private string _columnSeparator = ",";

	/// <summary>
	/// Column separator.
	/// </summary>
	public string ColumnSeparator
	{
		get => _columnSeparator;
		set => _columnSeparator = value.ThrowIfEmpty(nameof(value));
	}

	/// <summary>
	/// Board code assigned to imported securities. Defaults to <see cref="SecurityId.AssociatedBoardCode"/> value ("SMART").
	/// </summary>
	public string BoardCode { get; set; } = SecurityId.AssociatedBoardCode;

	/// <summary>
	/// First trade id used for imported trades.
	/// </summary>
	public long TradeIdStart { get; set; } = 1;

	/// <summary>
	/// Parse the stream into StockSharp messages.
	/// </summary>
	/// <param name="stream">The file stream.</param>
	/// <returns>Parsed <see cref="SecurityMessage"/> and <see cref="ExecutionMessage"/> instances.</returns>
	public IAsyncEnumerable<Message> Parse(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);

		return Impl(this, stream);

		static async IAsyncEnumerable<Message> Impl(IbTradesCsvParser parser, Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			// Normalize CRLF / CR into LF so files downloaded on Windows (CRLF) and edited on macOS/Linux (LF) both parse.
			using var reader = new CsvFileReader(new NormalizedStream(stream), "\n") { Delimiter = parser.ColumnSeparator[0] };

			var cells = new List<string>();
			var columns = default(Dictionary<string, int>);
			var tradeId = parser.TradeIdStart;
			var optionSecIds = new HashSet<SecurityId>();
			var lineIndex = 0;

			while (await reader.ReadRowAsync(cells, cancellationToken))
			{
				lineIndex++;

				if (cells.IsEmpty())
					continue;

				// Skip title rows (e.g. "Transaction History,Header,...") until the header row is found.
				if (columns == null)
				{
					columns = TryReadHeader(cells);
					continue;
				}

				Message[] row;

				try
				{
					row = ParseRow(parser, columns, cells, optionSecIds, ref tradeId);
				}
				catch (Exception ex)
				{
					parser.LogError(LocalizedStrings.CsvImportError.Put(lineIndex, null, string.Join(",", cells), nameof(IbTradesCsvParser)), ex);
					continue;
				}

				foreach (var msg in row)
					yield return msg;
			}
		}
	}

	private static Message[] ParseRow(IbTradesCsvParser parser, Dictionary<string, int> columns, List<string> cells, HashSet<SecurityId> optionSecIds, ref long tradeId)
	{
		string Get(string name)
			=> columns.TryGetValue(name, out var i) && i < cells.Count ? cells[i].Trim() : null;

		// Only trade rows (Buy/Sell) are imported; dividends, fees, interest, etc. are skipped.
		var type = Get(_transactionTypeColumn);
		var side = type?.ToLowerInvariant() switch
		{
			"buy" or "bought" or "bot" => Sides.Buy,
			"sell" or "sold" or "sld" => Sides.Sell,
			_ => (Sides?)null,
		};

		if (side == null)
			return [];

		var symbol = Get(_symbolColumn);

		if (symbol.IsEmpty())
			return [];

		var secId = new SecurityId
		{
			SecurityCode = symbol,
			BoardCode = parser.BoardCode,
		};

		var messages = new List<Message>();

		// Normalize IB option symbols ("SPY   250117C00500000") into a unique readable
		// StockSharp code ("SPY 250117C500") so each option series gets its own SecurityId.
		var match = _optionSymbolRegex.Match(symbol);

		if (match.Success)
		{
			var root = match.Groups["root"].Value.ToUpperInvariant();
			var exp = match.Groups["exp"].Value;
			var cp = match.Groups["cp"].Value.ToUpperInvariant();
			var strike = match.Groups["strike"].Value.To<decimal>() / 1000m;

			secId.SecurityCode = $"{root} {exp}{cp}{strike.ToString(CultureInfo.InvariantCulture)}";
			secId.BoardCode = parser.BoardCode;

			if (optionSecIds.Add(secId))
			{
				messages.Add(new SecurityMessage
				{
					SecurityId = secId,
					SecurityType = SecurityTypes.Option,
					OptionType = cp == "C" ? OptionTypes.Call : OptionTypes.Put,
					Strike = strike,
					ExpiryDate = DateTime.ParseExact(exp, "yyMMdd", CultureInfo.InvariantCulture),
					Multiplier = 100,
					UnderlyingSecurityId = new SecurityId { SecurityCode = root, BoardCode = parser.BoardCode },
					Currency = Get(_currencyColumn)?.To<CurrencyTypes>(),
				});
			}
		}

		var date = Get(_dateColumn);

		if (date.IsEmpty())
			return [];

		var time = ParseDate(date).UtcKind();

		var price = Get(_priceColumn).To<decimal?>();
		var volume = Get(_quantityColumn).To<decimal?>();

		if (price == null || volume == null)
			return [];

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
		messages.Add(execMsg);

		return [.. messages];
	}

	private static Dictionary<string, int> TryReadHeader(List<string> cells)
	{
		var dateIdx = -1;
		var symbolIdx = -1;

		for (var i = 0; i < cells.Count; i++)
		{
			var cell = cells[i].Trim();

			if (dateIdx < 0 && cell.EqualsIgnoreCase(_dateColumn))
				dateIdx = i;

			if (symbolIdx < 0 && cell.EqualsIgnoreCase(_symbolColumn))
				symbolIdx = i;
		}

		if (dateIdx < 0 || symbolIdx < 0)
			return null;

		var columns = new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase);

		// Store indices relative to the Date column: the header row may carry a title prefix
		// ("Transaction History,Header,...") that the data rows do not, so absolute indices
		// would be shifted. Relative indices align with the data rows.
		for (var i = dateIdx; i < cells.Count; i++)
		{
			var name = cells[i].Trim();

			if (name.IsEmpty())
				continue;

			columns[name] = i - dateIdx;
		}

		return columns;
	}

	private static DateTime ParseDate(string value)
	{
		if (DateTime.TryParseExact(value, _dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
			return date;

		if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
			return date;

		throw new FormatException($"{LocalizedStrings.InvalidValue} '{value}'.");
	}

	private sealed class NormalizedStream : Stream
	{
		private readonly Stream _inner;
		private int _pushBack = -1;

		public NormalizedStream(Stream inner)
		{
			_inner = inner ?? throw new ArgumentNullException(nameof(inner));
		}

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => _inner.Length;
		public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin)
			=> throw new NotSupportedException();

		public override void SetLength(long value)
			=> throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count)
			=> throw new NotSupportedException();

		public override int Read(byte[] buffer, int offset, int count)
		{
			if (count == 0)
				return 0;

			var total = 0;

			while (total < count)
			{
				var b = _pushBack >= 0 ? _pushBack : _inner.ReadByte();
				_pushBack = -1;

				if (b < 0)
					break;

				if (b != '\r')
				{
					buffer[offset + total++] = (byte)b;
					continue;
				}

				// Convert \r\n and lone \r into \n.
				var next = _inner.ReadByte();
				buffer[offset + total++] = (byte)'\n';

				if (next >= 0 && next != '\n')
					_pushBack = next;
			}

			return total;
		}
	}
}
