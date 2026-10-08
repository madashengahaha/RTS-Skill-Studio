# RTS Skill Studio Master Plan

## Purpose

This document is the repository-level implementation plan. It tracks what is
already delivered, what the user must verify, and the order of the remaining
phases.

The product goal is a natural-language skill configuration Agent that can:

1. Read and explain existing skill configuration.
2. Modify existing skills and behavior assets safely.
3. Write validated Excel changes to the configured work copy.
4. Create complete skills from scratch.
5. Support batch creation last.

## Current Status

### Read

- [x] Real Excel workspace loading.
- [x] Skill, Effect, Condition, Buff, Bullet, Search, Trap, and reference graph
  traversal.
- [x] Capability registry lookup.
- [x] Effect/Condition action parameter projection.
- [x] Enum alias and scaled value resolution.

### Modify Existing Assets

- [x] `ModifySkill` scalar field slice.
- [x] `ModifyAsset` for non-repeating Effect/Condition `action_param`.
- [x] Semantic parameter names map to `action_param[index]`.
- [x] Scale, unit, enum, and reference conversion.
- [x] WorkbookPatch compilation.
- [x] Patch validation against revision and source hash.
- [x] Excel field-level diff.
- [x] Temporary copy apply and re-read verification.
- [x] Source workbook apply and re-read verification.
- [x] Excel/file lock preflight.
- [x] Persistent transaction journal.
- [x] Backup and rollback.
- [x] Undo for a committed transaction.
- [ ] User verification of the current modify-existing-asset flow.

Verified example:

```text
TbEffect:100600180
action_param[1]: 1020 -> 1019
action_param[2]: 50000 -> 0
action_param[3]: 10000 -> 100000
```

The result was compiled, validated, applied to a temporary copy, written to a
test source workbook, re-read, and undone in automated tests.

### Still Missing In Modify Phase

- [ ] Repeating and paired action parameters.
- [ ] Adding, deleting, and reordering Effect/Condition nodes.
- [ ] Modifying existing links.
- [ ] Shared-asset ownership and edit-lock policy.
- [ ] Multiple operations in one Plan.
- [ ] Plan editing, recompilation, and revision rebase.
- [ ] Final confirmation placement decision: chat message versus diff/form.

## Phase Order

### B2: Modify Existing Action Parameters

Status: implemented, waiting for user validation.

Scope:

- one existing Effect or Condition;
- one `ModifyAsset` operation;
- non-repeating `action_param` parameters;
- compile, validate, diff, temp apply, source apply, rollback, and undo.

### B3: Modify Existing Skill Structure

Status: next after B2 validation.

Scope:

- modify existing references;
- add and remove Effect/Condition members;
- preserve group order where required;
- respect shared ownership and edit locks;
- prepare multi-operation Plan transactions.

### B4: Plan Editing And Recompilation

Status: after B3.

Scope:

- edit Plan values in the UI;
- recompile Patch after edits;
- rebase `UserEdited`, `ModelProposed`, and `Default` values on revision change;
- keep Patch immutable and generated only by the compiler.

### MVP-C1: Create One Complete Skill From Scratch

Status: after B4.

Scope:

- create a complete Skill and its required EffectGroup, Effect, Buff, Search,
  Trap, Bullet, and reference chain as required by the selected mechanism;
- allocate IDs and `group_id` values deterministically;
- choose the best implementation path from explicit mechanism rules;
- generate the full Excel diff;
- validate and write using the same B2 transaction pipeline.

### MVP-C2: Batch Creation

Status: final phase.

Scope:

- batch conversion and creation;
- shared ID allocation;
- duplicate and conflict detection;
- batch review and partial acceptance rules;
- per-item transaction status and batch-level audit.

## Extension Interfaces

The following interfaces must remain generic:

- `WorkbookPatchRegistry` action and parameter contracts.
- `WorkbookPatchField` binding kind and parameter index.
- `WorkbookPatchRecordValue` and `WorkbookPatchFieldAccessor`.
- target resolution by namespace and record, not asset ID.
- transaction journal and undo keyed by transaction ID.

New asset types or actions should extend registry metadata and workspace
projection rather than add concrete ID branches.

## User Verification Checklist

Use the current Studio at `http://localhost:5257/`.

- [ ] Open an existing Effect such as `TbEffect:100600180`.
- [ ] Request a supported non-repeating `action_param` change.
- [ ] Confirm the generated Excel diff uses `action_param[index]`.
- [ ] Confirm Patch validation is `Valid`.
- [ ] Click `确认并写入正式工作区`.
- [ ] Re-read the workbook and confirm the values.
- [ ] Click `撤销写入` and confirm the original values return.
- [ ] Confirm the transaction record exists under `.studio-work/transactions`.

## Related Documents

- `docs/phase-b2-generic-action-parameters.md`
- `docs/phase-b-modify-skill-vertical-slice.md`
- `docs/architecture.md`
