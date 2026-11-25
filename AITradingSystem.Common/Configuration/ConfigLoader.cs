using AITradingSystem.Core.Models.Config;
using Microsoft.Extensions.Configuration;

namespace AITradingSystem.Common.Configuration;

public static class ConfigLoader
{
    /// <summary>
    /// symbols.json — основной источник списка пар и их лимитов, если содержит непустой Symbols.
    /// Иначе используется appsettings/environment override. Повреждённый symbols.json — ошибка запуска,
    /// чтобы не начать торговлю с неожиданным списком. Валидация против биржи выполняется при старте движка.
    /// D23: файл разбирается в ту же модель <see cref="SymbolsRegistry"/>, что и экран настроек
    /// (SymbolsRegistryLoader), а нормализация/дубликаты/сумма активных лимитов проверяются ровно
    /// один раз — в SymbolsRegistry.Validate(). Ручной разбор JSON и вторая сумма лимитов убраны.
    /// </summary>
    public static AppConfig Load(string basePath, string? environment = null)
    {
        if (string.IsNullOrWhiteSpace(basePath)) throw new ArgumentException("basePath не может быть пустым.", nameof(basePath));
        if (!Directory.Exists(basePath)) throw new DirectoryNotFoundException($"Директория не найдена: {basePath}");
        var builder = new ConfigurationBuilder().SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        if (!string.IsNullOrWhiteSpace(environment))
            builder.AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: true);
        var configuration = builder.Build();
        var config = new AppConfig();
        configuration.GetSection("Trading").Bind(config.Trading);
        configuration.GetSection("Binance").Bind(config.Binance);
        configuration.GetSection("AI").Bind(config.AI);
        configuration.GetSection("News").Bind(config.News);
        configuration.GetSection("Telegram").Bind(config.Telegram);
        configuration.GetSection("Logging").Bind(config.Logging);

        ApplySymbolsRegistry(config, Path.Combine(basePath, "symbols.json"));

        config.Validate();
        return config;
    }

    /// <summary>
    /// symbols.json → SymbolsRegistry → TradingConfig. Пустой Symbols не затирает список из appsettings.
    /// В торговый список и в лимиты попадают только включённые пары (Enabled=false остаётся в реестре).
    /// </summary>
    private static void ApplySymbolsRegistry(AppConfig config, string path)
    {
        var registry = SymbolsRegistryLoader.TryLoad(path, config.Trading.QuoteAssets, config.Trading.WatchAssets);
        if (registry == null) return;

        if (registry.Symbols.Count == 0)
        {
            // Файл содержит только списки активов — торговые пары остаются из appsettings.
            config.Trading.QuoteAssets = SymbolNaming.NormalizeAssets(registry.QuoteAssets);
            config.Trading.WatchAssets = SymbolNaming.NormalizeAssets(registry.WatchAssets, registry.QuoteAssets);
            return;
        }

        // Единственная проверка реестра: нормализация BASE+QUOTE, дубликаты, лимиты, сумма активных ≤ 100%.
        registry.Validate();

        config.Trading.QuoteAssets = registry.QuoteAssets;
        config.Trading.WatchAssets = registry.WatchAssets;
        config.Trading.VolatileSymbols = registry.VolatileSymbols;
        config.Trading.StablecoinPairs = registry.StablecoinPairs;
        config.Trading.BenchmarkSymbols = registry.BenchmarkSymbols;
        config.Trading.SymbolExclusions.Clear();

        var enabled = registry.Symbols.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0)
            throw new InvalidOperationException("symbols.json: все торговые пары отключены (Enabled=false).");

        var candidates = new List<string>();
        foreach (var symbol in enabled)
        {
            var reason = SymbolUniversePolicy.EntryExclusionReason(symbol.Symbol,
                registry.VolatileSymbols, registry.StablecoinPairs, registry.BenchmarkSymbols);
            if (reason != null)
            {
                config.Trading.SymbolExclusions[symbol.Symbol] = reason;
                continue;
            }
            candidates.Add(symbol.Symbol);
        }
        foreach (var symbol in registry.Symbols.Where(s => !s.Enabled))
            config.Trading.SymbolExclusions[symbol.Symbol] = "registry_disabled";
        if (candidates.Count == 0)
            throw new InvalidOperationException("symbols.json: нет торговых кандидатов после исключения stablecoin/benchmark пар.");
        config.Trading.Symbols = candidates;
        foreach (var symbol in enabled.Where(s => candidates.Contains(s.Symbol)))
        {
            config.Trading.SymbolPositionLimits[symbol.Symbol] = symbol.MaxPositionPercent;
            if (symbol.PriceDecimals.HasValue)
                config.Trading.SymbolPriceDecimals[symbol.Symbol] = symbol.PriceDecimals.Value;
            // P1.5: точечные пороги ликвидности (спред/глубина/объём) для тонких пар.
            if (symbol.ToLiquidityOverride() is { } liquidity)
                config.Trading.SymbolLiquidityOverrides[symbol.Symbol] = liquidity;
        }
    }
}
