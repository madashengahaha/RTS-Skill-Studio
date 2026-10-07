# Repository Rules

## Architecture

- This repository is the independent RTS Skill Studio product.
- The .NET product lives at the repository root.
- `contract-factory/` contains the Node contract factory, registry generation,
  golden cases, and evaluation tools.
- `contracts/rts-skill-agent/` contains generated contract snapshots consumed by the
  Studio.
- `vendor/TianshuDM/` contains copied source. It must never reference the original
  TianshuDM solution or path.
- TianshuDM is a code source and optional interoperability target, not a runtime
  dependency.

## Product Boundary

- The user describes a skill in natural language.
- The model emits a typed `SkillConfigPlan`.
- Deterministic Studio code compiles it into a `WorkbookPatch`.
- Validation must pass before any Excel write.
- The user reviews Excel-level diffs before confirmation.
- Excel or an independent SVN working copy is the write target.
- Node graph views are inspection and explanation surfaces, not the primary authoring
  workflow.

## Generality and Long-Term Agent Evolution

- Agent capabilities must be modeled as reusable contracts, semantic metadata, and
  deterministic algorithms. Do not solve a general capability gap with a one-off
  UI, service, or prompt special case.
- Production code must not branch on concrete asset IDs, labels, source rows,
  workbook values, example skills, or one incident's namespace/action names.
- Stable domain constants and versioned contract field names are allowed only when
  they represent an explicit boundary. Even then, prefer capability-registry or
  schema-driven rules over direct code branches.
- If an issue cannot be described by a generic predicate, extend the versioned
  contract schema or semantic configuration first, then update Studio consumers.
  Concrete golden cases may prove behavior; production code must not import their
  conclusions.
- Keep UI code decoupled from domain tables and action implementations. UI should
  consume semantic projections such as traversal role, visibility, and execution
  boundary rather than table names.
- Every fallback must be explicit, deterministic, contract-defined, and covered by
  tests. Never hide a special case inside a namespace list, role-name check, or
  unexplained default.
- Before adding a hard-coded list or branch, ask whether new assets, actions,
  parameters, or tables would work without another code edit. If not, redesign it
  as registry metadata, a schema rule, or another reusable contract.

## Development

- Do not modify the original TianshuDM repository from this repository.
- Keep generated contract snapshots deterministic.
- Prefer general capability improvements over case-specific fixes, even when the
  case-specific fix is smaller.
- Run `dotnet build RtsSkillStudio.sln` before completion.
- Run `node scripts/sync-contracts.mjs` after contract-factory changes.
- Do not commit unless the user explicitly requests it.
