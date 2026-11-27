using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Tests;

/// <summary>
/// Phase 1.5 (Blocker 5): одна позиция не продаётся дважды. Lifetime-учёт «куплено/продано»
/// ограничивает суммарный объём закрытий, а восстановление биржевой защиты обнуляет бюджет
/// принудительных закрытий. Проверки纯 pure — без сети, ордеров и сборки runtime-контура.
/// </summary>
public class CloseIdempotencyTests
{
    private static Position PartialEntry() => new()
    {
        Symbol = "DASHUSDT",
        Side = OrderSide.Buy,
        Quantity = 1.267m,
        LifetimeBoughtQuantity = 1.267m,
        EntryPrice = 36.82m,
        CurrentPrice = 36.67m
    };

    [Fact]
    public void ExitReservation_NeverExceedsLifetimeBought()
    {
        var position = PartialEntry();

        Assert.True(position.TryReserveExitQuantity(1.267m, out var first, out var oversell1));
        Assert.Equal(1.267m, first);
        Assert.False(oversell1);

        // Повторный контур (reconcile / monitor / manual) просит тот же объём ещё раз.
        Assert.False(position.TryReserveExitQuantity(1.267m, out var second, out var oversell2));
        Assert.Equal(0m, second);
        Assert.True(oversell2);
        Assert.Equal(1.267m, position.LifetimeSoldQuantity);
        Assert.False(position.IsLifetimeOversold);
    }

    [Fact]
    public void ExitReservation_ClampsOversizedRequest()
    {
        var position = PartialEntry();

        Assert.True(position.TryReserveExitQuantity(19.038m, out var allowed, out var oversell));
        Assert.Equal(1.267m, allowed);
        Assert.True(oversell);
        Assert.Equal(0m, position.RemainingSellableLifetimeQuantity);
    }

    [Fact]
    public void PartialExits_AccumulateUntilLifetimeExhausted()
    {
        var position = PartialEntry();

        Assert.True(position.TryReserveExitQuantity(0.5m, out var first, out _));
        Assert.True(position.TryReserveExitQuantity(0.5m, out var second, out _));
        Assert.True(position.TryReserveExitQuantity(0.5m, out var third, out var oversell));

        Assert.Equal(0.5m, first);
        Assert.Equal(0.5m, second);
        Assert.Equal(0.267m, third);
        Assert.True(oversell);
        Assert.Equal(1.267m, position.LifetimeSoldQuantity);
    }

    [Fact]
    public void LegacyPosition_WithoutLifetimeAccounting_UsesCurrentQuantity()
    {
        var position = PartialEntry();
        position.LifetimeBoughtQuantity = 0m; // запись из старого positions.json

        Assert.Equal(1.267m, position.RemainingSellableLifetimeQuantity);
        Assert.True(position.TryReserveExitQuantity(1.267m, out var allowed, out _));
        Assert.Equal(1.267m, allowed);
        Assert.False(position.TryReserveExitQuantity(0.1m, out _, out _));
    }

    [Fact]
    public void EntryTopUp_RaisesLifetimeCeiling()
    {
        var position = PartialEntry();
        position.RegisterEntryQuantity(0.733m);

        Assert.Equal(2m, position.LifetimeBoughtQuantity);
        Assert.True(position.TryReserveExitQuantity(2m, out var allowed, out var oversell));
        Assert.Equal(2m, allowed);
        Assert.False(oversell);
    }

    [Fact]
    public void RestoredProtection_ResetsForcedCloseBudget()
    {
        var position = PartialEntry();
        position.ForcedCloseAttempts = 3;
        position.LastForcedCloseAttemptUtc = DateTime.UtcNow;

        position.SetProtection(new ProtectiveOrderList
        {
            Symbol = position.Symbol,
            OrderListId = 4242,
            ListClientOrderId = "oco-test",
            StopClientOrderId = "sl-test",
            TakeProfitClientOrderId = "tp-test"
        });

        Assert.Equal(0, position.ForcedCloseAttempts);
        Assert.Null(position.LastForcedCloseAttemptUtc);

        position.SetProtection(null);
        Assert.Equal(0, position.ForcedCloseAttempts);
    }

    [Fact]
    public void ExchangeFillDedup_IsCumulativePerClientOrderId()
    {
        var position = PartialEntry();
        const string clientId = "close-test";

        Assert.Equal(1.267m, position.GetUnappliedExchangeFill(clientId, 1.267m));
        position.RecordExchangeFill(clientId, 1.267m);
        Assert.Equal(0m, position.GetUnappliedExchangeFill(clientId, 1.267m));
    }
}
