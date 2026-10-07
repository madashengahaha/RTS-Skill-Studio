# Phase B: Modify Existing Skill Vertical Slice

## Goal

Deliver the first end-to-end editing path for one existing skill:

```text
SkillConfigPlan
  -> deterministic WorkbookPatch compiler
  -> deterministic validation
  -> Excel field-level diff
  -> apply to a temporary workspace copy
  -> re-read and verify
```

The source workbook remains untouched throughout this phase.

## Progress

- [x] Freeze Patch contracts and terminology.
- [x] Implement the deterministic Plan compiler.
- [x] Implement Patch validation.
- [x] Build Excel diff projection.
- [x] Add temporary workspace apply and re-read.
- [x] Integrate the editing loop.
- [x] Add automated coverage and pass the completion criteria.

## Verified

```text
dotnet build RtsSkillStudio.sln
dotnet test RtsSkillStudio.sln
npm run validate
npm test
npm run eval:readonly
npm run eval:modify
```

- Real workspace smoke: `TbSkill:100101`, semantic `cooldown = 8s` compiled to
  `cd_time = 8000`, validation returned `Valid`, the Excel diff contained one
  field change, and temporary apply/re-read returned `Verified`.
- The source-root hash was `fc5f0c721c5c47eba4de45dd16eb0547a4a1480b7ad9d192a5f032711f9412aa`
  before and after temporary apply.

## Baseline

- Phase A is complete.
- The Agent has a bounded read-only tool loop.
- Intent routing covers query, configuration, create, clarification, and
  unsupported requests.
- Clarification and unsupported results have structured contracts.
- `SkillConfigPlan` is validated against the generated JSON Schema.
- No `WorkbookPatch` compiler or patch validator exists yet.
- The current write smoke test only rewrites and re-reads a copied table.
- `WorkbookPatch` and `WorkbookPatchValidationReport` contracts are now
  separate and versioned.

## Initial Scope

Support only:

- one existing skill selected by the user;
- one `ModifySkill` operation per request;
- Plan fields use capability-registry semantic names, starting with `cooldown`
  and `duration`;
- Plan values declare their semantic unit; the compiler alone converts them to
  workbook cell values;
- deterministic compilation and validation;
- display of the resulting Excel field changes;
- apply and verification against a temporary workspace copy.

Re-planning is the only user correction path in this phase. Direct Plan editing
is a follow-on capability.

Do not include:

- Effect, Condition, Buff, Bullet, or Search restructuring;
- new skill or asset creation;
- source workbook writes;
- Luban publishing;
- transactional source apply or rollback;
- batch operations.

## Work Packages

### 1. Freeze Patch Contracts And Terminology

- `WorkbookPatch` is the only product-facing name and schema for deterministic
  compiler output.
- Validation is a separate immutable `WorkbookPatchValidationReport`; a
  validator must not edit a Patch in place.
- `patchId` is SHA-256 over canonical Patch JSON with `patchId` omitted.
- Patch base identity contains `workspaceId`, catalog `revision`, source-root
  `sourceHash`, and all contract versions used during compilation.
- Field changes contain a stable logical address and record workbook cell values
  in `before`/`after`; semantic value and unit remain explicit provenance.
- Validation checks declare `required`, `severity`, and `status`.
- Compile failures and validation failures use the versioned codes in
  `workbook-patch-errors.v0.json`.

Deliverable:

- Completed: one immutable Patch contract, one separate validation report, and
  no competing `AuthoringPatch` product concept.

### 2. Implement The Deterministic Plan Compiler

- Parse only the supported `ModifySkill` subset.
- Resolve the target skill against the current workspace revision.
- Resolve semantic Plan field names through `entityFields.semanticName`.
- Validate every requested field against capability-registry metadata.
- Apply declared scale and unit conversion rules to produce workbook cell values.
- Produce ordered `commands` and `fieldChanges`.
- Record `before`, `after`, `semanticValue`, `semanticUnit`, and value
  provenance.
- Compute `sourcePlanHash` and stable patch identity.
- Return structured compiler errors instead of partial patches.
- Treat an all-no-op request as a structured `NoChange` result, not as an empty
  Patch. `commands.minItems` remains one.
- Reject any request containing more than one operation or an operation other
  than `ModifySkill` as a compiler error.

Deliverable:

- A pure compiler that can run without Excel writes.
- Identical Plan plus identical base snapshot produces an identical Patch.

### 3. Implement Patch Validation

Validate:

- Patch JSON Schema;
- target asset existence and type;
- field existence and value type;
- enums, value ranges, units, and scale;
- references and target types where applicable;
- workspace, revision, and source hash;
- ownership and edit-lock constraints when published by the workspace.

Validation must:

- return a separate `WorkbookPatchValidationReport`;
- mark a required check failed when it is `Failed` or `NotRun`;
- return `Invalid` when any required check fails;
- prevent all apply work when validation fails;
- avoid model-specific or asset-id-specific branches.

Deliverable:

- A fail-closed validator consumed by both API and tests.

### 4. Build Excel Diff Projection

- Project `fieldChanges` into table, record, field, before, after, source, and
  evidence rows.
- Include the Patch `logicalAddress` for every change.
- Distinguish no-op values from real changes.
- Keep graph or reference impact optional for this first scalar-field slice.
- Expose the result through the Studio API for the Plan and Evidence inspector.

Deliverable:

- A user-readable Excel diff that exactly matches the validated Patch.

### 5. Add Temporary Workspace Apply And Re-Read

- Create a unique workspace copy under `.studio-work`.
- Apply only validated patches to the copy.
- Re-read the affected workbook table from disk.
- Compare expected and re-read values field by field.
- Report source hash, copied hash, written hash, and verification status.
- Preserve the existing writer round-trip smoke test as a separate probe.
- Reuse `WriteTestRoot` and prune old `write-smoke-*` runs using
  `WriteTestRetentionCount`.
- Never write to the configured source data root.

Deliverable:

- A reusable temporary apply service in addition to the existing write smoke
  test.

### 6. Integrate The Editing Loop

Add the backend and UI states needed for:

```text
Plan ready
  -> compile
  -> validate
  -> diff
  -> temporary apply
  -> re-read verification
```

The UI must show:

- compiler errors;
- validation checks;
- Excel field changes;
- temporary apply result;
- an explicit statement that the source workbook was not modified.

No source confirmation or source write action belongs in this phase.

### 7. Add Automated Coverage

Required tests:

- supported field produces the expected Patch;
- unsupported field is rejected before apply;
- invalid enum or value type is rejected;
- revision mismatch is rejected;
- source-root hash mismatch is rejected;
- compilation is deterministic across repeated runs;
- all-no-op requests produce a structured `NoChange` result and zero writes;
- diff rows match Patch field changes;
- temporary apply and re-read return the expected values;
- invalid Patch performs zero writes;
- source data-root hash is unchanged;
- model-side `eval:modify` resolves the `p0-012` cooldown case to semantic
  `ModifySkill.cooldown = 8s` and the compiler produces cell value `8000`;
- the existing read-only and Phase A tests continue to pass.

## Completion Criteria

Phase B is complete when:

- a user can select one real existing skill and request a supported scalar-field
  change in natural language;
- the Agent produces a valid typed `SkillConfigPlan`;
- Studio compiles it into a deterministic `WorkbookPatch`;
- validation either passes or blocks the operation with structured errors;
- the UI displays the exact Excel-level field diff;
- the validated Patch applies to a temporary workspace copy and passes re-read
  verification of the expected field values;
- the source data-root hash remains byte-for-byte unchanged;
- `dotnet build RtsSkillStudio.sln` succeeds;
- `dotnet test RtsSkillStudio.sln` succeeds;
- `npm run validate` succeeds in `contract-factory`;
- `npm test` succeeds in `contract-factory`;
- `npm run eval:readonly` succeeds.

## Follow-On Phases

After this vertical slice is stable:

1. Extend modification support to Effect, Condition, Buff, Bullet, and Search
   references.
2. Add Plan editing and recompilation after user changes.
3. Add source working-copy confirmation, atomic apply, backup, rollback, and undo.
4. Add source workbook writing and Luban release integration.
5. Add create-from-scratch and batch capabilities.
