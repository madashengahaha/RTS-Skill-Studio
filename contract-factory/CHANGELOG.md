# Changelog

## 0.2.2 - 2026-10-05

### Fixed

- Capability context now returns `NotFound` when a free-text query has no matches.
- Capability results report total and returned counts plus explicit truncation.
- Capability context returns compact namespace data and bounded enum values.
- Entity-field enum bindings now cover skill type, search fields, and damage pipeline
  stages, with values sourced from generated Unity config enums.
- Library calls validate their embedded `inputSchema` before execution.
- Runtime graph validation consumes the embedded snapshot JSON Schema directly.
- Enum import supports `--check`, paired or self-closing value tags, and overlay/XML
  conflict detection.
- Metrics now have deterministic names, measure explanation points, and report the real
  passed count.
- Skill lookup uses the registry entity key instead of hardcoded strings.
- Graph node and edge limits can be configured independently.

### Verification

- `npm run verify:enums -- --builtin /path/to/builtin.xml`
- `npm run verify:source -- --schema /path/to/hero-authoring-schema.json`
- `npm run validate`
- `npm test`
- `npm run eval:readonly`

## 0.2.1 - 2026-10-05

### Fixed

- Graph projection now enforces node and edge limits and never returns dangling edges.
- Graph snapshots are strictly validated for semantic data, edit locks, evidence, namespace,
  and entity-kind consistency.
- Asset name resolution reports total matches, returned count, and truncation.
- `verify:source --check` preserves existing source revisions when the source hash matches.
- Shared execution subtrees are expanded once and marked as reused.
- Every read-only result carries graph snapshot identity.
- Capability context includes the legal namespace list and referenced enum values.
- Enum values are generated from `builtin.xml` plus explicit runtime convention overlays.
- Read-only inputs and outputs are machine-validated against versioned JSON Schemas.
- P1 evaluation now follows the case set's graph, checks source immutability, covers invalid
  inputs, truncation, cycles, evidence, and emits deterministic metrics.

### Verification

- `npm run verify:source -- --schema /path/to/hero-authoring-schema.json`
- `npm run validate`
- `npm test`
- `npm run eval:readonly`

## 0.2.0 - 2026-10-05

### Added

- P1 read-only tool contract for capability context, asset resolution, graph queries,
  similar-skill search, and execution-chain explanation.
- Versioned `ReadOnlySkillGraph` schema and sample snapshot.
- Zero-dependency read-only index implementation and CLI host.
- Read-only evaluation case set and P1 validation command.
- Source-registry check mode that verifies the checked-in snapshot without writing.

### Verification

- `npm run verify:source -- --schema /path/to/hero-authoring-schema.json`
- `npm run validate`
- `npm test`
- `npm run eval:readonly`

## 0.1.0 - 2026-10-05

### Added

- P0 contract scope and architecture documentation.
- Capability registry schema, semantic overlay, foundation, and deterministic
  registry generator.
- Initial generated registry snapshot for 39 effect actions, 11 conditions,
  16 semantic intents, and 7 damage stages.
- `SkillConfigPlan` and `AuthoringPatch` JSON Schemas.
- Versioned default-value and default-mechanism contracts.
- Thirty golden cases and semantic equivalence rules.
- Semantic plan normalization and assertion evaluation.
- Zero-dependency P0 validation and Node.js tests.

### Verification

- `npm run validate`
- `npm test`

### Not Included

- Model provider integration.
- Product UI.
- Draft, Excel, or generated data mutation.
- Remote commit or push.
