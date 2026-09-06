namespace StockSharp.Samples.Chart.PnlCurve;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Threading;

using Ecng.Collections;
using Ecng.Common;
using Ecng.Drawing;

using StockSharp.Algo.PnL;
using StockSharp.Algo.Storages;
using StockSharp.Charting;
using StockSharp.Configuration;
using StockSharp.Messages;
using StockSharp.Xaml.Charting;

public partial class MainWindow
{
	// Matches option codes produced by the IB importer: "SPY 250117C500".
	private static readonly Regex _optionRegex = new(
		@"^([A-Z0-9]+) \d{6}[CP].+$",
		RegexOptions.Compiled | RegexOptions.IgnoreCase);

	private static readonly System.Drawing.Color[] _palette =
	[
		System.Drawing.Color.RoyalBlue,
		System.Drawing.Color.ForestGreen,
		System.Drawing.Color.DarkOrange,
		System.Drawing.Color.DarkViolet,
		System.Drawing.Color.Crimson,
		System.Drawing.Color.DarkCyan,
		System.Drawing.Color.SaddleBrown,
		System.Drawing.Color.MediumBlue,
	];

	private readonly LocalMarketDataDrive _drive;
	private readonly StorageRegistry _storage;
	private bool _isUpdating;
	private int _colorIndex;

	public MainWindow()
	{
		InitializeComponent();

		_drive = new LocalMarketDataDrive(Paths.FileSystem, Paths.HistoryDataPath);
		_storage = new StorageRegistry { DefaultDrive = _drive };
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		try
		{
			await LoadSecuritiesAsync();
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private async void OnRefreshClick(object sender, RoutedEventArgs e)
	{
		await LoadSecuritiesAsync();
	}

	private async System.Threading.Tasks.Task LoadSecuritiesAsync()
	{
		SecuritiesTree.Items.Clear();
		Chart.ClearAreas();

		var secIds = new List<SecurityId>();

		await foreach (var secId in _drive.GetAvailableSecuritiesAsync().WithEnforcedCancellation(CancellationToken.None))
		{
			if (!secId.SecurityCode.IsEmpty())
				secIds.Add(secId);
		}

		var underlyings = new List<SecurityId>();
		var options = new Dictionary<string, List<SecurityId>>(StringComparer.InvariantCultureIgnoreCase);

		foreach (var secId in secIds)
		{
			var match = _optionRegex.Match(secId.SecurityCode);

			if (match.Success)
				options.SafeAdd(match.Groups[1].Value).Add(secId);
			else
				underlyings.Add(secId);
		}

		foreach (var secId in underlyings.OrderBy(s => s.SecurityCode))
			AddNode(secId, false, options.TryGetValue(secId.SecurityCode, out var opts) ? opts : null);

		// Roots that appear only as option roots (no underlying trades yet).
		foreach (var root in options.Keys.Where(r => underlyings.All(s => !s.SecurityCode.EqualsIgnoreCase(r))).OrderBy(r => r))
			AddNode(new SecurityId { SecurityCode = root, BoardCode = SecurityId.AssociatedBoardCode }, false, options[root]);
	}

	private void AddNode(SecurityId secId, bool isOption, IEnumerable<SecurityId> options)
	{
		var item = new TreeViewItem { Tag = (secId, isOption) };
		var checkBox = new CheckBox { Content = secId.SecurityCode, Tag = item };
		checkBox.Checked += OnSelectionChanged;
		checkBox.Unchecked += OnSelectionChanged;
		item.Header = checkBox;

		if (options != null)
		{
			foreach (var opt in options.OrderBy(s => s.SecurityCode))
			{
				var optItem = new TreeViewItem { Tag = (opt, true) };
				var optCheckBox = new CheckBox { Content = opt.SecurityCode, Tag = optItem };
				optCheckBox.Checked += OnSelectionChanged;
				optCheckBox.Unchecked += OnSelectionChanged;
				optItem.Header = optCheckBox;
				item.Items.Add(optItem);
			}
		}

		SecuritiesTree.Items.Add(item);
	}

	private void OnSelectionChanged(object sender, RoutedEventArgs e)
	{
		if (_isUpdating)
			return;

		if (sender is not CheckBox checkBox || checkBox.Tag is not TreeViewItem item)
			return;

		// Checking/unchecking an underlying also toggles its options.
		_isUpdating = true;

		if (checkBox.IsChecked == true)
		{
			foreach (var child in item.Items.OfType<TreeViewItem>())
			{
				if (child.Header is CheckBox childCheckBox && childCheckBox.IsChecked != true)
					childCheckBox.IsChecked = true;
			}
		}
		else if (checkBox.IsChecked == false)
		{
			foreach (var child in item.Items.OfType<TreeViewItem>())
			{
				if (child.Header is CheckBox childCheckBox && childCheckBox.IsChecked != false)
					childCheckBox.IsChecked = false;
			}
		}

		_isUpdating = false;

		RebuildChart();
	}

	private List<(SecurityId secId, bool isOption)> GetSelectedSecurities()
	{
		var result = new List<(SecurityId, bool)>();

		void Walk(ItemCollection items)
		{
			foreach (var obj in items)
			{
				if (obj is not TreeViewItem item)
					continue;

				if (item.Header is CheckBox checkBox && checkBox.IsChecked == true && item.Tag is (SecurityId secId, bool isOption))
					result.Add((secId, isOption));

				Walk(item.Items);
			}
		}

		Walk(SecuritiesTree.Items);

		return result;
	}

	private async void RebuildChart()
	{
		Chart.ClearAreas();

		var area = Chart.AddArea();
		area.YAxises.First().AutoRange = true;
		_colorIndex = 0;

		var selected = GetSelectedSecurities();

		if (selected.Count == 0)
			return;

		var curves = new List<((SecurityId secId, bool isOption), PnLCurvePoint[])>();

		foreach (var (secId, isOption) in selected)
		{
			var trades = new List<ExecutionMessage>();
			var storage = _storage.GetTransactionStorage(secId);

			await foreach (var msg in storage.LoadAsync(null, null).WithEnforcedCancellation(CancellationToken.None))
			{
				if (msg.HasTradeInfo())
					trades.Add(msg);
			}

			// SPY options trade with a x100 multiplier.
			var curve = PnlCurveCalculator.BuildCumulativeByDate(trades, _ => isOption ? 100m : 1m).ToArray();

			curves.Add(((secId, isOption), curve));

			var line = area.Chart.CreateLineElement();
			line.FullTitle = secId.SecurityCode;
			line.Color = NextColor();
			line.StrokeThickness = 2;
			area.Chart.AddElement(area, line);

			var data = area.Chart.CreateData();

			foreach (var point in curve)
				data.Group(point.Date).Add(line, (double)point.CumulativePnL);

			area.Chart.Draw(data);
		}

		// A combined "selected total" line (sum of the per-security curves).
		if (curves.Count > 1)
		{
			var total = new SortedDictionary<DateTime, decimal>();

			foreach (var (_, curve) in curves)
			{
				foreach (var point in curve)
					total[point.Date] = (total.TryGetValue(point.Date, out var value) ? value : 0m) + point.CumulativePnL;
			}

			var totalLine = area.Chart.CreateLineElement();
			totalLine.FullTitle = "Total";
			totalLine.Color = System.Drawing.Color.Black;
			totalLine.StrokeThickness = 3;
			area.Chart.AddElement(area, totalLine);

			var data = area.Chart.CreateData();

			foreach (var pair in total)
				data.Group(pair.Key).Add(totalLine, (double)pair.Value);

			area.Chart.Draw(data);
		}
	}

	private System.Drawing.Color NextColor()
		=> _palette[_colorIndex++ % _palette.Length];
}
