using System.Globalization;
using AITradingSystem.Core.Models.Trading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITradingSystem.UI.ViewModels;

/// <summary>
/// Phase 1.7: строка списка external-кандидатов. Оборачивает immutable-кандидат сервиса
/// и добавляет единственное редактируемое поле — ручную цену входа (entry override).
/// Пустое поле = использовать предложенную цену (myTrades VWAP или оценка по mark-price).
/// </summary>
public partial class ExternalCandidateRow : ObservableObject
{
    public ExternalCandidateRow(ExternalPositionCandidate candidate)
    {
        Candidate = candidate;
    }

    public ExternalPositionCandidate Candidate { get; }

    [ObservableProperty]
    private string _manualEntryPrice = string.Empty;

    public string Symbol => Candidate.Symbol;
    public decimal ExternalQuantity => Candidate.ExternalQuantity;
    public decimal BotQuantity => Candidate.BotQuantity;
    public decimal NotionalQuote => Candidate.ExternalNotionalQuote;
    public decimal ProposedEntryPrice => Candidate.ProposedEntryPrice;
    public decimal ProposedStopLoss => Candidate.ProposedStopLoss;
    public decimal ProposedTakeProfit => Candidate.ProposedTakeProfit;
    public string EntryBasis => Candidate.EntryBasis;
    public string Status => Candidate.Status;
    public bool HasExternalOpenOrders => Candidate.HasExternalOpenOrders;
    public string EntryEvidence => Candidate.EntryEvidence;

    /// <summary>Подсказка оператору: оценка ≠ доказанный вход, PnL от неё недостоверен.</summary>
    public string EntryWarning => Candidate.EntryBasis switch
    {
        "mytrades-vwap" => "вход восстановлен по myTrades (VWAP)",
        "manual" => "вход задан оператором",
        _ => "ВНИМАНИЕ: цена входа оценочная (mark-price), PnL по ней не доказан"
    };

    /// <summary>Разбор ручного ввода. null = не задано; false = некорректное значение.</summary>
    public bool TryGetEntryOverride(out decimal? entry)
    {
        entry = null;
        var text = ManualEntryPrice?.Trim();
        if (string.IsNullOrEmpty(text)) return true;
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            !decimal.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return false;
        if (value <= 0m) return false;
        entry = value;
        return true;
    }
}
