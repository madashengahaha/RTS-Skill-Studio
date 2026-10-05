# Architecture

## Product Boundary

RTS Skill Studio is independent from TianshuDM and Unity editor integration. TianshuDM
is a source of reusable implementation code and an optional interoperability target.

The Node contract factory is part of this repository under `contract-factory/`.
Generated contracts consumed by the .NET product are synchronized to
`contracts/rts-skill-agent/`.

```text
Natural-language request
  -> Agent orchestration
  -> SkillConfigPlan
  -> deterministic compiler
  -> WorkbookPatch
  -> schema/reference/semantic validation
  -> Excel-level diff
  -> user confirmation
  -> atomic Excel or SVN working-copy write
```

## Ownership

| Layer | Owns |
| --- | --- |
| Studio workspace | Selected Excel/SVN working copy and Studio SQLite drafts |
| Studio Agent | Intent understanding, plan proposal, clarification, and explanation |
| Studio compiler | IDs, nodes, groups, fields, references, and WorkbookPatch |
| Studio validator | Schema, reference, parameter, semantic, revision, and sourceHash checks |
| Studio UI | Plan editing, Excel diff, graph explanation, and confirmation |
| Vendored TianshuDM code | Reusable graph, draft, Excel, and SQLite mechanics |

The model never writes cells, SQL, or mutation commands. It emits only a typed plan.
