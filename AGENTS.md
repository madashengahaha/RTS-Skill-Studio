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

## Development

- Do not modify the original TianshuDM repository from this repository.
- Keep generated contract snapshots deterministic.
- Run `dotnet build RtsSkillStudio.sln` before completion.
- Run `node scripts/sync-contracts.mjs` after contract-factory changes.
- Do not commit unless the user explicitly requests it.
