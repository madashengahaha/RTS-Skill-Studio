# RTS Skill Studio

Independent natural-language skill configuration studio for the RTS project.

The product accepts a simple natural-language skill request and produces a complete,
validated Excel configuration change set:

```text
natural language
  -> SkillConfigPlan
  -> deterministic compiler
  -> WorkbookPatch
  -> schema/reference/semantic validation
  -> Excel-level diff
  -> user confirmation
  -> atomic write to an Excel or SVN working copy
```

The Studio owns its process, SQLite draft store, GUI, and Agent orchestration. It does
not reference, modify, or run the TianshuDM product. Selected TianshuDM code is vendored
under `vendor/TianshuDM` with provenance recorded in [vendor/VENDOR.md](vendor/VENDOR.md).

## Current Status

- Independent .NET 8 solution scaffolded.
- TianshuDM Contract, Domain, Application, Excel, and SQLite projects vendored.
- Agent and API projects created.
- Contract snapshot synchronized from `../RTS-Skill-Agent`.

## Layout

```text
contracts/rts-skill-agent/  Synced schemas, registry, eval data, and examples
src/RtsSkillStudio.Agent/   Agent, plan, compiler, and validation code
src/RtsSkillStudio.Api/     Local Studio HTTP host
vendor/TianshuDM/           Copied source snapshot from TianshuDM
web/                        Studio frontend, added in a later step
```

## Commands

```powershell
dotnet build RtsSkillStudio.sln
dotnet run --project src/RtsSkillStudio.Api
node scripts/sync-contracts.mjs
```
