# Implementation Log

## 2026-10-07 - Stage A Agent Loop Closure

### Scope

Closed the remaining Stage A gaps between intent routing, read-only tools, Plan
validation, and the API/UI result surface.

### Decisions

- Read-only graph projection now supports `depth` 0-32 consistently in the Node
  contract library and the .NET workspace service.
- `get_graph.direction` is implemented for `out`, `in`, and `both`; the public
  read-only chain endpoint accepts the same direction values.
- The .NET Agent tool definitions are loaded from the generated
  `config/read-only-tools.v0.json` contract instead of duplicating input schemas.
- `get_graph` results now enforce closed node/edge limits and expose
  `maxNodes`/`maxEdges` plus `truncated`.
- `explain_execution_chain` uses the published `skill` input name.
- Execution-chain results now expose bounded evidence-bearing steps and a summary
  instead of returning an unlabeled edge list.
- The Plan parser only extracts a JSON object that is actually Plan-shaped, so
  ordinary explanatory JSON cannot become an unexpected proposal.
- Non-configuration routes suppress model-produced Plans before persistence and
  remove Plan blocks from assistant display text.
- Negated safety language such as "不要直接写 Excel" does not make a valid
  configuration request unsupported.
- Bare mutation requests such as "修改" and "改一下" route to structured
  clarification instead of producing a missing-plan proposal.
- The Agent now extracts the focused asset identity from `get_graph` results
  case-insensitively and treats the evidence label/name as authoritative.
- Query responses get a deterministic name guard: title, name fields, and
  asset-key mentions are normalized back to the evidence value, and JSON
  `\uXXXX` escapes from smaller models are decoded before display.
- Parameter-semantics questions are routed as queries and the graph bootstrap
  now extracts action keys from `__executor`, then automatically loads the
  matching `get_capability_context` parameter contracts.
- If no parameter contract was returned, the Agent emits a deterministic
  clarification instead of letting the model guess `action_param` meaning.
- Explicit UI asset bindings now take precedence over message-based asset
  resolution, so numeric parameter lists cannot unbind the selected Skill.
- Capability context now includes only enums referenced by the selected action
  and requests enough enum values to resolve named values such as
  `NumericType.LightningDamage`.
- Configuration answers are required to start with an Excel field-level change
  table and to avoid undocumented assumptions when a concrete value is given.
- Added a deterministic damage-change summarizer for explicit requests such as
  "fixed 27500 lightning damage". It resolves the target Effect from the graph,
  reads `Damage.fixedDamage` scale and `NumericType` values from capability
  contracts, and bypasses long model reasoning with a concise Excel change table.
- Asset resolution now supports display-name mentions before asking for IDs,
  ranks `TbSkill` first for skill behavior requests, and ignores modification
  operands such as `固定27500` during asset ID scanning.
- Pending asset confirmations are persisted per conversation. A follow-up
  "1" or "是" binds the candidate, resumes the original request, and then runs
  the deterministic change summary without requiring the user to repeat context.
- Display-name resolution is suppressed for self-introduction and other
  non-asset chat, and generic tokens such as "自己" are excluded from mention
  matching.
- ScaledInteger parameter explanations now follow the declared contract scale
  exactly. With `scale=10000`, raw `2000` is documented as `0.2` (20% for
  percentage fields), not `2000%` or an unspecified precision assumption.
- Added a global numeric-precision policy: every value must identify its evidence
  field, raw Excel value, scale, logical value, unit, and formula. Missing
  scale, unit, default, range, or rounding rules require clarification instead
  of a numeric proposal.
- Tool protocol now recognizes DeepSeek DSML tool-call envelopes emitted by
  CC Switch models, converts them to bounded read-only tool calls, and strips
  the DSML markers from user-visible assistant text.
- Agent bootstrap context is budgeted more tightly: graph depth 8, 64 nodes,
  128 edges, and action-scoped capability queries omit unrelated entity and
  field registries. This keeps local Ollama requests under its 32k context.
- Assistant text is normalized against every referenced graph node identity, so
  any `Namespace:id` mention is paired with the evidence label instead of an
  invented name. Tests use synthetic entity IDs and names.

### Verified

```text
npm run validate
npm test
dotnet test RtsSkillStudio.sln --artifacts-path <temporary-path>
```

## 2026-10-06 - Runtime Contract Alignment and Full Chain Traversal

### Scope

Resolved the validation differences using the Unity generated enums and effect
executor code as the authority.

### Decisions

- `EffectActionType` is authoritative for executable effect actions.
- The capability registry no longer exposes `AddShield`, which is absent from the
  runtime enum and executor set.
- `AddMaxPropertyWithCurrent` and `SubMaxPropertyWithCurrent` are exposed with
  repeated property/value pairs.
- Buff-target parameters are optional where the runtime parser defaults to the
  current effect target.
- `Healing` exposes all six runtime-backed parameters.
- The graph projector resolves `SummonUnit` through `EUnitType` to the actual unit
  table instead of emitting `UnitByType` placeholders.
- Search `type + table_id[]` references now produce graph edges to concrete unit
  tables.
- Behavior projection supports depth 32 so valid deep chains are not truncated at
  the previous depth-12 boundary.
- The Agent context now requests the full bounded chain and keeps its existing
  node, edge, and field-value limits.
- `import-builtin-enums.mjs` now reads the selected Unity generated C# enum files
  in addition to `builtin.xml`; overlapping enum definitions must have identical
  members and numeric values.
- External source paths and the pinned source revision live in
  `config/external-sources.v0.json`, so `npm run verify:enums` and
  `npm run verify:source` work without arguments.
- `TeamRelation` and `ECompareType` are marked as parameter-convention enums;
  `TeamRelation` explicitly warns against confusion with `EFactionRelation`.
- Condition parameter defaults from runtime handlers are carried into the
  registry, including the differing `sourceUnit` defaults.
- Unsupported `SummonUnit`/Search unit types now remain visible as explicit graph
  nodes instead of being silently omitted.
- Added the remaining directly referenced generated enums to
  `unityGeneratedEnums`, including `EUnitType`, `NumericType`, search enums, flag
  enums, and `EGroupCompletedType`.
- Added effect-side `defaultValue` metadata for buff targets, `ClearHitMarks`, and
  `Knockback`.
- Promoted `Effect`, `Item`, `Buff`, `Bullet`, and `Trap` to direct behavior roots
  alongside `Skill`, `EffectGroup`, and `ConditionGroup`.
- Added the generic `ModifyAsset` Plan operation so Item, Buff, Bullet, Trap, and
  other assets do not require Skill-only mutation contracts.

### Verified

```text
npm run verify:enums -- --builtin /path/to/builtin.xml
npm run verify:source -- --schema /path/to/hero-authoring-schema.json
npm run validate
npm test
dotnet test tests/RtsSkillStudio.Tests/RtsSkillStudio.Tests.csproj
```

The corrected projector resolves:

- `100104 -> TbTrap:60001` as a real missing target.
- `100804 -> TbSoldier:40002 -> TbSkill:12340015` as a valid chain.
- `12340014 -> TbBuilding:30001/30005` as valid search-driven links.
- `100602 -> TbEffect:100600090` at a depth beyond the previous limit.

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
