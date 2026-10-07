# Repository Rules

## Architecture

- Keep this repository independent from any existing configuration tool implementation.
- External projects are read-only evidence sources.
- Treat Excel/Luban data as authoritative. This repository stores contracts, generated
  snapshots, and evaluation data only.
- The model proposes `SkillConfigPlan`; deterministic code compiles and validates it.
- `WorkbookPatch` is immutable compiler output and must never be edited by a model,
  user, or validator.
- Validation results are represented by a separate `WorkbookPatchValidationReport`.

## Boundaries

- P0 and P1 do not mutate drafts, Excel, or generated game data.
- Do not invent action keys, parameter slots, enum values, IDs, or references.
- Structural data must be generated from the external semantic schema.
- Semantic annotations must be explicit and versioned.
- A fallback mechanism is valid only when a default-mechanism rule selects it.
- Unsupported requests return a structured result; they do not silently degrade.

## Generality

- Contracts and generation logic must describe reusable semantics, not individual
  skills, IDs, labels, workbook rows, or incident-specific examples.
- Do not encode traversal, visibility, defaults, or behavior in ad hoc namespace or
  action-name lists. Add versioned semantic metadata to the registry instead.
- Stable schema field names may appear in declarative projection rules when they
  are part of the public contract. They must not be hidden as unexplained code
  branches.
- Concrete fixtures and golden cases validate generic rules. They must not become
  production fallbacks or exceptions.
- Before adding a special case, define the generic dimension it represents, update
  the schema/config, regenerate snapshots, and add broad tests covering equivalent
  future cases.

## Development

- Use Node.js built-ins. Do not add runtime dependencies without an explicit decision.
- Keep generated JSON deterministic.
- Run `npm run validate` and `npm test` before completion.
- Do not commit unless the user explicitly requests it.
