using AITradingSystem.Core.Indicators;
using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Tests;

public class IndicatorTests
{
    private static OHLCV Candle(decimal close, decimal high, decimal low, decimal volume = 100m, int minute = 0) => new()
    {
        Symbol = "BTCUSDT",
        Open = close,
        High = high,
        Low = low,
        Close = close,
        Volume = volume,
        OpenTime = new DateTime(2025, 1, 1, 0, minute, 0, DateTimeKind.Utc),
        CloseTime = new DateTime(2025, 1, 1, 0, minute, 59, DateTimeKind.Utc)
    };

    [Fact]
    public void EMA_Converges_To_Constant_Price()
    {
        var ema = new EMA(20);
        for (int i = 0; i < 300; i++)
            ema.Update(Candle(100m, 101m, 99m, minute: i % 60));

        Assert.True(ema.IsReady);
        Assert.Equal(100m, ema.GetValue(), 2);
    }

    [Fact]
    public void RSI_All_Up_Moves_Approaches_100()
    {
        var rsi = new RSI(14);
        for (int i = 0; i < 100; i++)
            rsi.Update(Candle(100m + i, 101m + i, 99m + i, minute: i % 60));

        Assert.True(rsi.IsReady);
        Assert.Equal(100m, rsi.GetValue(), 0);
    }

    [Fact]
    public void RSI_Stays_In_Range()
    {
        var rsi = new RSI(14);
        var rnd = new Random(42);
        var price = 100m;
        for (int i = 0; i < 500; i++)
        {
            price += (decimal)(rnd.NextDouble() - 0.5) * 5m;
            rsi.Update(Candle(price, price + 1m, price - 1m, minute: i % 60));
            Assert.InRange(rsi.GetValue(), 0m, 100m);
        }
    }

    [Fact]
    public void Calculator_AllReady_After_300_Candles()
    {
        var calc = new IndicatorCalculator("BTCUSDT");
        var rnd = new Random(7);
        var price = 50000m;
        for (int i = 0; i < 300; i++)
        {
            price += (decimal)(rnd.NextDouble() - 0.5) * 200m;
            calc.Update(Candle(price, price + 50m, price - 50m, volume: 10m + i, minute: i % 60));
        }

        Assert.True(calc.AllReady);

        var snapshot = calc.GetNormalizedSnapshot(Candle(price, price + 50m, price - 50m));
        var dict = snapshot.ToDictionary();

        Assert.True(dict.ContainsKey("price"));
        Assert.True(dict.ContainsKey("atr_pct"));
        Assert.InRange(dict["rsi"], -1m, 1m);
        Assert.InRange(dict["adx"], 0m, 1m);
        Assert.InRange(dict["stoch_rsi_k"], 0m, 1m);
        Assert.InRange(dict["obv_trend"], -1m, 1m);
    }

    [Fact]
    public void VolumeRatio_Uses_Last_Closed_Candle_Not_Live_Partial()
    {
        var calc = new IndicatorCalculator("BTCUSDT");
        for (int i = 0; i < 20; i++)
            calc.Update(Candle(100m, 101m, 99m, volume: 100m, minute: i));
        calc.Update(Candle(100m, 101m, 99m, volume: 200m, minute: 20));

        // SMA20 = (19 * 100 + 200) / 20 = 105.
        var expected = 200m / 105m;
        Assert.Equal(expected, calc.VolumeRatio);

        var live = Candle(102m, 103m, 101m, volume: 1m, minute: 21);
        live.CloseTime = DateTime.UtcNow.AddMinutes(5);
        Assert.False(live.IsClosed);

        // Снапшот может использовать цену тика, но не его накопленный объём.
        var snapshot = calc.GetNormalizedSnapshot(live);
        Assert.Equal(102m, snapshot.Price);
        Assert.Equal(expected, snapshot.VolumeRatio);
        Assert.Equal(expected, snapshot.ToDictionary()["volume_ratio"]);
        Assert.Equal(200m, calc.LastClosedVolume);
        Assert.Equal(105m, calc.VolumeSma20);
    }

    [Fact]
    public void VolumeRatio_Follows_Rolling_Closed_Volume_Window()
    {
        var calc = new IndicatorCalculator("BTCUSDT");
        for (int i = 0; i < 20; i++)
            calc.Update(Candle(100m, 101m, 99m, volume: 100m, minute: i));
        calc.Update(Candle(100m, 101m, 99m, volume: 200m, minute: 20));
        Assert.Equal(200m / 105m, calc.VolumeRatio);

        for (int i = 21; i <= 40; i++)
            calc.Update(Candle(100m, 101m, 99m, volume: 100m, minute: i));

        Assert.Equal(100m, calc.VolumeSma20);
        Assert.Equal(100m, calc.LastClosedVolume);
        Assert.Equal(1m, calc.VolumeRatio);
    }

    [Fact]
    public void VolumeRatio_Reset_Clears_Closed_Volume()
    {
        var calc = new IndicatorCalculator("BTCUSDT");
        calc.Update(Candle(100m, 101m, 99m));
        Assert.Equal(1m, calc.VolumeRatio);

        calc.Reset();

        Assert.Equal(0m, calc.LastClosedVolume);
        Assert.Equal(0m, calc.VolumeSma20);
        Assert.Equal(0m, calc.VolumeRatio);
    }

    [Fact]
    public void StochRSI_D_Differs_From_K_When_Moving()
    {
        var stoch = new StochasticRsi(14, 14, 3, 3);
        var rnd = new Random(11);
        var price = 100m;
        for (int i = 0; i < 200; i++)
        {
            price += (decimal)(rnd.NextDouble() - 0.5) * 4m;
            stoch.Update(Candle(price, price + 1m, price - 1m, minute: i % 60));
        }

        Assert.InRange(stoch.K, 0m, 100m);
        Assert.InRange(stoch.D, 0m, 100m);
    }
}
