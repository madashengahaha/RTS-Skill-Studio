# P1 Read-Only Assistant Core

## Goal

Provide deterministic, read-only tools that let a model or host application inspect
skill capabilities, references, execution chains, edit locks, and similar skills
before any plan is proposed.

The core is host-independent. A future TianshuDM sidebar can call the same functions
or expose the same tool contract through its API.

## Tool Contract

The contract is versioned in `config/read-only-tools.v0.json`:

| Tool | Purpose |
| --- | --- |
| `get_capability_context` | Return only the relevant registry slice. |
| `resolve_asset` | Resolve a stable ID or return all name candidates. |
| `get_graph` | Return a bounded inbound, outbound, or bidirectional projection. |
| `search_similar_skills` | Rank skills by semantic and structural overlap. |
| `explain_execution_chain` | Return observed execution steps with edge evidence. |

All tools return `readOnly: true`. None accept field-address writes, mutations, or raw
cell coordinates. The library entry point validates the embedded input schema before
executing any tool; the CLI applies the same contract.

## Data Source

P1 operates on a `ReadOnlySkillGraph` snapshot conforming to
`contracts/skill-graph.schema.json`.

- A node represents one stable asset identity.
- An edge represents one actual reference or virtual-group membership.
- Every edge carries an evidence reference.
- Edit locks are visible but never acquired or modified by these tools.
- The runtime validator rejects missing semantic data, edit locks, evidence, unknown
  namespaces, and namespace/kind mismatches.
- Runtime validation consumes the checked-in JSON Schema directly, including
  `additionalProperties: false` and key patterns.

`examples/sample-skill-graph.json` is a small structural fixture. It is not a copy of
production data.

## Commands

```bash
npm run validate
npm run eval:readonly

npm run readonly -- get_graph --root TbSkill:100101 --depth 2
npm run readonly -- resolve_asset --namespace TbSkill --id 100101
npm run readonly -- explain_execution_chain --skill TbSkill:100101
```

Use `--graph` to point the CLI at another read-only snapshot and `--registry` to point
at another registry version.

## Acceptance

- The five tool names exactly match the capability registry.
- Name resolution never auto-selects a candidate.
- Name resolution reports total matches, returned count, and truncation.
- Capability context returns `NotFound` on a real zero-match query and reports explicit
  per-category totals and truncation.
- Capability context returns compact namespaces and bounded enum values.
- Entity fields such as `Skill.skill_type`, `Search.team`, and
  `DamagePipeline.PipelineStage_param` expose their legal enum bindings.
- Graph projections never return dangling edges and enforce independent node and edge
  limits.
- Execution explanations contain only observed nodes and evidence-bearing edges.
- Shared subtrees are emitted once and marked as reused.
- Every result carries `graphVersion`, `workspaceId`, and `revision`.
- Capability context returns legal namespaces and enum values.
- Evaluation fixtures cover stable IDs, ambiguous names, reverse references, missing
  assets, unknown actions, lock visibility, invalid inputs, truncation, evidence, and
  zero-depth queries.
- Source snapshots are cloned and frozen by the index.
