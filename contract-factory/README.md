# RTS Skill Studio Contract Factory

Language-neutral contracts and evaluation fixtures for a controlled natural-language
configuration agent. The agent proposes typed plans; deterministic server-side code
compiles, validates, and applies them through the existing authoring pipeline.

This repository intentionally starts clean. The legacy TianshuDM repository is only
an external source of schemas and semantic evidence.

## P0 Scope

P0 establishes contracts, not a writable product agent:

- capability registry generated from the current hero-authoring semantic schema;
- `SkillConfigPlan` and `AuthoringPatch` JSON Schemas;
- versioned default-value and default-mechanism contracts;
- 30 representative golden cases;
- semantic normalization and equivalence assertions;
- zero-dependency Node.js validation.

## P1 Scope

P1 adds a host-independent read-only assistant core:

- five read-only tool contracts;
- a bounded skill graph snapshot contract;
- reference implementations for capability lookup, asset resolution, graph queries,
  similar-skill search, and execution-chain explanation;
- a command-line host for inspection and debugging;
- strict graph and result validation;
- read-only evaluation cases and deterministic metrics.

P1 does not mutate drafts, Excel, generated data, or edit locks.

## Layout

```text
config/       Capability overlays and versioned contracts
contracts/    JSON Schemas for plans, patches, registry, and graph snapshots
docs/         Architecture and P0 decisions
evals/        Golden cases, read-only cases, and semantic equivalence rules
examples/     Small plans used by contract tests
src/          Pure semantic and read-only implementation code
tools/        Registry generation, CLI, evaluation, and validation
tests/        Node.js tests
```

See [CHANGELOG.md](CHANGELOG.md) for release-level changes and
[docs/implementation-log.md](docs/implementation-log.md) for round-by-round decisions.

## Commands

```bash
npm run validate
npm test
npm run eval:readonly
```

To refresh the generated capability registry from the external authoring schema:

```bash
npm run build:enums -- --builtin /path/to/builtin.xml
npm run build:registry -- --schema /path/to/hero-authoring-schema.json
```

To verify that the checked-in registry still matches the external source without
writing any file:

```bash
npm run verify:enums -- --builtin /path/to/builtin.xml
npm run verify:source -- --schema /path/to/hero-authoring-schema.json
```

The source schema is read-only. The command writes only
`config/capability-registry.v0.json` in this repository.

Inspect a read-only graph snapshot:

```bash
npm run readonly -- get_graph --root TbSkill:100101 --depth 2
```

## Boundary

- The registry and plans describe capabilities; they do not modify Excel.
- Models may only propose a plan. They do not write fields or call mutation commands.
- A plan must compile into an `AuthoringPatch` and pass deterministic validation before
  any future apply step.
- Unknown fields, actions, parameters, enum values, and references are rejected.
