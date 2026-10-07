# RTS Skill Studio

Independent natural-language skill configuration studio for the RTS project.

The product accepts a simple natural-language skill request and produces a complete,
validated Excel configuration change set:

```text
natural language
  -> SkillConfigPlan
  -> deterministic compiler
  -> WorkbookPatch
  -> WorkbookPatchValidationReport
  -> Excel-level diff
  -> user confirmation
  -> atomic write to an Excel or SVN working copy
```

The Studio owns its process, SQLite draft store, GUI, and Agent orchestration. It does
not reference, modify, or run the TianshuDM product. Selected TianshuDM code is vendored
under `vendor/TianshuDM` with provenance recorded in [vendor/VENDOR.md](vendor/VENDOR.md).

## Product Goal

Reduce the manual cost of skill Excel configuration. A designer describes a skill in
natural language and the Studio produces a complete, validated Excel change set.

Success is measured by designer workload and configuration correctness — not by
contract completeness, UI polish, or model capability. Measured against the manual
editing baseline (TianshuDM authoring flow; hand-editing Excel as secondary
comparison):

| Metric | Target |
| --- | --- |
| Single-skill configuration time | at least 50% lower |
| Proposal acceptance rate | at least 70% |
| Task completion after AI assist | at least 90% |
| Average clarification rounds | at most 1 |
| Golden-case semantic equivalence | at least 95% |
| Illegal plans applied | must be 0 |

### The agent is the delivery surface

The product is a skill configuration agent: it runs against the existing skill
framework and produces skill Excel configuration from natural language. Local and
cloud models are interchangeable; no particular model, protocol, or agent host is a
product dependency. The long-term asset is still the `SkillConfigPlan` schema and the
capability registry — the agent is how that asset reaches the designer.

### Domain fluency is a hard requirement

The agent must know the skill module and its execution chains. This is a gate, not an
emergent property of a stronger model:

- Every statement about a module, table, field, or chain must trace to real graph or
  schema evidence.
- With no supporting evidence the agent must return "unable to determine" plus a next
  check path — never a fabricated conclusion.
- Skill-chain explanations are scored against golden explanation points: recall at
  least 95%, reference accuracy 100%, zero confident conclusions without evidence.

### Permanent control boundary

The model never writes Excel cells, SQL, generated Luban data, or mutation commands.
The only model-to-product channel is a typed `SkillConfigPlan`. The permanent execution
path is:

```text
SkillConfigPlan
  -> Studio compiler
  -> WorkbookPatch
  -> deterministic validation
  -> Excel diff
  -> user confirmation
  -> Studio-controlled atomic apply
```

The compiler does not confirm a plan; the user does. The current phase adds a
temporary limit because Plan compilation, validation, diff, confirmation, and
controlled apply are not implemented yet. Direct writes are permanently forbidden;
controlled writes are temporarily unavailable.

## Current Status

Progress is tracked against the product baseline in the Obsidian document
`技能自然语言配置方案.md`. Last verified: 2026-10-07.

The next executable phase is defined in
[docs/phase-b-modify-skill-vertical-slice.md](docs/phase-b-modify-skill-vertical-slice.md).

### Completed

- Independent .NET 8 Studio solution with vendored TianshuDM Contract, Domain,
  Application, Excel, and SQLite projects.
- P0/P1 contract factory with capability registry, typed plan/patch schemas,
  30 golden cases, read-only tools, semantic assertions, and deterministic
  evaluation.
- Immutable `WorkbookPatch` and `WorkbookPatchValidationReport` contracts with
  workspace/source identity, semantic field metadata, and required validation
  checks.
- Unified model provider layer:
  - OpenAI Responses API.
  - OpenAI-compatible Chat Completions.
  - Local Ollama provider.
  - Optional local CC Switch development route.
- Local deployment verified with `qwen3.5:4b` on Ollama.
- Cloud model routing verified through CC Switch's active Codex provider.
- Backend-owned Agent policy shared by every provider, including the final product
  goal, current phase, domain terms, evidence rules, and permanent write boundary.
- Intent routing for query, configuration, creation, clarification, and unsupported
  requests.
- A bounded, provider-independent read-only tool loop with five tools:
  capability context, asset resolution, graph projection, similar-skill search, and
  execution-chain explanation.
- Full generated `SkillConfigPlan` JSON Schema validation, plus structured
  clarifications and unsupported results.
- Bounded real-workspace graph context for Skill, Item, Effect, Buff, Bullet, Trap,
  EffectGroup, and ConditionGroup behavior roots.
- Persistent SQLite conversation sessions with create, list, switch, selected-skill
  binding, and multi-turn history.
- Model enumeration and selection for Ollama and CC Switch, plus configurable
  reasoning effort.
- Structured `SkillConfigPlan` extraction for configuration requests, deterministic
  structural validation, and a real Plan inspector tab.
- First usable Studio UI:
  - Provider selection and status.
  - Model and reasoning selection.
  - Conversation creation and switching.
  - Natural-language conversation.
  - Real skill list and search.
  - Execution-chain, Plan, and evidence inspector.
  - Desktop and mobile layouts.
- Provider routing verified end to end:
  - Ollama local route returns `200`.
  - CC Switch forwards to its active Codex upstream and honors the requested model.
  - CC Switch exposes its model catalog through `/v1/models`.
- Real Excel workspace reading:
  - Shared-file snapshot loading while Excel has workbooks open.
  - Current workspace: 29 tables, 1,958 graph nodes, 1,749 graph edges,
    and 54 skills.
  - Skill graph and downstream chain queries.
- Safe Excel write smoke test:
  - Copies the workspace into `.studio-work/`.
  - Rewrites `Skill.xlsx` in the copy.
  - Re-reads and compares configured fields.
  - Verified 54/54 records with matching fields.
  - Never writes the selected source workbook.
- Phase B deterministic editing vertical slice:
  - Validates `SkillConfigPlan`, compiles it into immutable `WorkbookPatch`.
  - Validates patch identity, base revision, source hash, target, field type,
    enum, range, reference, and scale/unit provenance.
  - Projects an Excel field-level diff into the Studio inspector.
  - Applies validated patches only to a temporary `.studio-work` copy and
    re-reads the result.
  - Verified `cooldown = 8s -> cd_time = 8000` against the real workspace with
    the source-root hash unchanged.

### In Progress

- Prepare the next phase: controlled source working-copy confirmation, atomic
  apply, backup, rollback, and undo.

### Next

Extend modification support beyond the scalar-field vertical slice, then add
Plan recompilation and controlled source working-copy apply.

### Current Limits

- Conversation rename and delete are not implemented.
- Phase B supports one `ModifySkill` operation and scalar fields; Effect,
  Condition, Buff, Bullet, and Search restructuring are not implemented.
- Source workbook writes, source confirmation, backup, rollback, and undo are
  not implemented yet.
- Direct OpenAI access is configured but has not been live-tested because no
  API key/model was provided. CC Switch cloud routing has been tested.

## Layout

```text
contract-factory/           Node contract factory, registry tools, and evals
contracts/rts-skill-agent/  Generated schemas, registry, eval data, examples
src/RtsSkillStudio.Agent/   Agent, plan, compiler, and validation code
src/RtsSkillStudio.Api/     Local Studio HTTP host and web UI
vendor/TianshuDM/           Copied source snapshot from TianshuDM
web/                        Legacy frontend placeholder; active UI is under
                            src/RtsSkillStudio.Api/wwwroot
```

## Commands

```powershell
dotnet build RtsSkillStudio.sln
dotnet run --project src/RtsSkillStudio.Api --urls http://127.0.0.1:5257
node scripts/sync-contracts.mjs
cd contract-factory; npm run validate; npm test; npm run eval:modify
```

## Local LLM

Ollama is configured as the default provider at
`http://127.0.0.1:11434/v1` with model `qwen3.5:4b`.

The cloud provider uses the OpenAI Responses API. Set `OPENAI_API_KEY` and fill
in the `openai` model in `src/RtsSkillStudio.Api/appsettings.json`, or override
configuration with environment variables.

CC Switch can be used as an optional development provider while its local proxy
is running on `127.0.0.1:15721`. The `ccswitch` provider routes through the
currently selected Codex provider and is not a product dependency. Its `/v1/models`
endpoint exposes the models supported by that Codex route, and the Studio UI loads
that catalog into its model selector.

The provider configuration still supplies the default model. The UI can override it
per request through the enumerated model list, and reasoning effort can be selected
independently.

```powershell
$env:OPENAI_API_KEY = "<key>"
$env:RtsSkillStudio__Llm__Providers__openai__Model = "<model>"
dotnet run --project src/RtsSkillStudio.Api
```

Useful endpoints:

```text
GET  /api/v1/llm/providers
POST /api/v1/llm/chat
GET  /api/v1/workspace/status
GET  /api/v1/skills
GET  /api/v1/skills/{skillId}/chain
POST /api/v1/workbook-patches/compile
POST /api/v1/workbook-patches/apply-temporary
POST /api/v1/workspace/write-smoke-test
```

The Studio UI is served at `http://127.0.0.1:5257/`. The write smoke test copies
the Excel data root into `.studio-work/`, rewrites `Skill.xlsx` in that copy,
then re-reads and compares all configured fields. It never writes the source
workbook.
