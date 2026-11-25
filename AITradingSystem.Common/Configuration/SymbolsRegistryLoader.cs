using System.Text.Json;
using System.Text.Json.Nodes;
using AITradingSystem.Core.Models.Config;

namespace AITradingSystem.Common.Configuration;

/// <summary>
/// D23: единая точка чтения symbols.json для конфигурации (ConfigLoader) и экрана настроек
/// (SettingsViewModel). Обе стороны получают одну и ту же модель <see cref="SymbolsRegistry"/>,
/// поэтому нормализация, дубликаты и сумма активных лимитов проверяются один раз — в Validate().
/// Legacy-формат («Symbols»: ["BTCUSDT", ...]) поддерживается: строка превращается в объект реестра.
/// </summary>
public static class SymbolsRegistryLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// null — файла нет (используется список из appsettings).
    /// Повреждённый JSON — InvalidOperationException: торговля не должна стартовать с неожиданным списком.
    /// Списки активов: файл главный, значения из appsettings — fallback (в т.ч. когда ключа нет в файле,
    /// ведь SymbolsRegistry.QuoteAssets по умолчанию заполнен DefaultQuotes).
    /// </summary>
    public static SymbolsRegistry? TryLoad(
        string path,
        IEnumerable<string>? fallbackQuoteAssets = null,
        IEnumerable<string>? fallbackWatchAssets = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path), nodeOptions: null, documentOptions: DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"symbols.json повреждён: {ex.Message}", ex);
        }

        if (node is not JsonObject root)
            throw new InvalidOperationException("symbols.json: ожидался JSON-объект реестра символов.");

        if (root["Symbols"] is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue(out string? legacy) && legacy != null)
                    array[i] = new JsonObject { ["Symbol"] = legacy };
            }
        }

        var registry = root.Deserialize<SymbolsRegistry>(JsonOptions)
                       ?? throw new InvalidOperationException("symbols.json: реестр не прочитан.");

        // JSON мог содержать явные null — реестр остаётся валидной пустой моделью.
        if (registry.Symbols is null) registry.Symbols = new List<SymbolConfig>();
        if (registry.QuoteAssets is null) registry.QuoteAssets = new List<string>();
        if (registry.WatchAssets is null) registry.WatchAssets = new List<string>();

        if (!root.ContainsKey("QuoteAssets") || registry.QuoteAssets.Count == 0)
            registry.QuoteAssets = SymbolNaming.NormalizeAssets(fallbackQuoteAssets);
        if (!root.ContainsKey("WatchAssets") || registry.WatchAssets.Count == 0)
            registry.WatchAssets = fallbackWatchAssets is null
                ? new List<string>()
                : SymbolNaming.NormalizeAssets(fallbackWatchAssets);

        return registry;
    }
}
