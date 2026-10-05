# Repository Rules

## Architecture

- Keep this repository independent from any existing configuration tool implementation.
- External projects are read-only evidence sources.
- Treat Excel/Luban data as authoritative. This repository stores contracts, generated
  snapshots, and evaluation data only.
- The model proposes `SkillConfigPlan`; deterministic code compiles and validates it.
- `AuthoringPatch` is compiler output and must never be edited by a model or user.

## Boundaries

- P0 and P1 do not mutate drafts, Excel, or generated game data.
- Do not invent action keys, parameter slots, enum values, IDs, or references.
- Structural data must be generated from the external semantic schema.
- Semantic annotations must be explicit and versioned.
- A fallback mechanism is valid only when a default-mechanism rule selects it.
- Unsupported requests return a structured result; they do not silently degrade.

## Development

- Use Node.js built-ins. Do not add runtime dependencies without an explicit decision.
- Keep generated JSON deterministic.
- Run `npm run validate` and `npm test` before completion.
- Do not commit unless the user explicitly requests it.

