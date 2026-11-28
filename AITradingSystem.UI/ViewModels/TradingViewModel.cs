using System.Collections.ObjectModel;
using System.Windows;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Storage;
using AITradingSystem.Services.Trading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.UI.ViewModels;

public partial class TradingViewModel : ObservableObject
{
	private readonly PositionManager _positionManager;
	private readonly TradingEngine _engine;
	private readonly ITradeHistoryRepository _tradeHistory;
	private readonly ILogger<TradingViewModel> _logger;
	private readonly ExchangeAccountService _account;
	[ObservableProperty] private ObservableCollection<ExchangePosition> _exchangePositions = new();
	[ObservableProperty] private ObservableCollection<Order> _exchangeOpenOrders = new();
	[ObservableProperty] private string _accountStatusText = "Аккаунт ещё не опрошен";
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RefreshAccountCommand))]
	private bool _isRefreshingAccount;

	[ObservableProperty]
	private ObservableCollection<Position> _openPositions = new();

	[ObservableProperty]
	private ObservableCollection<TradeRecord> _tradeHistoryItems = new();

	[ObservableProperty]
	private string _lastSignalText = "Ожидание сигнала...";

	/// <summary>Phase 1.7: ручные/external остатки по символам реестра, ещё не подхваченные ботом.</summary>
	[ObservableProperty]
	private ObservableCollection<ExternalCandidateRow> _externalCandidates = new();

	/// <summary>Phase 1.7: сводка по external-остаткам и режиму подхвата.</summary>
	[ObservableProperty]
	private string _externalStatusText = "External-позиции: режим ещё не опрошен";

	/// <summary>P0.6: статус панического закрытия под кнопкой.</summary>
	[ObservableProperty]
	private string _panicStatusText = string.Empty;

	public TradingViewModel(
		PositionManager positionManager,
		TradingEngine engine,
		ITradeHistoryRepository tradeHistory,
		ILogger<TradingViewModel> logger,
		ExchangeAccountService account)
	{
		_positionManager = positionManager;
		_engine = engine;
		_tradeHistory = tradeHistory;
		_logger = logger;
		_account = account;
		UpdateAccount();
		RefreshExternalCandidates();

		// Загрузка начальных данных
		foreach (var pos in _positionManager.GetOpenPositions())
		{
			AddPosition(pos);
		}

		_ = LoadTradeHistoryAsync();
	}

	private async Task LoadTradeHistoryAsync()
	{
		try
		{
			var trades = await _tradeHistory.GetTradesAsync(100);
			Application.Current?.Dispatcher.Invoke(() =>
			{
				foreach (var trade in trades)
				{
					TradeHistoryItems.Add(trade);
				}
			});
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Не удалось загрузить историю сделок");
		}
	}

	public void AddSignal(TradingSignal signal)
	{
		LastSignalText = $"[{signal.Timestamp:HH:mm:ss}] {signal.Symbol}: {signal.Action} (Conf: {signal.Confidence:P0})\nReason: {signal.Reasoning}";
	}

	public void AddPosition(Position position)
	{
		// Защита от дублей (восстановленные позиции приходят событием при старте)
		if (OpenPositions.Any(p => p.Symbol == position.Symbol))
			return;

		OpenPositions.Add(position);
	}

	public void RemovePosition(string symbol)
	{
		var pos = OpenPositions.FirstOrDefault(p => p.Symbol == symbol);
		if (pos != null)
		{
			OpenPositions.Remove(pos);
		}
	}

	public void AddTradeRecord(TradeRecord trade)
	{
		TradeHistoryItems.Insert(0, trade);
		if (TradeHistoryItems.Count > 100)
		{
			TradeHistoryItems.RemoveAt(100);
		}
	}

	public void UpdateAccount()
	{
		ExchangePositions.Clear();
		foreach (var position in _account.ExchangePositions) ExchangePositions.Add(position);
		ExchangeOpenOrders.Clear();
		foreach (var order in _account.OpenOrders) ExchangeOpenOrders.Add(order);
		AccountStatusText = (_account.LastRefreshUtc.HasValue ? "Обновлено UTC " + _account.LastRefreshUtc.Value.ToString("HH:mm:ss") : "Ожидание аккаунта") +
			(string.IsNullOrEmpty(_account.LastError) ? string.Empty : " · " + _account.LastError);
	}

	/// <summary>Phase 1.7: перечитать кандидатов из движка (событие или обновление аккаунта).</summary>
	public void RefreshExternalCandidates()
	{
		var manual = ExternalCandidates.ToDictionary(r => r.Symbol, r => r.ManualEntryPrice, StringComparer.OrdinalIgnoreCase);
		ExternalCandidates.Clear();
		foreach (var candidate in _engine.ExternalCandidates.OrderBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase))
		{
			var row = new ExternalCandidateRow(candidate);
			if (manual.TryGetValue(candidate.Symbol, out var typed)) row.ManualEntryPrice = typed;
			ExternalCandidates.Add(row);
		}
		var unadopted = _engine.UnadoptedExternalCount;
		ExternalStatusText = $"Режим подхвата: {_engine.ExternalAdoptionMode}. Не подхвачено остатков: {unadopted}." +
			(unadopted > 0
				? " Они уже учтены в exposure/MaxOpenPositions, но бот их не сопровождает и kill-switch их НЕ закроет."
				: " Неподхваченных external-остатков нет.") +
			" Подхват = согласие сопровождать и защищать объём; kill-switch закроет и подхваченную external-долю.";
	}

	/// <summary>Phase 1.7: согласие оператора на подхват (Confirm) с необязательной ручной ценой входа.</summary>
	[RelayCommand]
	private async Task AdoptExternalAsync(ExternalCandidateRow? row)
	{
		if (row == null) return;
		if (!row.TryGetEntryOverride(out var entryOverride))
		{
			MessageBox.Show("Цена входа должна быть положительным числом (или пустое поле — использовать предложенную).",
				"Некорректный ввод", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		var confirm = MessageBox.Show(
			$"Подхватить {row.Symbol}: {row.ExternalQuantity:0.########} (≈{row.NotionalQuote:0.##} USDT)?\n\n" +
			$"Предлагаемый вход: {row.ProposedEntryPrice:0.##########} ({row.EntryBasis})\n" +
			(entryOverride.HasValue ? $"Ручной вход оператора: {entryOverride.Value:0.##########}\n" : string.Empty) +
			$"SL/TP будут рассчитаны движком от ATR; OCO ставится только на свободный объём.\n" +
			$"{row.EntryWarning}\n\n" +
			"После подхвата позиция входит в риск-учёт, и kill-switch/ПАНИКА закроет и её.\n" +
			"Внешние ордера по символу не отменяются.",
			"Подхват external-позиции", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
		if (confirm != MessageBoxResult.Yes) return;

		try
		{
			var ok = await _engine.AdoptExternalPositionAsync(row.Symbol, entryOverride);
			ExternalStatusText = ok
				? $"{row.Symbol}: external-остаток подхвачен, защита ставится."
				: $"{row.Symbol}: подхват не выполнен (кандидат устарел, kill-switch или отказ защиты — см. лог).";
			RefreshExternalCandidates();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Ошибка подхвата external-позиции {Symbol}", row.Symbol);
			MessageBox.Show($"Не удалось подхватить позицию: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	/// <summary>Phase 1.7: Release — оператор забирает остаток себе; бот снимает свою защиту и не продаёт.</summary>
	[RelayCommand]
	private async Task ReleaseExternalAsync(string? symbol)
	{
		if (string.IsNullOrWhiteSpace(symbol)) return;
		var confirm = MessageBox.Show(
			$"Отпустить {symbol}?\n\nБот снимет свою OCO-защиту (если стоит) и уберёт позицию из учёта БЕЗ продажи.\n" +
			"Остаток останется на бирже без сопровождения; kill-switch его не закроет.\n" +
			"Новые покупки сверх отпущенного объёма снова станут кандидатами.",
			"Release external-позиции", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
		if (confirm != MessageBoxResult.Yes) return;

		try
		{
			var ok = await _engine.ReleaseExternalPositionAsync(symbol);
			ExternalStatusText = ok
				? $"{symbol}: остаток отпущен оператору, защита бота снята, продажи не было."
				: $"{symbol}: Release не выполнен (не удалось снять защиту — позиция осталась под сопровождением).";
			RefreshExternalCandidates();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Ошибка Release external-позиции {Symbol}", symbol);
			MessageBox.Show($"Не удалось отпустить позицию: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private bool CanRefreshAccount() => !IsRefreshingAccount;
	[RelayCommand(CanExecute = nameof(CanRefreshAccount))]
	private async Task RefreshAccountAsync()
	{
		IsRefreshingAccount = true;
		try { await _account.RefreshAsync(); UpdateAccount(); RefreshExternalCandidates(); }
		catch (Exception ex) { AccountStatusText = "Ошибка обновления: " + ex.Message; }
		finally { IsRefreshingAccount = false; }
	}

	[RelayCommand]
	private async Task ClosePositionAsync(Position? position)
	{
		if (position == null) return;

		try
		{
			_logger.LogInformation("Ручное закрытие позиции {Symbol}", position.Symbol);
			// Закрываем через движок: ордер + история + баланс + событие OnPositionClosed,
			// которое само удалит позицию из списка и добавит запись в историю.
			await _engine.ClosePositionManuallyAsync(position);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Ошибка ручного закрытия позиции {Symbol}", position.Symbol);
			MessageBox.Show($"Не удалось закрыть позицию: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	[RelayCommand]
	private async Task ResetKillSwitchAsync()
	{
		var confirm = MessageBox.Show(
			"Сбросить kill-switch и снова разрешить новые входы?\n\nУбедитесь, что причина пробития лимита разобрана, позиции закрыты, а лимит риска больше не пробит.",
			"Сброс kill-switch", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
		if (confirm != MessageBoxResult.Yes) return;
		var ok = await _engine.ResetKillSwitchAsync("UI manual reset");
		PanicStatusText = ok
			? "Kill-switch сброшен. Новые входы снова разрешены после Resume/Старт."
			: "Сброс kill-switch запрещён: остались позиции или лимит риска всё ещё пробит.";
	}

	/// <summary>
	/// P0.6: паническая кнопка — закрыть ВСЕ позиции бота и заблокировать новые входы.
	/// Двухшаговая: подтверждение в диалоге, затем kill-switch движка.
	/// </summary>
	[RelayCommand]
	private async Task PanicCloseAllAsync()
	{
		var confirm = MessageBox.Show(
			"ЗАКРЫТЬ ВСЕ ПОЗИЦИИ по рынку и заблокировать новые входы?\n\n" +
			"Действие отправит реальные MARKET-ордера в выбранном окружении.\n\n" +
			"Подхваченные external/Mixed-позиции (Source=External/Mixed) будут закрыты тоже.\n" +
			"Неподхваченные остатки и отпущенные через Release — НЕ закрываются.",
			"ПАНИКА — закрытие всех позиций", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
		if (confirm != MessageBoxResult.Yes) return;

		try
		{
			PanicStatusText = "Паника: закрываю все позиции...";
			var closed = await _engine.EngageKillSwitchAsync("ManualPanic");
			PanicStatusText = $"Kill-switch взведён: закрыто {closed} позиций. Новые входы заблокированы.";
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Ошибка панического закрытия");
			PanicStatusText = "Ошибка панического закрытия: " + ex.Message;
		}
	}
}
