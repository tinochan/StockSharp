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
