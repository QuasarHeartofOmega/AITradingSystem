namespace AITradingSystem.Common.Configuration;

/// <summary>
/// Конфигурация источников новостей (FR-2).
/// Поддерживаются: cryptocurrency.cv (бесплатный агрегатор, без ключа) и CryptoPanic (по ключу).
/// </summary>
public sealed class NewsConfig
{
    /// <summary>API ключ CryptoPanic (FR-2.1). Пусто — провайдер отключён.</summary>
    public string CryptoPanicApiKey { get; set; } = string.Empty;

    /// <summary>Включить провайдер CryptoPanic.</summary>
    public bool CryptoPanicEnabled { get; set; } = true;

    /// <summary>Базовый URL CryptoPanic API (v1 legacy / v2 developer).</summary>
    public string CryptoPanicBaseUrl { get; set; } = "https://cryptopanic.com/api/developer/v2/";

    /// <summary>Включить бесплатный агрегатор cryptocurrency.cv.</summary>
    public bool CryptocurrencyCvEnabled { get; set; } = true;

    /// <summary>Базовый URL агрегатора cryptocurrency.cv.</summary>
    public string CryptocurrencyCvBaseUrl { get; set; } = "https://cryptocurrency.cv/api/";

    /// <summary>Сколько статей запрашивать у cryptocurrency.cv за раз.</summary>
    public int CryptocurrencyCvLimit { get; set; } = 60;

    /// <summary>Фильтр категорий cryptocurrency.cv (пусто = все).</summary>
    public string CryptocurrencyCvCategory { get; set; } = string.Empty;

    /// <summary>Минимальная достоверность источника (credibility 0..1) для cryptocurrency.cv.</summary>
    public double CryptocurrencyCvMinCredibility { get; set; } = 0.0;

    /// <summary>Интервал обновления новостей в минутах (FR-2.2).</summary>
    public int UpdateIntervalMinutes { get; set; } = 5;

    /// <summary>Максимальное количество новостей на один актив за цикл.</summary>
    public int MaxNewsPerSymbol { get; set; } = 10;

    /// <summary>Максимальный возраст новости в часах (устаревшие отбрасываются).</summary>
    public int MaxNewsAgeHours { get; set; } = 24;

    /// <summary>
    /// P2.8: подмешивать общий фон (GENERAL), когда по активу новостей нет.
    /// По умолчанию false: нерелевантные заголовки (BBC/ZeroHedge/…) уходили в промпт
    /// каждой монеты как «новости по активу» — шум вместо независимых свидетельств.
    /// </summary>
    public bool AllowGeneralNewsFallback { get; set; } = false;

    /// <summary>Таймаут HTTP-запросов к новостным API, мс.</summary>
    public int HttpTimeoutMs { get; set; } = 20_000;

    /// <summary>Включен ли анализ новостей.</summary>
    public bool Enabled { get; set; } = true;

    public void Validate()
    {
        if (UpdateIntervalMinutes < 1 || UpdateIntervalMinutes > 60)
            throw new InvalidOperationException("UpdateIntervalMinutes должен быть в диапазоне [1; 60].");

        if (MaxNewsPerSymbol < 1 || MaxNewsPerSymbol > 50)
            throw new InvalidOperationException("MaxNewsPerSymbol должен быть в диапазоне [1; 50].");

        if (MaxNewsAgeHours < 1 || MaxNewsAgeHours > 720)
            throw new InvalidOperationException("MaxNewsAgeHours должен быть в диапазоне [1; 720].");

        if (HttpTimeoutMs < 1000 || HttpTimeoutMs > 120_000)
            throw new InvalidOperationException("News:HttpTimeoutMs должен быть в диапазоне [1000; 120000].");

        if (CryptocurrencyCvLimit < 1 || CryptocurrencyCvLimit > 500)
            throw new InvalidOperationException("CryptocurrencyCvLimit должен быть в диапазоне [1; 500].");

        if (CryptocurrencyCvMinCredibility < 0 || CryptocurrencyCvMinCredibility > 1)
            throw new InvalidOperationException("CryptocurrencyCvMinCredibility должен быть в диапазоне [0; 1].");

        if (CryptocurrencyCvEnabled && string.IsNullOrWhiteSpace(CryptocurrencyCvBaseUrl))
            throw new InvalidOperationException("CryptocurrencyCvBaseUrl не указан.");
    }
}
