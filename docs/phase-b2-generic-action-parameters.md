# Phase B2: Generic Action Parameter Modification

## Position

Phase B is not the product-level `MVP-B` by itself. The original Phase B slice
only delivered one `ModifySkill` vertical slice.

Phase B2 completes the next part of `MVP-B`: modifying existing Effect and
Condition action parameters through the capability registry.

## Scope

Supported:

- one existing Effect or Condition;
- one `ModifyAsset` operation per request;
- non-repeating `action_param` parameters;
- semantic Plan field names from the action parameter contract, such as
  `attackType`, `fixedDamage`, and `attackScale`;
- deterministic binding from semantic parameter key to `action_param[index]`;
- enum, reference, scaled integer, integer, boolean, and text parameters;
- the existing compile, validate, diff, temporary apply, and source apply
  pipeline.

Not yet supported:

- repeating or paired parameters;
- adding, deleting, or reordering Effect or Condition nodes;
- modifying links between assets;
- multiple operations in one Plan;
- Plan editing and recompilation;
- create-from-scratch and batch creation.

## Extension Interfaces

`WorkbookPatchRegistry` now carries action contracts loaded from the capability
registry.

`WorkbookPatchField` carries:

- `BindingKind`: scalar field or action parameter;
- `RecordId`;
- `ActionKey`;
- `ParameterIndex`;
- `Repeating`.

`WorkbookPatchRecordValue` and `WorkbookPatchFieldAccessor` provide one
deterministic read/write path for scalar fields and indexed `action_param`
values.

These interfaces are intentionally generic. Adding Condition, Buff, Bullet, or
Search bindings should extend registry metadata and workspace projection rather
than add asset-id-specific compiler branches.

## Acceptance

- A `ModifyAsset` Plan targeting `TbEffect:100600180` compiles to:
  - `action_param[1] = 1019`
  - `action_param[2] = 0`
  - `action_param[3] = 100000`
- Patch validation succeeds against the current workspace revision and source
  hash.
- Temporary apply writes the expected indexed values to a copied workbook and
  re-reads them successfully.
- Source apply writes the values to the configured working copy and re-reads
  them successfully.
- Source apply creates backups and rolls back when final verification fails.
- Source apply refuses to write while the target workbook is locked by Excel
  or another writer.
- Source apply persists a transaction journal under `.studio-work/transactions`.
- A committed transaction can be undone and restores the backed-up workbooks.
- No compiler path branches on the concrete asset ID.

## Next

Phase B3 should cover:

- modifying existing links;
- adding and deleting Effect or Condition members;
- shared-asset ownership and edit-lock behavior;
- multi-operation planning.

Phase B4 should cover Plan editing, recompilation, and revision rebase.

`MVP-C` remains the later phase for create-from-scratch, ID allocation, chain
selection, batch conversion, and release integration.
