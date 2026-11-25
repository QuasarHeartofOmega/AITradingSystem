namespace AITradingSystem.Core.Models.Enums;

/// <summary>
/// Действие, которое AI предлагает по символу.
/// Buy   — открыть лонг (спот: купить базовый актив).
/// Sell  — на споте: закрыть открытый лонг; шорт только если Trading:AllowShort=true.
/// Hold  — ничего не делать.
/// Close — явно закрыть открытую позицию по рынку.
/// </summary>
public enum SignalAction
{
    Buy = 0,
    Sell = 1,
    Hold = 2,
    Close = 3
}
