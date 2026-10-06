import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

async function readJson(name) {
  return JSON.parse(await readFile(path.join(repoRoot, name), "utf8"));
}

test("capability registry matches the runtime action enum", async () => {
  const registry = await readJson("config/capability-registry.v0.json");
  const enumSnapshot = await readJson("config/enum-values.v0.json");
  const runtimeActions = enumSnapshot.enums
    .find((item) => item.name === "EffectActionType")
    .values.filter((item) => item.name !== "None");

  assert.deepEqual(
    registry.effects.map((action) => [action.key, action.legacyValue]),
    runtimeActions.map((action) => [action.name, action.value])
  );
  assert.equal(
    registry.effects.some((action) => action.key === "AddShield"),
    false
  );
  assert.deepEqual(
    registry.effects
      .filter((action) => ["AddMaxPropertyWithCurrent", "SubMaxPropertyWithCurrent"].includes(action.key))
      .map((action) => action.key),
    ["AddMaxPropertyWithCurrent", "SubMaxPropertyWithCurrent"]
  );
});

test("runtime-backed parameter contracts are exposed to the agent", async () => {
  const registry = await readJson("config/capability-registry.v0.json");
  const byKey = new Map(registry.effects.map((action) => [action.key, action]));

  assert.equal(byKey.get("Healing").maxParameterCount, 6);
  assert.equal(byKey.get("Healing").parameters.length, 6);
  assert.equal(byKey.get("AddHeightBuff").minParameterCount, 1);
  assert.equal(byKey.get("AddHeightBuff").parameters[1].required, false);
  assert.equal(byKey.get("AddBuff").minParameterCount, 1);
  assert.equal(byKey.get("RemoveBuff").parameters[1].required, false);
  assert.equal(byKey.get("AddFlagLabel").maxParameterCount, 2);
  assert.equal(byKey.get("RemoveFlagLabel").parameters[1].required, false);
  assert.equal(byKey.get("AddBuff").parameters[1].defaultValue, 0);
  assert.equal(byKey.get("RemoveBuff").parameters[1].defaultValue, 0);
  assert.equal(byKey.get("AddHeightBuff").parameters[1].defaultValue, 0);
  assert.equal(byKey.get("ClearHitMarks").parameters[0].defaultValue, 2);
  assert.equal(byKey.get("Knockback").parameters[0].defaultValue, 0);
  assert.equal(byKey.get("Knockback").parameters[1].defaultValue, 15000);
  assert.equal(
    registry.conditions
      .find((action) => action.key === "HasBuff")
      .parameters[1].defaultValue,
    1
  );
  assert.equal(
    registry.conditions
      .find((action) => action.key === "IsAlive")
      .parameters[0].defaultValue,
    0
  );
  assert.equal(
    registry.conditions
      .find((action) => action.key === "TryMarkHit")
      .parameters[0].defaultValue,
    2
  );
  assert.equal(
    registry.entityFields.some(
      (field) => field.path === "Buff.status_effect_type" &&
        field.enumName === "ESkillBuffType"
    ),
    true
  );
});

test("runtime enums include values used by current workbooks", async () => {
  const enumSnapshot = await readJson("config/enum-values.v0.json");
  const byName = new Map(
    enumSnapshot.enums.map((item) => [item.name, item])
  );

  assert.equal(
    byName.get("ESkillType").values.find((item) => item.value === 5).name,
    "Auto"
  );
  assert.equal(
    byName.get("ESkillBuffType").values.find((item) => item.value === 5).name,
    "Disengage"
  );
  assert.ok(byName.has("EConsumeType"));
  assert.ok(byName.has("ESkillBuffOverlapType"));
  for (const name of [
    "EGroupCompletedType",
    "ESearchTargetShape",
    "ESearchTargetTeam",
    "EUnitType",
    "FlagLabel",
    "FlagRestrict",
    "NumericType"
  ]) {
    assert.equal(byName.get(name).sourceKind, "UnityGeneratedEnum");
  }
  assert.equal(byName.get("TeamRelation").sourceKind, "ParameterConvention");
  assert.match(byName.get("TeamRelation").comment, /EFactionRelation/);
});

test("enum snapshot is generated from the source XML and Unity generated code", async () => {
  const enumSnapshot = await readJson("config/enum-values.v0.json");

  assert.equal(
    enumSnapshot.source.unityGenerated.declaredRuntimeEnum.endsWith(
      "EffectActionType.cs"
    ),
    true
  );
  assert.ok(
    enumSnapshot.source.unityGenerated.files.some(
      (file) => file.fileName === "EConditionType.cs"
    )
  );
  assert.equal(
    enumSnapshot.source.unityGenerated.files.length,
    15
  );
  assert.equal(
    enumSnapshot.enums.find((item) => item.name === "EffectActionType")
      .sourceKind,
    "UnityGeneratedEnum"
  );
});
