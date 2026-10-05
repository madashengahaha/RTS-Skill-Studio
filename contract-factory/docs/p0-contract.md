# P0 Contract Prototype

## Goal

Prove that natural-language skill configuration can be represented as a typed,
validatable contract before a model or product UI is connected.

## Required Deliverables

1. Capability registry v0 with structural and semantic layers.
2. `SkillConfigPlan` JSON Schema.
3. `AuthoringPatch` JSON Schema.
4. Versioned default-value contract.
5. Versioned default-mechanism contract.
6. Thirty golden cases.
7. Semantic normalization and equivalence assertion rules.
8. Deterministic validation and tests.

## Non-Goals

- No chat UI.
- No model provider integration.
- No draft or Excel mutation.
- No direct field-address writes.
- No automatic creation of unsupported runtime capabilities.

## Acceptance

- The generated registry contains every effect and condition from the source schema.
- Every action has a semantic mapping and mechanism classification.
- Duplicate keys, missing annotations, invalid references, and invalid golden cases fail
  validation.
- Two semantically equivalent plans normalize to the same IR.
- A behaviorally different plan fails the equivalence assertion.
- Golden case count and identifier uniqueness are machine-checked.

## Source Rules

The structural layer is generated from the current `hero-authoring-schema.json`.
The semantic layer is maintained in this repository and must be reviewed when Unity
runtime handlers, enums, or parameter semantics change.

