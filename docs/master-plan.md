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
- [x] Retargeting an existing scalar reference through registry metadata.
- [x] Removing an existing scalar reference link with declared removal metadata.
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
- [x] Retargeting an existing scalar reference (first B3 slice).
- [x] Removing a scalar reference link (second B3 slice).
- [ ] List-valued references and `action_param` references.
- [ ] Shared-asset ownership and edit-lock policy.
- [x] Multiple supported modify operations in one Plan, compiled against one base
  revision and applied/undone as one transaction. Duplicate operation IDs and
  overlapping field writes are rejected; an invalid operation rejects the batch.
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

Status: three slices implemented, waiting for user validation. The rest of B3 is
not started.

Implemented slice 1: retargeting one existing scalar reference.

- the Plan writes an Existing `namespace` + `id` value into a reference field;
- the field is addressed by its registry `entityFields` semantic name;
- the registry declares the reference kind and its target namespace
  (`Skill.search_target` -> `TbSearch`);
- the compiler requires the declared target, cross-checks it against the
  workbook `#ref=` annotation, and rejects a target that is absent, has the
  wrong namespace, or is not an existing asset;
- a reference field whose registry entry does not declare `referenceTarget` is
  rejected even when the workbook annotation names one, so the registry stays
  authoritative;
- link changes travel through the same immutable `WorkbookPatch`, validation,
  diff, temporary apply, working-copy apply, and undo path as B2.

Implemented slice 2: `RemoveLink` for one existing scalar reference.

- the Plan uses the schema `RemoveLink` operation with `parent`, semantic
  `field`, and the `target` link it expects to remove;
- the compiler only unlinks when the field's current cell value is exactly that
  target, so a stale or mismatched plan fails closed with
  `compiler.remove_link_target_mismatch`;
- the workbook encoding of "no link" is the versioned
  `entityFields.referenceRemoval` metadata (`Null`, `Empty`, or `Zero`); a
  reference field without that declaration fails closed with
  `compiler.reference_removal_undeclared` instead of guessing;
- list-valued and `action_param` links fail closed with dedicated codes;
- removal travels through the same immutable `WorkbookPatch`, validation, diff,
  temporary apply, working-copy apply, and undo path as B2.

Still missing in B3:

- list-valued reference fields such as `condition_id_array`;
- `action_param` references;
- add, delete, and reorder Effect/Condition members;
- shared ownership and edit locks for linked assets;

Implemented slice 3: multi-operation modify transactions.

- combines supported `ModifySkill`, `ModifyAsset`, and `RemoveLink` operations;
- every operation resolves against the same immutable base revision;
- duplicate operation IDs and overlapping changed-field writes fail closed;
- any invalid operation rejects the entire Plan without emitting a Patch;
- no-change operations are omitted; an entirely unchanged Plan is `NoChange`;
- the full Plan hash and contiguous command sequence identify one immutable Patch;
- automated tests cover deterministic compilation, validation, failure isolation,
  temporary apply, final apply, and undo of multiple records.

### B4: Plan Editing And Recompilation

Status: after B3.

Scope:

- edit Plan values in the UI;
- recompile Patch after edits;
- rebase `UserEdited`, `ModelProposed`, and `Default` values on revision change;
- keep Patch immutable and generated only by the compiler.

### MVP-C1: Create One Complete Skill From Scratch

Status: generic creation compiler and Excel transaction pipeline implemented;
synthetic tests pass. One cloud-generated Skill/Search/Damage chain passed real
workbook-copy write/readback and undo after one semantic correction. Broader
mechanism coverage and first-pass generation reliability remain to be evaluated.
See `docs/skill-chain-creation.md`.

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
- `WorkbookPatchField` binding kind, parameter index, and reference contract.
- `WorkbookPatchRecordValue` and `WorkbookPatchFieldAccessor`.
- `WorkbookPatchReference` target resolution by declared namespace, not asset ID.
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
- [ ] Request a change to a skill's `search_target` reference and confirm the
  diff targets the reference field of `skill`.
- [ ] Confirm the target namespace and ID in the diff evidence resolve to a real
  `TbSearch` asset, and that an unknown ID is rejected before any write.
- [ ] Request a `RemoveLink` for a skill's `search_target` and confirm the diff
  writes the declared `referenceRemoval` encoding (`0` for `Skill.search_target`)
  and that a mismatched current target is rejected before any write.
- [ ] Request multiple supported changes and verify one combined diff, one
  confirmation, and one transaction undo restores all changed records.

## Related Documents

- `docs/phase-b2-generic-action-parameters.md`
- `docs/phase-b-modify-skill-vertical-slice.md`
- `docs/architecture.md`

## Capability acceptance boundary

Agent completeness is measured against runtime-supported, contract-expressible requirements, not existing examples or every design request. Runtime support, Studio contract/compiler support, and example/verification coverage must be assessed separately. See [agent-capability-acceptance.md](agent-capability-acceptance.md).
