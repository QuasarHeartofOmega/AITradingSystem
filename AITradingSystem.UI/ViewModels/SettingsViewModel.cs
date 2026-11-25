using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Security;
using AITradingSystem.Core.Models.Config;
using AITradingSystem.Services.AI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AITradingSystem.UI.ViewModels;

public partial class SymbolSetting : ObservableObject
{
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private decimal _maxPositionPercent = 5m;
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly IAIModel _model;
    public ObservableCollection<SymbolSetting> SymbolItems { get; } = new();
    [ObservableProperty] private string _watchAssetsList;
    [ObservableProperty] private string _quoteAssetsList;
    [ObservableProperty] private double _riskPerTrade;
    [ObservableProperty] private double _maxDrawdown;
    [ObservableProperty] private bool _paperTradingMode;
    [ObservableProperty] private string _binanceEnvironment;
    [ObservableProperty] private string _aiBackend;
    [ObservableProperty] private bool _enableThinking;
    [ObservableProperty] private bool _aiCanClosePositions;
    [ObservableProperty] private bool _cryptocurrencyCvEnabled;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _backendInfo = "Модель ещё не загружена";

    /// <summary>P0.7: выбран Live, но переход ещё не подтверждён вторым шагом.</summary>
    [ObservableProperty] private bool _isLiveConfirmPending;

    /// <summary>P0.7: фраза подтверждения (нужно ввести LIVE).</summary>
    [ObservableProperty] private string _liveConfirmPhrase = string.Empty;

    private bool _isLiveConfirmed;

    /// <summary>P0.7: ввод отдельного Live API Key (не сохраняется в открытом виде).</summary>
    [ObservableProperty] private string _liveApiKeyInput = string.Empty;
    /// <summary>P0.7: ввод отдельного Live API Secret.</summary>
    [ObservableProperty] private string _liveApiSecretInput = string.Empty;

    public IReadOnlyList<string> BinanceEnvironments { get; } = new[] { "Demo", "Testnet", "Live" };
    public IReadOnlyList<string> AiBackends { get; } = new[] { "Auto", "Cuda12", "Vulkan", "Cpu" };

    partial void OnBinanceEnvironmentChanged(string value)
    {
        // Смена окружения сбрасывает прежнее подтверждение Live.
        _isLiveConfirmed = false;
        IsLiveConfirmPending = value == "Live" &&
            _config.Binance.ResolvedEnvironment != AITradingSystem.Common.Configuration.BinanceEnvironment.Live;
        LiveConfirmPhrase = string.Empty;
    }

    [RelayCommand]
    private void ConfirmLive()
    {
        if (!string.Equals(LiveConfirmPhrase.Trim(), "LIVE", StringComparison.Ordinal))
        {
            StatusText = "Подтверждение Live отклонено: введите точную фразу LIVE.";
            return;
        }
        _isLiveConfirmed = true;
        IsLiveConfirmPending = false;
        StatusText = "Переход в Live подтверждён вторым шагом. Нажмите «Сохранить» и перезапустите приложение.";
    }

    public SettingsViewModel(AppConfig config, IAIModel model)
    {
        _config = config; _model = model;
        var registry = LoadRegistry();
        foreach (var item in registry.Symbols)
            SymbolItems.Add(new SymbolSetting { Symbol = item.Symbol, IsEnabled = item.Enabled, MaxPositionPercent = item.MaxPositionPercent });
        _watchAssetsList = string.Join(", ", config.Trading.WatchAssets);
        _quoteAssetsList = string.Join(", ", config.Trading.QuoteAssets);
        _riskPerTrade = config.Trading.RiskPerTrade * 100;
        _maxDrawdown = config.Trading.MaxDrawdown * 100;
        _paperTradingMode = config.Trading.PaperTradingMode;
        _binanceEnvironment = config.Binance.ResolvedEnvironment.ToString();
        _aiBackend = config.AI.ResolvedBackend.ToString();
        _enableThinking = config.AI.EnableThinking;
        _aiCanClosePositions = config.Trading.AiCanClosePositions;
        _cryptocurrencyCvEnabled = config.News.CryptocurrencyCvEnabled;
        RefreshBackendInfo();
    }

    private static string ConfigPath(string file) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private SymbolsRegistry LoadRegistry()
    {
        // D23: тот же разбор symbols.json, что и в ConfigLoader (legacy-строки, комментарии,
        // fallback котировок). Повреждённый файл не должен ронять конструктор VM.
        try
        {
            var saved = SymbolsRegistryLoader.TryLoad(ConfigPath("symbols.json"),
                _config.Trading.QuoteAssets, _config.Trading.WatchAssets);
            if (saved is { Symbols.Count: > 0 }) return saved;
        }
        catch (Exception ex)
        {
            StatusText = "symbols.json: " + ex.Message + " Показан список из appsettings.";
        }
        return new SymbolsRegistry
        {
            Symbols = _config.Trading.Symbols.Select(s => new SymbolConfig { Symbol = s, MaxPositionPercent = 5m }).ToList(),
            WatchAssets = new(_config.Trading.WatchAssets), QuoteAssets = new(_config.Trading.QuoteAssets),
            VolatileSymbols = new(_config.Trading.VolatileSymbols),
            StablecoinPairs = new(_config.Trading.StablecoinPairs),
            BenchmarkSymbols = new(_config.Trading.BenchmarkSymbols)
        };
    }
    public void RefreshBackendInfo()
    {
        BackendInfo = _model is QwenModel q
            ? $"{q.BackendSummary}; AutoFit={_config.AI.AutoFitGpu}, VRAM reserve={_config.AI.VramReserveMb} MiB" +
              (string.IsNullOrEmpty(q.LoadWarning) ? string.Empty : "; " + q.LoadWarning)
            : $"Конфигурация: {_config.AI.Backend}; AutoFit={_config.AI.AutoFitGpu}";
    }
    [RelayCommand] private void AddSymbol()
    {
        if (SymbolItems.Count < 32) SymbolItems.Add(new SymbolSetting());
        else StatusText = "Максимум 32 пары.";
    }
    [RelayCommand] private void RemoveSymbol(SymbolSetting? item) { if (item != null) SymbolItems.Remove(item); }
    [RelayCommand] private void Save()
    {
        // P0.7: двухшаговая защита от случайного Live — без явного подтверждения не сохраняем.
        if (BinanceEnvironment == "Live" &&
            _config.Binance.ResolvedEnvironment != AITradingSystem.Common.Configuration.BinanceEnvironment.Live &&
            !_isLiveConfirmed)
        {
            StatusText = "Сохранение заблокировано: переход в Live не подтверждён. Введите LIVE и нажмите «Подтвердить Live».";
            return;
        }

        try
        {
            var registry = LoadRegistry();
            registry.WatchAssets = WatchAssetsList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            registry.QuoteAssets = QuoteAssetsList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            registry.Symbols = SymbolItems.Select((item, i) => {
                var old = registry.Symbols.FirstOrDefault(s => s.Symbol.Equals(item.Symbol.Trim(), StringComparison.OrdinalIgnoreCase));
                return new SymbolConfig { Symbol = item.Symbol, Enabled = item.IsEnabled, MaxPositionPercent = item.MaxPositionPercent,
                    Priority = old?.Priority ?? i, Notes = old?.Notes ?? string.Empty, PriceDecimals = old?.PriceDecimals };
            }).ToList();
            registry.UpdatedAt = DateTime.UtcNow;
            registry.Validate();
            var trading = new TradingConfig
            {
                Symbols = registry.Symbols.Where(s => s.Enabled).Select(s => s.Symbol).ToList(),
                VolatileSymbols = new(registry.VolatileSymbols),
                StablecoinPairs = new(registry.StablecoinPairs),
                BenchmarkSymbols = new(registry.BenchmarkSymbols),
                WatchAssets = new(registry.WatchAssets), QuoteAssets = new(registry.QuoteAssets),
                RiskPerTrade = RiskPerTrade / 100, MaxDrawdown = MaxDrawdown / 100,
                PaperTradingMode = PaperTradingMode, AiCanClosePositions = AiCanClosePositions,
                AccountRefreshIntervalSeconds = _config.Trading.AccountRefreshIntervalSeconds,
                MinExchangePositionNotional = _config.Trading.MinExchangePositionNotional
            };
            // Сначала проверяем классы, затем исключаем неторговые пары из fallback-списка.
            SymbolUniversePolicy.ValidateLists(trading.VolatileSymbols, trading.StablecoinPairs, trading.BenchmarkSymbols);
            trading.SymbolExclusions.Clear();
            foreach (var symbol in trading.Symbols.ToList())
            {
                var reason = SymbolUniversePolicy.EntryExclusionReason(symbol,
                    trading.VolatileSymbols, trading.StablecoinPairs, trading.BenchmarkSymbols);
                if (reason != null)
                {
                    trading.SymbolExclusions[symbol] = reason;
                    trading.Symbols.Remove(symbol);
                }
            }
            foreach (var symbol in registry.Symbols.Where(s => !s.Enabled))
                trading.SymbolExclusions[symbol.Symbol] = "registry_disabled";
            trading.Validate();
            var path = ConfigPath("appsettings.json");
            var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject() : new JsonObject();
            var section = root["Trading"]?.AsObject() ?? new JsonObject();
            section["Symbols"] = new JsonArray(trading.Symbols.Select(s => JsonValue.Create(s)).ToArray());
            section["VolatileSymbols"] = new JsonArray(trading.VolatileSymbols.Select(s => JsonValue.Create(s)).ToArray());
            section["StablecoinPairs"] = new JsonArray(trading.StablecoinPairs.Select(s => JsonValue.Create(s)).ToArray());
            section["BenchmarkSymbols"] = new JsonArray(trading.BenchmarkSymbols.Select(s => JsonValue.Create(s)).ToArray());
            section["SymbolExclusions"] = new JsonObject(trading.SymbolExclusions.Select(x => new KeyValuePair<string, JsonNode?>(x.Key, JsonValue.Create(x.Value))));
            section["WatchAssets"] = new JsonArray(registry.WatchAssets.Select(s => JsonValue.Create(s)).ToArray());
            section["QuoteAssets"] = new JsonArray(registry.QuoteAssets.Select(s => JsonValue.Create(s)).ToArray());
            section["SymbolPositionLimits"] = new JsonObject(registry.Symbols.Where(s => s.Enabled && trading.Symbols.Contains(s.Symbol)).Select(s => new KeyValuePair<string, JsonNode?>(s.Symbol, JsonValue.Create(s.MaxPositionPercent))));
            section["RiskPerTrade"] = trading.RiskPerTrade; section["MaxDrawdown"] = trading.MaxDrawdown;
            section["PaperTradingMode"] = PaperTradingMode; section["AiCanClosePositions"] = AiCanClosePositions;
            root["Trading"] = section;
            var binance = root["Binance"]?.AsObject() ?? new JsonObject();
            binance["Environment"] = BinanceEnvironment; binance["UseTestnet"] = BinanceEnvironment == "Testnet";
            // P0.7: отдельные Live-ключи шифруются тем же ApiKeyEncryptor, что и общие.
            if (!string.IsNullOrWhiteSpace(LiveApiKeyInput))
                binance["EncryptedLiveApiKey"] = ApiKeyEncryptor.Encrypt(LiveApiKeyInput);
            if (!string.IsNullOrWhiteSpace(LiveApiSecretInput))
                binance["EncryptedLiveApiSecret"] = ApiKeyEncryptor.Encrypt(LiveApiSecretInput);
            root["Binance"] = binance;
            var ai = root["AI"]?.AsObject() ?? new JsonObject(); ai["Backend"] = AiBackend; ai["EnableThinking"] = EnableThinking; root["AI"] = ai;
            var news = root["News"]?.AsObject() ?? new JsonObject(); news["CryptocurrencyCvEnabled"] = CryptocurrencyCvEnabled; root["News"] = news;
            // Сначала реестр, затем appsettings; UI сообщает о необходимости перезапуска.
            File.WriteAllText(ConfigPath("symbols.json"), JsonSerializer.Serialize(registry, JsonOptions));
            File.WriteAllText(path, root.ToJsonString(JsonOptions));
            LiveApiKeyInput = string.Empty; LiveApiSecretInput = string.Empty;
            StatusText = "Сохранено. Перезапустите приложение: список пар, режим, ключи и GPU применяются при старте.";
        }
        catch (Exception ex) { StatusText = "Ошибка сохранения: " + ex.Message; }
    }
}
