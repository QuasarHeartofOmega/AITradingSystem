using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// Интерфейс исполнителя ордеров (FR-6).
/// </summary>
public interface IOrderExecutor
{
    /// <summary>
    /// Исполнить торговый сигнал (открыть позицию) с учетом текущего стакана для Post-Only.
    /// </summary>
    Task<Order?> ExecuteSignalAsync(
        TradingSignal signal,
        decimal quantity,
        OrderBook? currentOrderBook,
        CancellationToken ct = default,
        ExecutionIntent? entryIntent = null);

    /// <summary>
    /// Закрыть позицию. P1.2: обычный выход идёт marketable LIMIT IOC с cap проскальзывания;
    /// аварийный выход может обойти cap (emergency=true) ради закрытия риска.
    /// </summary>
    Task<Order?> ClosePositionAsync(
        Position position,
        CancellationToken ct = default,
        OrderBook? currentOrderBook = null,
        bool emergency = false);

    /// <summary>
    /// Убедиться, что позиция защищена на бирже (P0.1): ставит OCO
    /// STOP_LOSS_LIMIT + TAKE_PROFIT_LIMIT, если защиты ещё нет.
    /// true — защита есть (или не применима: paper-режим / ExchangeProtectionEnabled=false).
    /// false — защиты нет, позиция без биржевого стопа.
    /// </summary>
    Task<bool> EnsureProtectionAsync(Position position, CancellationToken ct = default);

    /// <summary>
    /// Подтянуть Stop-Loss: cancel/replace биржевой OCO-связки, локальный SL — дублёр.
    /// true — биржевый стоп обновлён (и локальный вместе с ним).
    /// </summary>
    Task<bool> UpdateStopLossAsync(Position position, decimal newStopLoss, CancellationToken ct = default);

    /// <summary>
    /// Снять биржевую защиту позиции (перед market-закрытием или при разборе сиротских ордеров).
    /// true — на бирже защитных ног не осталось.
    /// </summary>
    Task<bool> CancelProtectionAsync(Position position, CancellationToken ct = default);
}