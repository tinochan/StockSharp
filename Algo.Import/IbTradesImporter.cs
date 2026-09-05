namespace StockSharp.Algo.Import;

using Ecng.Logging;

/// <summary>
/// Importer of Interactive Brokers "Transaction History" CSV file into storage.
/// </summary>
public class IbTradesImporter : BaseLogReceiver
{
	private readonly IbTradesCsvParser _parser;
	private readonly ISecurityStorage _securityStorage;
	private readonly IExchangeInfoProvider _exchangeInfoProvider;
	private readonly Func<SecurityId, IMarketDataStorage> _getStorage;

	/// <summary>
	/// Initializes a new instance of the <see cref="IbTradesImporter"/>.
	/// </summary>
	/// <param name="parser">The IB CSV parser.</param>
	/// <param name="securityStorage">Securities meta info storage.</param>
	/// <param name="exchangeInfoProvider">Exchanges and trading boards provider.</param>
	/// <param name="getStorage">Function to get <see cref="IMarketDataStorage"/> by <see cref="SecurityId"/>.</param>
	public IbTradesImporter(IbTradesCsvParser parser, ISecurityStorage securityStorage, IExchangeInfoProvider exchangeInfoProvider, Func<SecurityId, IMarketDataStorage> getStorage)
	{
		_parser = parser ?? throw new ArgumentNullException(nameof(parser));
		_securityStorage = securityStorage ?? throw new ArgumentNullException(nameof(securityStorage));
		_exchangeInfoProvider = exchangeInfoProvider ?? throw new ArgumentNullException(nameof(exchangeInfoProvider));
		_getStorage = getStorage ?? throw new ArgumentNullException(nameof(getStorage));
	}

	/// <summary>
	/// Security updated event.
	/// </summary>
	public event Action<Security, bool> SecurityUpdated;

	/// <summary>
	/// Import from IB CSV file.
	/// </summary>
	/// <param name="stream">The file stream.</param>
	/// <param name="updateProgress">Progress notification.</param>
	/// <param name="cancellationToken"><see cref="CancellationToken"/></param>
	/// <returns>Count of imported trades and last trade time.</returns>
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
				var isNew = security == null;

				if (isNew)
					security = secMsg.ToSecurity(_exchangeInfoProvider);
				else
					security.ApplyChanges(secMsg, _exchangeInfoProvider, true);

				await _securityStorage.SaveAsync(security, true, cancellationToken);

				SecurityUpdated?.Invoke(security, isNew);
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

	private async ValueTask<SecurityId> SaveSecurity(SecurityId securityId, CancellationToken cancellationToken)
	{
		var security = await _securityStorage.LookupByIdAsync(securityId, cancellationToken);

		if (security is null)
		{
			if (securityId.BoardCode.IsEmpty())
				securityId.BoardCode = SecurityId.AssociatedBoardCode;

			security = new()
			{
				Id = securityId.ToStringId(),
				Code = securityId.SecurityCode,
				Board = _exchangeInfoProvider.GetOrCreateBoard(securityId.BoardCode),
			};

			await _securityStorage.SaveAsync(security, false, cancellationToken);
			LogInfo(LocalizedStrings.CreatingSec.Put(securityId));

			SecurityUpdated?.Invoke(security, true);
		}

		return securityId;
	}

	private async ValueTask FlushBuffer(List<Message> buffer, CancellationToken cancellationToken)
	{
		if (buffer.Count == 0)
			return;

		static IEnumerable<Message> orderBy<T>(IEnumerable<T> messages)
			=> messages.Cast<IServerTimeMessage>().OrderBy(m => m.ServerTime).Cast<Message>();

		var secIdMsgs = buffer.Cast<ISecurityIdMessage>().ToArray();

		foreach (var secGroup in secIdMsgs.GroupBy(m => m.SecurityId))
		{
			var secId = secGroup.Key;
			secId = await SaveSecurity(secId, cancellationToken);

			var arr = secGroup.ToArray();

			foreach (var m in arr)
				m.SecurityId = secId;

			await _getStorage(secId).SaveAsync(orderBy(arr), cancellationToken);
		}

		buffer.Clear();
	}
}
