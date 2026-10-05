# Implementation Log

## 2026-10-05 - P1 Second Review Remediation

### Scope

Removed the remaining P1 safety, context-size, contract, enum, metric, and schema
drift findings before P2.

### Decisions

- Capability status depends only on semantic capability matches, not the namespace list.
- Every truncated capability category reports total, returned, and truncated state.
- Entity fields now participate in enum discovery and legal candidate exposure.
- Read-only contracts and the graph JSON Schema are embedded in the capability registry.
- Library API calls enforce input schemas; graph construction validates against the
  embedded JSON Schema.
- Enum snapshots support non-writing verification and reject XML/overlay conflicts.
- Skill-kind checks use the registry entity key rather than hardcoded strings.
- Graph node and edge limits are independently configurable.
- Metrics are renamed to describe deterministic behavior and report the real pass count.

### Verified

```text
npm run verify:enums -- --builtin /path/to/builtin.xml
verified config/enum-values.v0.json

npm run verify:source -- --schema /path/to/hero-authoring-schema.json
verified config/capability-registry.v0.json

npm run validate
validated 5 read-only tools and 26 cases

npm test
19 tests passed

npm run eval:readonly
26 cases passed
deterministicCasePassRate: 1
deterministicExplanationPointRecall: 1
stepEvidencePresenceRate: 1
capabilityNoMatchHonestyRate: 1
falseConclusionWithoutEvidence: 0
```

### Open Items

- Model-side explanation evaluation remains `NotRun` until a provider is connected.
- Bind the read-only tool contract into the TianshuDM product sidebar.
- Replace the sample graph with a generated production snapshot.

### Git State

No commit or push has been performed.

## 2026-10-05 - P1 Review Remediation

### Scope

Fixed the P1 findings from external review before any P2 work starts.

### Decisions

- `get_graph.limit` now caps both returned nodes and returned edges.
- Graph validation mirrors the snapshot schema instead of relying on optional fields.
- Name candidates never hide their total count.
- Registry checks preserve an existing source revision when the source hash is unchanged.
- Execution-chain traversal deduplicates shared subtrees and reports reuse.
- All read-only results carry snapshot identity.
- Runtime-only enum options are mirrored from TianshuDM's enum reader; XML enums are
  generated from `builtin.xml`.
- Read-only tool inputs and outputs are validated with a zero-dependency JSON Schema subset.
- P1 evaluation follows `caseSet.graph` and reports deterministic metrics. Model
  evaluation remains explicitly `NotRun`.

### Verified

```text
npm run verify:source -- --schema /path/to/hero-authoring-schema.json
verified config/capability-registry.v0.json against hero-authoring-schema.json

npm run validate
validated 39 effects, 11 conditions, 16 intents, and 30 golden cases
validated 5 read-only tools and 19 cases

npm test
14 tests passed

npm run eval:readonly
19 cases passed
casePassRate: 1
explanationCasePassRate: 1
evidenceCoverage: 1
falseConclusionWithoutEvidence: 0
```

### Open Items

- Bind the read-only tool contract into the TianshuDM product sidebar.
- Replace the sample graph with a generated read-only snapshot from the current workspace.
- Add accepted plans for all editing golden cases before model evaluation.
- Connect a model provider before marking model-side evaluation complete.

### Git State

No commit or push has been performed.

## 2026-10-05 - P0 Source Closure and P1 Read-Only Core

### Scope

Closed the P0 source-sync gap and implemented the first P1 read-only core in this
repository without modifying TianshuDM or ET_RTS_Pro.

### Decisions

- Registry verification is a non-writing `--check` mode.
- The checked-in registry is pinned to TianshuDM schema revision `fa37737`.
- P1 is host-independent and consumes an explicitly read-only graph snapshot.
- Name resolution returns candidates even when only one match exists.
- Execution explanations contain edge-level evidence and never synthesize missing
  steps.
- A future product sidebar can bind these functions through its own host adapter.

### Artifacts

- `contracts/skill-graph.schema.json`
- `config/read-only-tools.v0.json`
- `examples/sample-skill-graph.json`
- `src/skill-graph-index.mjs`
- `src/read-only-eval.mjs`
- `evals/read-only-cases.v0.json`
- `tools/read-only-cli.mjs`
- `tools/evaluate-read-only.mjs`
- `tools/validate-p1.mjs`

### Verified

```text
npm run verify:source -- --schema /path/to/hero-authoring-schema.json
verified config/capability-registry.v0.json against hero-authoring-schema.json

npm run validate
validated 39 effects, 11 conditions, 16 intents, and 30 golden cases
validated 5 read-only tools and 12 cases

npm test
Node.js tests passed

npm run eval:readonly
evaluated 12 read-only cases against examples/sample-skill-graph.json
```

### Open Items

- Bind the read-only tool contract into the TianshuDM product sidebar.
- Replace the sample graph with a generated read-only snapshot from the current
  workspace.
- Add accepted plans for all editing golden cases before model evaluation.
- Review default-mechanism choices against runtime semantics.

### Git State

No commit or push has been performed.

## 2026-10-05 - P0 Contract Prototype

### Scope

Started a clean, language-neutral P0 repository for a controlled natural-language
skill configuration agent.

### Decisions

- The structural capability layer is generated from an external semantic schema.
- The semantic interpretation layer is versioned and reviewed inside this repository.
- Models propose `SkillConfigPlan`; they never edit `AuthoringPatch` or authoring fields.
- Equivalent mechanisms require an explicit default-mechanism rule.
- Unsupported requests must return a structured rejection, not silently degrade.
- Semantic evaluation uses normalized IR rather than exact JSON matching.

### Artifacts

- `config/`
- `contracts/`
- `evals/`
- `examples/`
- `src/`
- `tools/`
- `tests/`

### Verified

```text
npm run validate
validated 39 effects, 11 conditions, 16 intents, and 30 golden cases

npm test
4 tests passed
```

### Open Items

- Review the proposed default mechanism choices with runtime semantics.
- Replace the registry source snapshot when the upstream semantic schema changes.
- Add actual accepted-plan fixtures for all 30 golden cases before model evaluation.
- Define the read-only tool contracts before P1 implementation.
- Decide the eventual service language and runtime host.

### Git State

The repository is initialized on `main` with the target remote configured. No commit
or push has been performed.
