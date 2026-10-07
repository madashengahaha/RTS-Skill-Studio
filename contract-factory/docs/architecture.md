# Architecture

```text
Natural-language request
  -> read-only capability context
  -> SkillConfigPlan
  -> deterministic compiler
  -> WorkbookPatch
  -> schema/reference/semantic validation
  -> field and graph diff
  -> user confirmation
  -> existing authoring commands
```

## Truth Ownership

| Layer | Owns |
| --- | --- |
| External Unity/Excel schema | Structural truth for actions, parameters, enums, references |
| Capability registry | A versioned snapshot plus semantic annotations |
| `SkillConfigPlan` | User/model intent, assumptions, clarifications, and value provenance |
| Compiler | Unit conversion, name resolution, ID allocation, mechanism selection |
| `WorkbookPatch` | Immutable deterministic command and field-change output |
| `WorkbookPatchValidationReport` | Validation status and required checks for one Patch |
| Validator | Schema, reference, ownership, revision, and semantic gates |
| Existing authoring service | Draft and Excel execution |

## Model Boundary

The model may:

- query read-only capability and graph tools;
- ask one structured clarification round;
- emit one complete typed plan;
- receive normalized server validation errors for bounded replanning.

The model may not:

- write authoring commands directly;
- edit a `WorkbookPatch`;
- invent identifiers or references;
- choose among equivalent mechanisms outside the default-mechanism contract;
- skip compile or validation.

## Rebase Principle

When the base revision changes:

- preserve `UserEdited` values when paths and references remain valid;
- regenerate `ModelProposed` values;
- refresh `Default` values from the active contract;
- mark conflicts and require confirmation again.
