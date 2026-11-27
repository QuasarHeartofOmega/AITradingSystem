using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Data.Storage;

/// <summary>
/// E1 append-only JSONL ledger. Single application writer; durable flush before returning.
/// Corrupt partitions fail closed. This is statistics/evidence storage, not the P0.3
/// write-ahead execution-intent journal and not a replacement for human trading logs.
/// </summary>
public sealed class JsonE1Ledger : IE1Ledger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly string _environment;
    private readonly string _accountFingerprint;
    private readonly LedgerSource _source;
    private readonly LedgerFile<DecisionLedgerRecord> _decisions;
    private readonly LedgerFile<FillLedgerRecord> _fills;
    private readonly LedgerFile<OutcomeLedgerRecord> _outcomes;

    public JsonE1Ledger(string directoryPath, string environment, string accountFingerprint, LedgerSource source)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) throw new ArgumentException("Directory is required", nameof(directoryPath));
        _environment = SafeSegment(environment, nameof(environment));
        _accountFingerprint = SafeSegment(accountFingerprint, nameof(accountFingerprint));
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        _source = source;

        var partition = Path.Combine(directoryPath, "ledger",
            $"{_environment.ToLowerInvariant()}-{source.ToString().ToLowerInvariant()}", _accountFingerprint);
        Directory.CreateDirectory(partition);
        _decisions = new(Path.Combine(partition, "decisions.jsonl"), r => r.DecisionId, ValidateDecision, (_, _) => false);
        _fills = new(Path.Combine(partition, "fills.jsonl"), r => r.FillId, ValidateFill, (_, _) => false);
        _outcomes = new(Path.Combine(partition, "outcomes.jsonl"), r => r.LifecycleId, ValidateOutcome,
            MayReplaceOutcome);
    }

    /// <summary>
    /// An open (unresolved) lifecycle is refined in place: protection attach/resolve, trailing replace,
    /// partial fills, reconcile-open. Its identity is immutable, its evidence is not.
    /// A resolved lifecycle (closed/no-fill/…) is final evidence and is never rewritten.
    /// </summary>
    private static bool MayReplaceOutcome(OutcomeLedgerRecord old, OutcomeLedgerRecord next) =>
        old.Status == OutcomeStatus.Unresolved
        && string.Equals(old.Symbol, next.Symbol, StringComparison.Ordinal)
        && string.Equals(old.EntryIntentId, next.EntryIntentId, StringComparison.Ordinal)
        && string.Equals(old.DecisionId, next.DecisionId, StringComparison.Ordinal)
        && old.Side == next.Side;

    public Task<bool> RecordDecisionAsync(DecisionLedgerRecord record, CancellationToken ct = default) =>
        _decisions.AppendAsync(record, ct);

    public async Task<bool> RecordFillAsync(FillLedgerRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_source is LedgerSource.Shadow)
            throw new InvalidOperationException("Shadow decisions have no execution fills");
        return await _fills.AppendAsync(record, ct).ConfigureAwait(false);
    }

    public async Task<bool> RecordOutcomeAsync(OutcomeLedgerRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_source is LedgerSource.Shadow)
            throw new InvalidOperationException("Shadow decisions have no execution outcomes");
        return await _outcomes.AppendAsync(record, ct, async () =>
        {
            var fills = (await _fills.LoadAsync(ct).ConfigureAwait(false)).ToDictionary(f => f.FillId, StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(record.DecisionId))
            {
                var decisions = (await _decisions.LoadAsync(ct).ConfigureAwait(false)).ToDictionary(d => d.DecisionId, StringComparer.Ordinal);
                if (!decisions.TryGetValue(record.DecisionId, out var decision) || decision.Symbol != record.Symbol)
                    throw new LedgerConflictException($"Outcome {record.LifecycleId} references an unknown/mismatched decision");
            }
            ValidateLinkedFills(record, fills);
        }).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DecisionLedgerRecord>> LoadDecisionsAsync(CancellationToken ct = default) => _decisions.LoadAsync(ct);
    public Task<IReadOnlyList<FillLedgerRecord>> LoadFillsAsync(CancellationToken ct = default) => _fills.LoadAsync(ct);
    public Task<IReadOnlyList<OutcomeLedgerRecord>> LoadOutcomesAsync(CancellationToken ct = default) => _outcomes.LoadAsync(ct);

    private void ValidateCommon(LedgerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        foreach (var value in new[] { record.RunId, record.ConfigHash, record.BuildHash, record.Environment,
                     record.AccountFingerprint, record.QuoteAsset, record.Symbol })
            if (string.IsNullOrWhiteSpace(value)) throw new LedgerConflictException("E1 identity/context fields are required");
        if (!string.Equals(record.Environment, _environment, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(record.AccountFingerprint, _accountFingerprint, StringComparison.Ordinal)
            || record.Source != _source)
            throw new LedgerConflictException("E1 record belongs to a different environment/source/account partition");
        if (!Enum.IsDefined(record.Source) || record.RecordedAtUtc.Kind != DateTimeKind.Utc)
            throw new LedgerConflictException("E1 source/timestamp is invalid");
    }

    private void ValidateDecision(DecisionLedgerRecord r)
    {
        ValidateCommon(r);
        RequireId(r.DecisionId); RequireId(r.GateStage); RequireId(r.ReasonCode);
        if (!Enum.IsDefined(r.Outcome) || !Enum.IsDefined(r.AiDisposition))
            throw new LedgerConflictException("Invalid decision outcome/AI disposition");
        if (r.CandleCloseUtc is { Kind: not DateTimeKind.Utc })
            throw new LedgerConflictException("candle_close_utc must be UTC");
    }

    private void ValidateFill(FillLedgerRecord r)
    {
        ValidateCommon(r);
        RequireId(r.FillId); RequireId(r.ClientOrderId);
        if (!Enum.IsDefined(r.Role) || !Enum.IsDefined(r.Evidence) || !Enum.IsDefined(r.Side)
            || !Enum.IsDefined(r.FeeValuation))
            throw new LedgerConflictException("Invalid fill enum");
        if (r.Quantity <= 0 || r.CumulativeFilledQuantity < r.Quantity || r.AverageFillPrice <= 0
            || r.Fee < 0 || r.RequestedQuantity < 0 || r.RoundedQuantity < 0)
            throw new LedgerConflictException("Fill quantity/price/fee is invalid");
        if (r.ExchangeTimeUtc.Kind != DateTimeKind.Utc)
            throw new LedgerConflictException("exchange_time_utc must be UTC");

        switch (r.Evidence)
        {
            case FillEvidence.ExchangeTrade:
                RequireId(r.ExchangeTradeId!);
                if (_source is not (LedgerSource.Demo or LedgerSource.Live))
                    throw new LedgerConflictException("Exchange trade evidence requires Demo/Live source");
                break;
            case FillEvidence.OrderCumulativeDelta:
                if (_source is not (LedgerSource.Demo or LedgerSource.Live))
                    throw new LedgerConflictException("Exchange cumulative evidence requires Demo/Live source");
                break;
            case FillEvidence.PaperSimulation when _source != LedgerSource.Paper:
                throw new LedgerConflictException("Simulated fill evidence does not match source");
            case FillEvidence.ReplaySimulation when _source != LedgerSource.Replay:
                throw new LedgerConflictException("Simulated fill evidence does not match source");
        }

        switch (r.FeeValuation)
        {
            case FeeValuation.NotApplicable:
                if (r.Fee != 0 || r.FeeQuote != 0)
                    throw new LedgerConflictException("Zero-fee valuation requires fee=0 and fee_quote=0");
                break;
            case FeeValuation.ReportedQuote:
                if (r.Fee <= 0 || r.FeeQuote != r.Fee || !string.Equals(r.FeeAsset, r.QuoteAsset, StringComparison.OrdinalIgnoreCase))
                    throw new LedgerConflictException("Reported quote fee must actually be in quote asset");
                break;
            case FeeValuation.ConvertedQuote:
                if (r.Fee <= 0 || !r.FeeQuote.HasValue || r.FeeQuote <= 0
                    || !r.FeeConversionRate.HasValue || r.FeeConversionRate <= 0
                    || r.FeeConversionTimeUtc is not { Kind: DateTimeKind.Utc }
                    || string.IsNullOrWhiteSpace(r.FeeAsset)
                    || string.Equals(r.FeeAsset, r.QuoteAsset, StringComparison.OrdinalIgnoreCase))
                    throw new LedgerConflictException("Converted fee requires asset, positive rate/value and UTC conversion time");
                CloseTo(r.FeeQuote!.Value, r.Fee * r.FeeConversionRate!.Value, "fee_quote_conversion");
                break;
            case FeeValuation.Unvalued:
                if (r.Fee <= 0 || r.FeeQuote.HasValue)
                    throw new LedgerConflictException("Unvalued fee must preserve fee>0 and null fee_quote");
                break;
        }
    }

    private void ValidateOutcome(OutcomeLedgerRecord r)
    {
        ValidateCommon(r);
        RequireId(r.LifecycleId); RequireId(r.EntryIntentId);
        if (!Enum.IsDefined(r.Status) || !Enum.IsDefined(r.Side))
            throw new LedgerConflictException("Invalid outcome status/side");
        if (r.Status == OutcomeStatus.NoFill)
        {
            if (r.EntryQuantity != 0 || r.ExitQuantity != 0 || r.DustQuantity != 0
                || r.EntryFillIds.Count != 0 || r.ExitFillIds.Count != 0
                || r.RealizedGrossQuote.HasValue || r.RealizedFeesQuote.HasValue || r.RealizedSlippageQuote.HasValue
                || r.RealizedNetQuote.HasValue || r.RealizedNetR.HasValue)
                throw new LedgerConflictException("NO_FILL is not a trade");
            return;
        }
        if (r.EntryQuantity < 0 || r.ExitQuantity < 0 || r.DustQuantity < 0
            || r.ExitQuantity + r.DustQuantity > r.EntryQuantity
            || r.InitialRiskQuote < 0 || r.RealizedFeesQuote < 0)
            throw new LedgerConflictException("Outcome quantity/risk/fee is invalid");
        if (r.ExitTimeUtc is { Kind: not DateTimeKind.Utc } || r.HoldingSeconds < 0 || r.UnprotectedSeconds < 0)
            throw new LedgerConflictException("Outcome time/duration is invalid");
        if (r.Status == OutcomeStatus.Closed)
        {
            if (r.EntryQuantity <= 0 || r.ExitQuantity <= 0
                || !r.AverageEntryPrice.HasValue || r.AverageEntryPrice.Value <= 0
                || !r.AverageExitPrice.HasValue || r.AverageExitPrice.Value <= 0
                || r.EntryFillIds.Count == 0 || r.ExitFillIds.Count == 0
                || r.ExitQuantity + r.DustQuantity != r.EntryQuantity
                || !r.RealizedGrossQuote.HasValue)
                throw new LedgerConflictException("Closed outcome requires actual entry/exit fills, gross and exit+dust accounting");
            if (!r.RealizedFeesQuote.HasValue && (r.RealizedNetQuote.HasValue || r.RealizedNetR.HasValue))
                throw new LedgerConflictException("Unknown fees cannot produce a known net outcome");
            var gross = (r.Side == Core.Models.Enums.OrderSide.Buy
                ? r.AverageExitPrice!.Value - r.AverageEntryPrice!.Value
                : r.AverageEntryPrice!.Value - r.AverageExitPrice!.Value) * r.ExitQuantity;
            CloseTo(r.RealizedGrossQuote.Value, gross, "gross");
            if (r.RealizedFeesQuote.HasValue)
            {
                if (!r.RealizedNetQuote.HasValue) throw new LedgerConflictException("Known fees require net quote");
                CloseTo(r.RealizedNetQuote.Value, gross - r.RealizedFeesQuote.Value, "net");
                if (r.InitialRiskQuote > 0)
                {
                    if (!r.RealizedNetR.HasValue) throw new LedgerConflictException("Known risk requires net R");
                    CloseTo(r.RealizedNetR.Value, r.RealizedNetQuote.Value / r.InitialRiskQuote.Value, "net_r");
                }
            }
        }
    }

    private void ValidateLinkedFills(OutcomeLedgerRecord r, IReadOnlyDictionary<string, FillLedgerRecord> fills)
    {
        if (r.Status == OutcomeStatus.NoFill)
        {
            if (string.IsNullOrWhiteSpace(r.DecisionId))
                throw new LedgerConflictException("NO_FILL requires its decision identity");
            return;
        }
        if (r.Status != OutcomeStatus.Closed) return;
        var linked = new List<FillLedgerRecord>();
        decimal entryQty = 0, exitQty = 0, fees = 0;
        var allFeesValued = true;
        foreach (var (ids, isEntry) in new[] { (r.EntryFillIds, true), (r.ExitFillIds, false) })
        {
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                throw new LedgerConflictException("Outcome repeats a fill id");
            foreach (var id in ids)
            {
                if (!fills.TryGetValue(id, out var f) || f.Symbol != r.Symbol)
                    throw new LedgerConflictException($"Outcome references unknown/mismatched fill {id}");
                if (isEntry ? f.Role != FillRole.Entry : f.Role is FillRole.Entry)
                    throw new LedgerConflictException($"Outcome fill role mismatch: {id}");
                if (_source is LedgerSource.Demo or LedgerSource.Live && f.Evidence != FillEvidence.ExchangeTrade)
                    throw new LedgerConflictException("Exchange outcomes require exchange-trade fill evidence");
                if (f.FeeValuation == FeeValuation.Unvalued) allFeesValued = false;
                else fees += f.FeeQuote!.Value;
                if (isEntry) entryQty += f.Quantity; else exitQty += f.Quantity;
                linked.Add(f);
            }
        }
        CloseTo(entryQty, r.EntryQuantity, "entry_quantity");
        CloseTo(exitQty, r.ExitQuantity, "exit_quantity");
        if (allFeesValued)
        {
            if (!r.RealizedFeesQuote.HasValue) throw new LedgerConflictException("Valued linked fills require outcome fee total");
            CloseTo(r.RealizedFeesQuote.Value, fees, "fees");
        }
        else if (r.RealizedFeesQuote.HasValue)
            throw new LedgerConflictException("Cannot report complete fees while a linked fee is unvalued");
    }

    private static void CloseTo(decimal actual, decimal expected, string field)
    {
        // Exchange decimal quantities/prices are exact; allow only representation-scale noise.
        if (Math.Abs(actual - expected) > 0.00000001m)
            throw new LedgerConflictException($"Outcome {field} does not match linked fills/arithmetic");
    }

    private static void RequireId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new LedgerConflictException("E1 identity is required");
    }

    private static string SafeSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("Unsafe ledger partition segment", name);
        return value;
    }

    private static string PayloadHash<T>(T record) where T : LedgerRecord
    {
        var node = JsonSerializer.SerializeToNode(record, JsonOptions)!.AsObject();
        node.Remove("recorded_at_utc"); // redelivery time is not a new fact
        static JsonNode? Canonical(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                var result = new JsonObject();
                foreach (var p in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                    result[p.Key] = Canonical(p.Value);
                return result;
            }
            if (n is JsonArray a)
            {
                var result = new JsonArray();
                foreach (var item in a) result.Add(Canonical(item));
                return result;
            }
            return n?.DeepClone();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(node)!.ToJsonString())));
    }

    private sealed class Envelope<T> where T : LedgerRecord
    {
        public int SchemaVersion { get; init; } = 1;
        public required string Id { get; init; }
        public required string PayloadHash { get; init; }
        public required T Record { get; init; }
    }

    private sealed class LedgerFile<T> where T : LedgerRecord
    {
        private readonly string _path;
        private readonly Func<T, string> _identity;
        private readonly Action<T> _validate;
        private readonly Func<T, T, bool> _mayReplace;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Dictionary<string, T>? _cache;

        public LedgerFile(string path, Func<T, string> identity, Action<T> validate, Func<T, T, bool> mayReplace)
        {
            _path = path; _identity = identity; _validate = validate; _mayReplace = mayReplace;
        }

        public async Task<bool> AppendAsync(T record, CancellationToken ct, Func<Task>? validateLinks = null)
        {
            _validate(record);
            var id = _identity(record);
            var hash = PayloadHash(record);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var map = await LoadCoreAsync(ct).ConfigureAwait(false);
                if (map.TryGetValue(id, out var old))
                {
                    if (PayloadHash(old) == hash) return false;
                    if (!_mayReplace(old, record))
                        throw new LedgerConflictException($"Conflicting immutable E1 identity: {id}");
                }
                if (validateLinks != null) await validateLinks().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var envelope = new Envelope<T> { Id = id, PayloadHash = hash, Record = record };
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions) + "\n");
                await using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                                 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                map[id] = record;
                return true;
            }
            finally { _gate.Release(); }
        }

        public async Task<IReadOnlyList<T>> LoadAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { return (await LoadCoreAsync(ct).ConfigureAwait(false)).Values.ToArray(); }
            finally { _gate.Release(); }
        }

        private async Task<Dictionary<string, T>> LoadCoreAsync(CancellationToken ct)
        {
            if (_cache != null) return _cache;
            var map = new Dictionary<string, T>(StringComparer.Ordinal);
            if (File.Exists(_path))
            {
                // Do not silently discard a torn final line: an unknown execution fact must freeze consumers.
                using var reader = new StreamReader(_path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lineNumber = 0;
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    lineNumber++;
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var envelope = JsonSerializer.Deserialize<Envelope<T>>(line, JsonOptions)
                            ?? throw new InvalidDataException("null envelope");
                        if (envelope.SchemaVersion != 1 || envelope.Id != _identity(envelope.Record)
                            || envelope.PayloadHash != PayloadHash(envelope.Record))
                            throw new InvalidDataException("identity/hash/schema mismatch");
                        _validate(envelope.Record);
                        if (map.TryGetValue(envelope.Id, out var old))
                        {
                            if (PayloadHash(old) == envelope.PayloadHash) continue;
                            if (!_mayReplace(old, envelope.Record))
                                throw new InvalidDataException("conflicting immutable identity");
                        }
                        map[envelope.Id] = envelope.Record;
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or LedgerConflictException)
                    {
                        throw new InvalidDataException($"E1 ledger is corrupt at {_path}:{lineNumber}; refusing partial statistics", ex);
                    }
                }
            }
            _cache = map;
            return map;
        }
    }
}
