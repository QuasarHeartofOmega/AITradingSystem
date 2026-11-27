# E1 ledger — storage contract + runtime wiring

**Status:** contracts, storage, runtime producers and unexecuted regression tests added. Decision/entry/protection/exit/outcome paths are wired through `E1Runtime`; restart/polling recovery is code-only. No builds, compilations or tests were run. Demo acceptance is still open; this does not establish any trading results.

## Partition and files

`JsonE1Ledger` writes below `data/ledger/{environment}-{source}/{accountFingerprint}/`:

- `decisions.jsonl` — one final gate decision per `DecisionId`;
- `fills.jsonl` — idempotent fill deltas per `FillId`;
- `outcomes.jsonl` — one current outcome per `LifecycleId`.

Environment, source and hashed account fingerprint are constructor constraints and are checked on every record. Paper/replay fills cannot enter a Demo/Live partition. Shadow has no fills. Use a stable `Position.Id` across restarts; use the entry intent id for a `NoFill` lifecycle. `RunId` identifies an attempt/process, not a new trade.

The caller supplies `ConfigHash`, `BuildHash` (or explicit `unknown`), point-in-time features, gate versions and a **hashed** account fingerprint. Never put API keys, raw account ids or full balances in this ledger.

## Durability and conflicts

Each JSONL envelope contains an identity, SHA-256 payload hash and record. Appends use `FileStream.Flush(flushToDisk: true)`. A replay with the same identity and payload returns `false`; a conflicting payload throws `LedgerConflictException`. A torn/corrupt line or corrupt partition fails closed with an exception, rather than silently starting an empty ledger.

This is a **single-writer** store (the application process). The async gate serializes its calls, not multiple processes sharing a directory. Stop the application before inspecting/repairing files; keep backups. Do not delete an unresolved outcome to make a conflicting final result fit.

## Fill and fee semantics

- `ExchangeTrade`: actual exchange trade id and trade fee; required for statistically usable Demo/Live outcomes.
- `OrderCumulativeDelta`: new quantity derived from an order cumulative snapshot; not a fabricated `myTrades` id. Its fee is a delta, not the cumulative fee copied again.
- `PaperSimulation` / `ReplaySimulation`: explicitly simulated, never mixed with exchange evidence.
- `Quantity` is the new delta; `CumulativeFilledQuantity` is the order snapshot.
- Fee assets are preserved. Base/BNB fees require an evidenced conversion (`ConvertedQuote`, rate and timestamp). Without a valuation use `Unvalued`, `FeeQuote = null`: the outcome is not statistically usable. Zero fee uses `NotApplicable` and quote zero. Never substitute a balance delta or assume a base/BNB fee is quote currency.

## Outcome semantics

`NoFill`, `Hold`, rejected/cancelled/unfilled entry intents are not trades. A position outcome links decision → entry intent → entry fills → protection identity/resolution → exit fills, including dust quantity. An `Unresolved` outcome may be replaced by a final outcome once; final outcomes are immutable. `LoadOutcomesAsync` returns the latest outcome per lifecycle, not every revision.

For `Closed`, gross is calculated from actual average entry/exit prices and **exit quantity**; net is gross minus valued round-trip fees; net R is net divided by the original quote risk. Slippage is diagnostic against an arrival benchmark and is not deducted again from actual-fill gross. Dust is not counted as realized exit quantity. Missing fees, risk, fills, protection resolution or an unresolved reason excludes the outcome from `IsStatisticallyUsable`.

The storage layer checks linked fill ids, roles, evidence, fee totals and arithmetic. `E1Runtime` obtains exchange trades/fees from `myTrades` or executionReport trade ids, persists entry identity before submission, records protection identity/resolution, and reconciles restart/polling without creating a second lifecycle outcome. Base-asset fees are valued at the same fill price with conversion evidence; BNB/other non-quote fees remain `Unvalued` until separate conversion evidence exists. Demo acceptance remains open.

## Applied exchange facts vs. deferred evidence (Phase 1.5)

An exchange fill is a fact; the ledger is evidence about that fact. `E1Runtime` therefore separates the two:

- **Write-ahead entry path stays fail-closed.** `PrepareEntryAsync`, `RecordDecisionAsync` and `RecordEntryResultAsync` still throw on ledger failure, so an entry cannot be submitted without durable identity.
- **Applied-fact path is fail-safe.** `RecordOrderAsync`, `RecordReportAsync`, `RecordProtectionAsync`, `AttachPositionAsync` and `RefreshOutcomeAsync` never propagate a ledger exception into the trading loop. A failure is logged as `E1-EVIDENCE-DEFERRED`, queued (bounded, 512 items) and replayed by `ReconcileAsync` (`E1-EVIDENCE-REPLAYED`). Replay is idempotent: fills dedup by `FillId`, outcomes by lifecycle.
- **Deferred evidence freezes entries, not exits.** While `EvidencePendingCount > 0` and `Trading:EvidenceFailureFreezesEntries=true`, reconciliation reports `E1-EVIDENCE-PENDING` and marks execution state untrusted. Position protection and closing continue: a journal outage must not leave a position open or trigger a second sell (Blocker 5, 2026-11-27: 17 sells of one DASH lifecycle).
- **Identity is repaired, not fatal.** An adopted orphan without E1 identity gets it from the execution journal (`RestoreIdentityAsync`) or is explicitly marked `external:` (`E1-EXTERNAL-IDENTITY`). An outcome record without identity is skipped with `E1-OUTCOME-SKIPPED` instead of throwing `E1 identity is required` into reconcile.
- **Lifetime quantity accounting.** `Position.LifetimeBoughtQuantity` / `LifetimeSoldQuantity` cap the total sellable volume of one lifecycle across SL, reconcile, manual and kill-switch contours. A request above the remaining allowance raises `CLOSE-OVERSELL-ANOMALY` and is clamped, never sent.

Demo regression acceptance for these paths is still open (`[accept]` in `docs/history/MASTER-PLAN.md`, Phase 1.5).
