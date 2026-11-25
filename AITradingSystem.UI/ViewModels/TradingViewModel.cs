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

	private bool CanRefreshAccount() => !IsRefreshingAccount;
	[RelayCommand(CanExecute = nameof(CanRefreshAccount))]
	private async Task RefreshAccountAsync()
	{
		IsRefreshingAccount = true;
		try { await _account.RefreshAsync(); UpdateAccount(); }
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
	private void ResetKillSwitch()
	{
		var confirm = MessageBox.Show(
			"Сбросить kill-switch и снова разрешить новые входы?\n\nУбедитесь, что причина пробития лимита разобрана.",
			"Сброс kill-switch", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
		if (confirm != MessageBoxResult.Yes) return;
		_engine.ResetKillSwitch();
		PanicStatusText = "Kill-switch сброшен. Новые входы снова разрешены после Resume/Старт.";
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
			"Действие отправит реальные MARKET-ордера в выбранном окружении.",
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
