using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Binance;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// Read-only снимки аккаунта выбранной биржи. Не изменяет RiskManager и не отправляет ордера.
/// Один таймер и SemaphoreSlim исключают наложение запросов; ошибки остаются в LastError.
/// </summary>
public sealed class ExchangeAccountService : IAsyncDisposable
{
    private readonly TradingConfig _config;
    private readonly IBinanceRestClient _rest;
    private readonly IBinanceWebSocketClient _ws;
    private readonly ILogger<ExchangeAccountService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _snapshotLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private AccountSnapshot _snapshot;

    public event Action? OnAccountUpdated;
    public event Action? OnOrdersUpdated;
    public event Action? OnStatusUpdated;

    public sealed record AccountSnapshot(
        AccountBalance? Balance, IReadOnlyList<ExchangePosition> Positions, IReadOnlyList<Order> Orders,
        decimal? Equity, bool EquityIsPartial, DateTime? LastRefreshUtc, string? Error, string Environment, string Mode);

    public ExchangeAccountService(TradingConfig config, IBinanceRestClient rest, IBinanceWebSocketClient ws,
        ILogger<ExchangeAccountService> logger)
    {
        _config = config; _rest = rest; _ws = ws; _logger = logger;
        _snapshot = new AccountSnapshot(null, Array.Empty<ExchangePosition>(), Array.Empty<Order>(), null, false,
            null, null, _rest.EnvironmentName, _config.PaperTradingMode ? "PAPER" : "REAL ORDERS");
    }

    public AccountSnapshot Snapshot { get { lock (_snapshotLock) return _snapshot; } }
    public AccountBalance? AccountBalance => Snapshot.Balance;
    public IReadOnlyList<ExchangePosition> ExchangePositions => Snapshot.Positions;
    public IReadOnlyList<Order> OpenOrders => Snapshot.Orders;
    public string EnvironmentName => _rest.EnvironmentName;
    public string Mode => _config.PaperTradingMode ? "PAPER" : "REAL ORDERS";
    public bool HasApiKeys => _rest.HasApiKeys;
    public bool IsConnected => _ws.IsConnected;
    public DateTime? LastRefreshUtc => Snapshot.LastRefreshUtc;
    public string? LastError => Snapshot.Error;
    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.AccountRefreshIntervalSeconds));
            await RefreshAsync(ct);
            // C12: у PeriodicTimer метод называется WaitForNextTickAsync (WaitAsync — CS1061).
            while (await timer.WaitForNextTickAsync(ct)) await RefreshAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>Ручное обновление ожидает текущий цикл, но не запускает второй параллельный опрос.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        var previous = Snapshot;
        AccountBalance? balance = null;
        List<Order>? orders = null;
        var errors = new List<string>();
        try
        {
            if (!HasApiKeys)
                errors.Add("API-ключи не заданы: балансы и ордера недоступны. Публичные цены продолжают работать.");
            else
            {
                // Запросы последовательны: общая синхронизация времени REST-клиента остаётся простой.
                try { balance = await _rest.GetAccountBalanceAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add("Баланс: " + ex.Message); _logger.LogWarning(ex, "Ошибка чтения аккаунта {Environment}", EnvironmentName); }
                try { orders = await _rest.GetOpenOrdersAsync(ct: ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add("Ордера: " + ex.Message); _logger.LogWarning(ex, "Ошибка чтения ордеров {Environment}", EnvironmentName); }
            }

            var effectiveBalance = balance ?? previous.Balance;
            var positions = new List<ExchangePosition>();
            decimal? equity = null;
            var partial = false;
            if (effectiveBalance != null)
            {
                Dictionary<string, decimal>? prices = null;
                try { prices = await _rest.GetAllPricesAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add("Цены: " + ex.Message); }
                prices ??= new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                // REST заполняет отсутствующие цены, свежий WS-кэш имеет приоритет.
                foreach (var pair in _config.Symbols)
                    if (_ws.TryGetLastPrice(pair, out var price)) prices[pair] = price;

                // Таблица «позиции на бирже» остаётся отфильтрованной по watch-list + базовым
                // активам настроенных пар, но EQUITY считается по ВСЕМ активам аккаунта (C15):
                // иначе монета вне списка молча исчезала бы из оценки депозита.
                var watch = _config.WatchAssets.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var symbol in _config.Symbols)
                {
                    var quote = _config.QuoteAssets.OrderByDescending(q => q.Length)
                        .FirstOrDefault(q => symbol.EndsWith(q, StringComparison.OrdinalIgnoreCase));
                    if (quote != null) watch.Add(symbol[..^quote.Length]);
                }

                foreach (var pair in effectiveBalance.Assets.OrderBy(a => a.Key, StringComparer.Ordinal))
                {
                    var asset = pair.Key;
                    var amount = pair.Value;
                    if (amount.Total <= 0) continue;

                    var (price, value, route) = EstimateUsdt(asset, amount.Total, prices);

                    // Equity: любой положительный актив без маршрута конвертации делает оценку частичной,
                    // независимо от того, входит ли он в watch-list.
                    if (value.HasValue) equity = (equity ?? 0m) + value.Value;
                    else partial = true;

                    if (!watch.Contains(asset)) continue;

                    var position = new ExchangePosition { Asset = asset, Free = amount.Free, Locked = amount.Locked,
                        LastPrice = price, ValueInQuote = value, PriceRoute = route };
                    if (value >= _config.MinExchangePositionNotional || value is null)
                        positions.Add(position);
                }
                // Нулевые остатки тоже видны в полной таблице активов, но не являются позициями.
                if (balance != null && !balance.Assets.ContainsKey("USDT")) balance.UpdateAsset("USDT", 0m, 0m);
            }
            var snapshot = new AccountSnapshot(effectiveBalance, positions, orders ?? previous.Orders, equity, partial,
                DateTime.UtcNow, errors.Count > 0 ? string.Join(" | ", errors) : null,
                EnvironmentName, Mode);
            Publish(snapshot);
            OnAccountUpdated?.Invoke();
            OnOrdersUpdated?.Invoke();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сформировать снимок аккаунта");
            Publish(previous with { Error = ex.Message, Environment = EnvironmentName, Mode = Mode });
        }
        finally
        {
            _refreshLock.Release();
            OnStatusUpdated?.Invoke();
        }
    }

    private static (decimal? Price, decimal? Value, string Route) EstimateUsdt(
        string asset, decimal quantity, IReadOnlyDictionary<string, decimal> prices)
    {
        if (string.Equals(asset, "USDT", StringComparison.OrdinalIgnoreCase)) return (1m, quantity, "USDT");
        foreach (var quote in new[] { "USDT", "FDUSD", "USDC", "BTC", "ETH", "BNB" })
        {
            if (!prices.TryGetValue(asset + quote, out var direct)) continue;
            var factor = quote == "USDT" ? 1m : prices.TryGetValue(quote + "USDT", out var q) ? q : (decimal?)null;
            if (factor.HasValue) return (direct, quantity * direct * factor.Value, $"{asset}{quote}" + (quote == "USDT" ? "" : $" → {quote}USDT"));
        }
        return (null, null, "Нет цены конвертации в USDT");
    }

    private void Publish(AccountSnapshot snapshot) { lock (_snapshotLock) _snapshot = snapshot; }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _cts?.Dispose(); _refreshLock.Dispose();
    }
}
