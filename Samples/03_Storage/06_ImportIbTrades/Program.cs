namespace StockSharp.Samples.Storage.ImportIbTrades;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using StockSharp.Algo.Import;
using StockSharp.Algo.PnL;
using StockSharp.Algo.Storages;
using StockSharp.BusinessEntities;
using StockSharp.Configuration;
using StockSharp.Messages;

static class Program
{
	private static async Task Main(string[] args)
	{
		var token = CancellationToken.None;

		var csvPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "sample_ib_trades.csv");

		if (!File.Exists(csvPath))
		{
			Console.WriteLine($"File not found: {csvPath}");
			Console.WriteLine($"Usage: {AppDomain.CurrentDomain.FriendlyName} <path-to-ib-trades.csv>");
			return;
		}

		var localDrive = new LocalMarketDataDrive(Paths.FileSystem, Paths.HistoryDataPath);
		var storageRegistry = new StorageRegistry { DefaultDrive = localDrive };
		var securityStorage = new InMemorySecurityStorage();

		var parser = new IbTradesCsvParser();
		var importer = new IbTradesImporter(parser, securityStorage, new InMemoryExchangeInfoProvider(), secId => storageRegistry.GetStorage(secId, DataType.Transactions));

		var secIds = new HashSet<SecurityId>();
		importer.SecurityUpdated += (sec, _) => secIds.Add(sec.ToSecurityId());

		Console.WriteLine($"Importing {csvPath} ...");

		(int count, DateTime? lastTime) result;

		using (var stream = File.OpenRead(csvPath))
			result = await importer.Import(stream, p => Console.Write($"\r{p}%"), token);

		Console.WriteLine($"\nImported {result.count} trades, last trade time: {result.lastTime}.");

		Console.WriteLine();
		Console.WriteLine($"{"Security",-20} {"Trades",6} {"RealizedPnL",12}");

		foreach (var secId in secIds.OrderBy(s => s.SecurityCode))
		{
			var queue = new PnLQueue(secId);

			var security = await securityStorage.LookupByIdAsync(secId, token);

			// Options are imported with Multiplier=100 (SPY options), stocks/ETFs with 1.
			if (security?.Multiplier is decimal multiplier)
				queue.LotMultiplier = multiplier;

			var trades = 0;
			var storage = storageRegistry.GetTransactionStorage(secId);

			await foreach (var msg in storage.LoadAsync(null, null))
			{
				if (msg is not ExecutionMessage exec || !exec.HasTradeInfo() || exec.Side is not Sides)
					continue;

				queue.Process(exec);
				trades++;
			}

			Console.WriteLine($"{secId.SecurityCode,-20} {trades,6} {queue.RealizedPnL,12:F2}");
		}
	}
}
