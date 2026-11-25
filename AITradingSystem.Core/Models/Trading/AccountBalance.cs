namespace AITradingSystem.Core.Models.Trading;

public sealed class AssetBalance
{
    public string Asset { get; set; } = string.Empty;
    public decimal Free { get; set; }
    public decimal Locked { get; set; }
    public decimal Total => Free + Locked;
}

public sealed class AccountBalance
{
    public decimal TotalUSDT { get; set; }
    public decimal AvailableUSDT { get; set; }
    public decimal LockedUSDT { get; set; }
    /// <summary>Совместимость: свободные количества активов.</summary>
    public Dictionary<string, decimal> AssetBalances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Полные остатки, включая заблокированные ордерами средства.</summary>
    public Dictionary<string, AssetBalance> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public void UpdateAsset(string asset, decimal free, decimal locked)
    {
        if (string.IsNullOrWhiteSpace(asset)) return;
        asset = asset.Trim().ToUpperInvariant();
        AssetBalances[asset] = free;
        Assets[asset] = new AssetBalance { Asset = asset, Free = free, Locked = locked };
        if (asset == "USDT")
        {
            AvailableUSDT = free; LockedUSDT = locked; TotalUSDT = free + locked;
        }
    }
}
