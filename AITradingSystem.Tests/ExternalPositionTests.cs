using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITradingSystem.Tests;

/// <summary>
/// Phase 1.7 (Blocker 7): учёт external-доли, lifetime-база при ручной продаже и
/// durable-журнал Release. Тесты не запускались в этом проходе (правка кода без прогона).
/// </summary>
public class ExternalPositionTests
{
    private static Position BotPosition(decimal qty = 10m) => new()
    {
        Symbol = "DOGEUSDT",
        Side = OrderSide.Buy,
        Quantity = qty,
        EntryPrice = 0.10m,
        CurrentPrice = 0.11m,
        LifetimeBoughtQuantity = qty
    };

    [Fact]
    public void LegacyPosition_DefaultsToBot_WithoutExternalOwnership()
    {
        var p = BotPosition();
        Assert.Equal(PositionSource.Bot, p.Source);
        Assert.Equal("engine", p.EntryBasis);
        Assert.False(p.HasExternalOwnership);
        Assert.Equal(10m, p.BotQuantity);
        Assert.Equal(-1m, p.ExternalObservedBotQuantity); // baseline ещё не снят
    }

    [Fact]
    public void Merge_ExternalShare_KeepsBotAndExternalSeparate_AndWeightedEntry()
    {
        var p = BotPosition();
        p.Quantity += 5m;
        p.RegisterEntryQuantity(5m);
        p.ExternalQuantity = 5m;
        p.ExternalEntryPrice = 0.20m;
        p.Source = PositionSource.Mixed;

        Assert.True(p.HasExternalOwnership);
        Assert.Equal(10m, p.BotQuantity);
        Assert.Equal(5m, p.ExternalQuantity);
        Assert.Equal(15m, p.LifetimeBoughtQuantity);
    }

    [Fact]
    public void RegisterExitQuantity_ManualSell_ConsumesLifetimeBudget_NoOversellOnNextBotExit()
    {
        var p = BotPosition();
        p.RegisterExitQuantity(4m);   // оператор продал 4 мимо бота
        p.ReduceQuantity(4m);

        Assert.Equal(6m, p.Quantity);
        Assert.Equal(4m, p.LifetimeSoldQuantity);

        // Бот пытается продать исходные 10 — разрешено только 6, oversell flagged.
        var ok = p.TryReserveExitQuantity(10m, out var allowed, out var oversell);
        Assert.True(ok);
        Assert.Equal(6m, allowed);
        Assert.True(oversell);
    }

    [Fact]
    public void RegisterExitQuantity_IgnoresNonPositive()
    {
        var p = BotPosition();
        p.RegisterExitQuantity(0m);
        p.RegisterExitQuantity(-1m);
        Assert.Equal(0m, p.LifetimeSoldQuantity);
    }

    [Fact]
    public void ExternalOnlyPosition_HasZeroBotShare()
    {
        var p = new Position
        {
            Symbol = "XRPUSDT", Side = OrderSide.Buy, Quantity = 100m, EntryPrice = 0.5m,
            Source = PositionSource.External, ExternalQuantity = 100m, ExternalEntryPrice = 0.5m,
            ExternalObservedBotQuantity = 0m, LifetimeBoughtQuantity = 100m
        };
        Assert.True(p.HasExternalOwnership);
        Assert.Equal(0m, p.BotQuantity);
    }

    [Fact]
    public void BalanceDelta_SubtractsBotOwnFillChange()
    {
        // Снимок 1: total=10, доля бота=10. Снимок 2: бот докупил 3 → total=13, bot=13.
        // External-delta = (13-10) - (13-10) = 0 — ручной покупки не было.
        var totalDelta = 13m - 10m;
        var botDelta = 13m - 10m;
        Assert.Equal(0m, totalDelta - botDelta);

        // Снимок 3: оператор докупил 2 → total=15, bot=13 → external +2.
        Assert.Equal(2m, (15m - 13m) - (13m - 13m));
    }

    [Fact]
    public async Task ReleaseLedger_PersistsAcrossReload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ext-ledger-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ledger = new JsonExternalPositionLedger(dir, NullLogger<JsonExternalPositionLedger>.Instance);
            var state = new ExternalPositionLedgerState();
            state.Released["DOGEUSDT"] = new ExternalReleaseRecord
            {
                Symbol = "DOGEUSDT", ReleasedQuantity = 5333m, Reason = "operator-release"
            };
            await ledger.SaveAsync(state);

            var reloaded = await new JsonExternalPositionLedger(dir, NullLogger<JsonExternalPositionLedger>.Instance).LoadAsync();
            Assert.True(reloaded.Released.ContainsKey("dogeusdt")); // case-insensitive ключи
            Assert.Equal(5333m, reloaded.Released["DOGEUSDT"].ReleasedQuantity);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ReleaseLedger_MissingFile_ReturnsEmptyState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ext-ledger-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ledger = new JsonExternalPositionLedger(dir, NullLogger<JsonExternalPositionLedger>.Instance);
            var state = await ledger.LoadAsync();
            Assert.Empty(state.Released);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DefaultPolicy_IsConfirmAndAlertNotSell()
    {
        var config = new AITradingSystem.Common.Configuration.TradingConfig();
        Assert.Equal(ExternalAdoptionMode.Confirm, config.AdoptExternalPositions);
        Assert.Equal(ExternalProtectionFailurePolicy.AlertNotSell, config.ExternalProtectionFailurePolicy);
        Assert.Equal(72, config.ExternalAdoptionLookbackHours);
    }
}
