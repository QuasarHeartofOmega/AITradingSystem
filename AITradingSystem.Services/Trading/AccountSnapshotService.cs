using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Binance;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// P0.1: строит AccountSnapshot для live/demo и paper.
/// Live equity оценивает ВСЕ положительные активы счёта, а не только watch-list.
/// Paper equity = виртуальный cash ledger + рыночная стоимость длинных спот-позиций.
/// </summary>
public sealed class AccountSnapshotService
{
    private const string QuoteAsset = "USDT";
    private readonly TradingConfig _config;
    private readonly IBinanceRestClient _rest;
    private readonly IBinanceWebSocketClient _ws;
    private readonly PositionManager _positions;

    public AccountSnapshotService(
        TradingConfig config,
        IBinanceRestClient rest,
        IBinanceWebSocketClient ws,
        PositionManager positions)
    {
        _config = config;
        _rest = rest;
        _ws = ws;
        _positions = positions;
    }

    public async Task<AccountSnapshot> CreateLiveAsync(CancellationToken ct = default)
    {
        var balance = await _rest.GetAccountBalanceAsync(ct);
        var prices = await _rest.GetAllPricesAsync(ct);
        foreach (var symbol in _config.Symbols)
            if (_ws.TryGetLastPrice(symbol, out var price) && price > 0m)
                prices[symbol] = price;

        var free = balance.Assets.TryGetValue(QuoteAsset, out var q) ? q.Free : 0m;
        var locked = balance.Assets.TryGetValue(QuoteAsset, out var l) ? l.Locked : 0m;
        var equity = 0m;
        var complete = true;
        foreach (var (asset, amount) in balance.Assets)
        {
            if (amount.Total <= 0m) continue;
            var value = EstimateQuoteValue(asset, amount.Total, prices);
            if (value.HasValue) equity += value.Value;
            else complete = false;
        }

        var open = _positions.GetOpenPositions();
        var notional = open.Sum(MarkedNotional);
        var unrealized = open.Sum(p => p.UnrealizedPnL);

        return new AccountSnapshot
        {
            EquityQuote = equity,
            FreeCashQuote = free,
            LockedCashQuote = locked,
            PositionNotionalQuote = notional,
            UnrealizedPnlQuote = unrealized,
            AccountId = _rest.AccountId,
            Environment = _rest.EnvironmentName,
            QuoteAsset = QuoteAsset,
            AsOfUtc = DateTime.UtcNow,
            Source = "exchange-rest+ws",
            IsValuationComplete = complete
        };
    }

    public AccountSnapshot CreatePaper(decimal freeCashQuote)
    {
        var open = _positions.GetOpenPositions();
        var notional = open.Sum(MarkedNotional);
        var unrealized = open.Sum(p => p.UnrealizedPnL);
        var complete = open.All(p => p.Side == OrderSide.Buy);
        return new AccountSnapshot
        {
            EquityQuote = freeCashQuote + notional,
            FreeCashQuote = freeCashQuote,
            LockedCashQuote = 0m,
            PositionNotionalQuote = notional,
            UnrealizedPnlQuote = unrealized,
            AccountId = string.IsNullOrWhiteSpace(_config.PaperTradingAccountId) ? "default" : _config.PaperTradingAccountId,
            Environment = "Paper",
            QuoteAsset = QuoteAsset,
            AsOfUtc = DateTime.UtcNow,
            Source = "paper-cash-ledger",
            IsValuationComplete = complete
        };
    }

    private static decimal MarkedNotional(Position p)
    {
        var price = p.CurrentPrice > 0m ? p.CurrentPrice : p.EntryPrice;
        return p.Quantity * price;
    }

    private static decimal? EstimateQuoteValue(string asset, decimal quantity, IReadOnlyDictionary<string, decimal> prices)
    {
        if (string.Equals(asset, QuoteAsset, StringComparison.OrdinalIgnoreCase)) return quantity;
        foreach (var quote in new[] { "USDT", "FDUSD", "USDC", "BTC", "ETH", "BNB" })
        {
            if (!prices.TryGetValue(asset + quote, out var direct) || direct <= 0m) continue;
            var factor = quote == "USDT" ? 1m : prices.TryGetValue(quote + "USDT", out var q) && q > 0m ? q : (decimal?)null;
            if (factor.HasValue) return quantity * direct * factor.Value;
        }
        return null;
    }
}
