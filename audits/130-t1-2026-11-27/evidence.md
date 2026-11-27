# 130/T1 — semantic carve, decision + serial full-suite carrier

Feature: 130-sqzrlite · task T1 (order 1) · artifact `e524353a-8a4d-4bae-a9cb-18c0a76de29d`
Date: 2026-11-27 · Worktree `feat/130-sqzrlite` · commits `aae5c24` (carve), `d9b633e` (wire pins), `88c136e` (stub fix)

## What the task proved

Squeezr.Core is semantic-free: `rg "Qdrant|Ollama|IVectorStore|Embedding" src/Squeezr.Core`
(excluding bin/obj) returns **0 hits** at head `88c136e`, and `CoreSemanticFreedomTests`
keeps that a permanent ratchet — the same scan plus the project-graph direction
(Core never references Squeezr.Semantic; Semantic and Cli do).

## Composition mechanisms (spec §scope 1)

1. **Config** — `SqueezrConfig` carries the semantic sections as opaque
   `[JsonExtensionData]` JSON: Core round-trips them without naming them. Binding, defaults
   and validation (ONNX refusal with `OnnxProviderCode`, model-profile resolution) live in
   `Squeezr.Semantic.Configuration.SemanticConfigFile` / `SemanticConfig`; `Attach` writes the
   typed sections back over the raw form so TUI/services-up edits survive a save. A missing
   qdrant section binds the same default endpoint `squeezr init` used to write.
2. **Index/watch seams** — Core owns `IIndexVectorPhase` (pass lifecycle:
   DropCollection/Preflight/ReplayJournal/RetirePoints/DrainRepo/DrainFile plus executor
   evidence strings), `ISemanticArmSource` (search arm), `IWorktreeVectorProbe` (claim
   measure/attest by chunk hashes). Implemented in Semantic: `WatchEmbeddingRuntime`
   (phase + probe), `SemanticSearchArm` (arm). `VectorReapJournal.Defer` is the one internal
   Core door (reap journal is meta.db content), shared via InternalsVisibleTo.
3. **Storage stays whole** — every MetaStore method name survives the AC grep
   (embedded/pending/modelKey), so no internals door was needed. The BSON contract
   `embeddingStatus` is pinned via `[BsonField]` under the renamed `VectorStatus` property;
   LiteDB resolves expressions through the mapper (PendingQueueRefillTests and the reap
   suites exercise these queries end to end).

## Wire contracts pinned

- Heartbeat activity keeps the literal phase name `embeddings` (`IndexPhase.Embeddings`
  renamed to `Vectors`; `DaemonHeartbeatWriter` maps it — fleet listings and stored beats
  read exactly as before; FleetActivityTests unchanged).
- Index events and `--json` output keep `embedded` / `embeddingsCompleted` /
  `embeddingsSkipped` at both the completed-line and the repos[] level — the Core fields
  renamed to `DrainCompleted`/`DrainSkippedReason` and the JSON keys are pinned with
  `JsonPropertyName` (IndexEventWriterTests pin the shapes).
- ONNX and model-profile error texts byte-identical, now thrown by the binder
  (ConfigNullSectionCliTests, OnnxStoreDiagnosticsTests pass unchanged).

## Moved (git mv, namespaces Squeezr.Core.* → Squeezr.Semantic.*)

- `Core/Embeddings/` 16 files (VectorDimensionMismatchException stays Core under
  `Squeezr.Core.Indexing` — the drain-disable policy's contract type).
- Configuration: EmbeddingsConfig, OllamaOptions, OpenAiCompatibleOptions, QdrantConfig,
  QdrantOwnedDecision.
- Health: EmbeddingsHealth, EmbeddingVintages, WorkspaceSemanticFacts + the doctor coverage
  leg (`DoctorSemanticFindings`; the runner takes a findings delegate).
- Workspace: QdrantStatus, OllamaStatus; WorkspaceStatus loses the Qdrant field (the Cli
  carries the probe result into WorkspaceStatusReport directly).
- Infra: ServicesComposeFile, OwnedComposeFile, ManagedServiceProbe, the services fleet
  cluster (ExternalServiceState/Solver, ServiceLiveness(+Probe), ServiceOwnership,
  ServiceProbeState, IServiceOwnershipSource), RocmDeviceProbe/IRocmDeviceProbe. Core keeps
  `GeneratedComposeFile` (internal, two lowercase markers) so drop/migration can still
  delete a generated compose file.
- Indexing: EmbeddingIndexPhase, VectorPointReconciler, SemanticHitsResolver,
  WatchEmbeddingRuntime.
- StoreCopyModelGuard stays in Core deliberately: it is types-only model-key arithmetic over
  MetaStore (no banned symbols), and it guards the copy path Core owns.

## Ratchet interplay (032's two-sided floor)

The sync-over-async sweep left Core with the drain: measured `5`
(`grep -c GetAwaiter().GetResult()` over src/Squeezr.Core — RoslynProvider 3,
RoslynSession 2). CoreBaseline lowered **21 → 5**, denominator floor **10 → 3**, file
floor **4 → 2**, same two-sided contract; the sweep test passes at the new constant. The
denominator floor had to follow the baseline down: with 5 honest sites a floor of 10 would
have red-lined the very lowering the carve sanctions.

## Behavior deltas (deliberate, tested)

- `squeezr init` writes a config without the semantic sections; every consumer binds the
  same defaults (binder + `WorkspaceManagerInitTests`), and the sections materialize in the
  file on the first save that touches them.
- Unknown top-level config sections now survive a save (extension data) where they were
  previously dropped — codified in `SemanticConfigFileTests.Save_PreservesSectionsCoreDoesNotModel`.
- With an unreadable-for-semantic config, the watcher's core-side LoadConfig no longer falls
  back to defaults (the ONNX throw moved to the binder), so filter rulesets read the real
  config while the drain still switches off (the Cli factory cannot build a provider).

## Carrier — the formal gate

See `run-full-suite.log` in this directory (command, head, SQUEEZR_HOME, wall time,
box load probes, Failed/Passed/Total).

## New tests

- `Structure/CoreSemanticFreedomTests` — the grep and project-graph direction as ratchets.
- `Configuration/SemanticConfigFileTests` — binder defaults, null-section tolerance, ONNX
  and profile refusals, attach-on-save round-trips, unknown-section preservation.
- `Indexing/VectorPhaseSeamTests` — a stub phase pins where the pass calls the seam
  (preflight/replay ordering, per-repo drains, budget carry, reembed drop ordering,
  exact-only shape with a reason).
- `TestVectorPhase` — the seam hand test call sites spell out (provider+store pair → one
  phase; null on either side stays exact-only).
