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
- Unified LLM provider configuration with OpenAI Responses and
  OpenAI-compatible chat adapters.
- Node contract factory migrated into `contract-factory/`.
- Product contract snapshot generated into `contracts/rts-skill-agent/`.

## Layout

```text
contract-factory/           Node contract factory, registry tools, and evals
contracts/rts-skill-agent/  Generated schemas, registry, eval data, examples
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

## Local LLM

Ollama is configured as the default provider at
`http://127.0.0.1:11434/v1` with model `qwen3.5:4b`.

The cloud provider uses the OpenAI Responses API. Set `OPENAI_API_KEY` and fill
in the `openai` model in `src/RtsSkillStudio.Api/appsettings.json`, or override
configuration with environment variables.

CC Switch can be used as an optional development provider while its local proxy
is running on `127.0.0.1:15721`. The `ccswitch` provider routes through the
currently selected Codex provider and is not a product dependency.

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
POST /api/v1/workspace/write-smoke-test
```

The Studio UI is served at `http://127.0.0.1:5257/`. The write smoke test copies
the Excel data root into `.studio-work/`, rewrites `Skill.xlsx` in that copy,
then re-reads and compares all configured fields. It never writes the source
workbook.
