namespace StockSharp.Tests;

using StockSharp.Algo.PnL;

[TestClass]
public class PnlCurveCalculatorTests : BaseTestClass
{
	private static readonly SecurityId _spy = new() { SecurityCode = "SPY", BoardCode = SecurityId.AssociatedBoardCode };
	private static readonly SecurityId _option = new() { SecurityCode = "SPY 250117C500", BoardCode = SecurityId.AssociatedBoardCode };

	private static ExecutionMessage Trade(SecurityId secId, Sides side, decimal price, decimal volume, DateTime time, long tradeId)
		=> new()
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = secId,
			Side = side,
			TradePrice = price,
			TradeVolume = volume,
			ServerTime = time,
			TradeId = tradeId,
		};

	[TestMethod]
	public void Spy_RoundTrip()
	{
		var day1 = new DateTime(2024, 1, 10);
		var day2 = new DateTime(2024, 1, 11);

		var curve = PnlCurveCalculator.BuildCumulativeByDate(new[]
		{
			Trade(_spy, Sides.Buy, 480m, 100m, day1, 1),
			Trade(_spy, Sides.Sell, 482m, 100m, day2, 2),
		}).ToArray();

		curve.Length.AssertEqual(2);

		curve[0].Date.AssertEqual(day1);
		curve[0].CumulativePnL.AssertEqual(0m);

		curve[1].Date.AssertEqual(day2);
		curve[1].CumulativePnL.AssertEqual(200m);
	}

	[TestMethod]
	public void Option_Multiplier()
	{
		var day1 = new DateTime(2024, 1, 10);
		var day2 = new DateTime(2024, 1, 11);

		var trades = new[]
		{
			Trade(_option, Sides.Buy, 2.50m, 1m, day1, 1),
			Trade(_option, Sides.Sell, 3.00m, 1m, day2, 2),
		};

		// SPY options trade with a x100 multiplier.
		var withMultiplier = PnlCurveCalculator.BuildCumulativeByDate(trades, sid => sid == _option ? 100m : 1m).ToArray();
		withMultiplier.Last().CumulativePnL.AssertEqual(50m);

		// Without a multiplier the same round-trip is priced per contract.
		var withoutMultiplier = PnlCurveCalculator.BuildCumulativeByDate(trades).ToArray();
		withoutMultiplier.Last().CumulativePnL.AssertEqual(0.5m);
	}

	[TestMethod]
	public void CombinesSecurities()
	{
		var day1 = new DateTime(2024, 1, 10);
		var day2 = new DateTime(2024, 1, 11);
		var day3 = new DateTime(2024, 1, 12);

		var curve = PnlCurveCalculator.BuildCumulativeByDate(new[]
		{
			Trade(_spy, Sides.Buy, 480m, 100m, day1, 1),
			Trade(_option, Sides.Buy, 2.50m, 1m, day1, 2),
			Trade(_spy, Sides.Sell, 482m, 100m, day2, 3),
			Trade(_option, Sides.Sell, 3.00m, 1m, day3, 4),
		}, sid => sid == _option ? 100m : 1m).ToArray();

		curve.Length.AssertEqual(3);

		curve[0].Date.AssertEqual(day1);
		curve[0].CumulativePnL.AssertEqual(0m);

		curve[1].Date.AssertEqual(day2);
		curve[1].CumulativePnL.AssertEqual(200m);

		curve[2].Date.AssertEqual(day3);
		curve[2].CumulativePnL.AssertEqual(250m);
	}

	[TestMethod]
	public void OrdersByDate()
	{
		var day1 = new DateTime(2024, 1, 10);
		var day2 = new DateTime(2024, 1, 11);
		var day3 = new DateTime(2024, 1, 12);

		// Per-security order is chronological (matching stays correct), but globally the
		// dates are non-monotonic (day1, day1, day3, day2). The curve must still be
		// globally date-ascending.
		var curve = PnlCurveCalculator.BuildCumulativeByDate(new[]
		{
			Trade(_spy, Sides.Buy, 480m, 100m, day1, 1),
			Trade(_option, Sides.Buy, 2.50m, 1m, day1, 2),
			Trade(_option, Sides.Sell, 3.00m, 1m, day3, 3),
			Trade(_spy, Sides.Sell, 482m, 100m, day2, 4),
		}, sid => sid == _option ? 100m : 1m).ToArray();

		curve.Select(p => p.Date).AssertEqual(new[] { day1, day2, day3 });

		curve[0].CumulativePnL.AssertEqual(0m);
		curve[1].CumulativePnL.AssertEqual(200m);
		curve[2].CumulativePnL.AssertEqual(250m);
	}
}
