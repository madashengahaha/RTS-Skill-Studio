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
    byKey
      .get("Damage")
      .parameters.find((parameter) => parameter.key === "fixedDamage")
      .conversionStatus,
    "ConfigDeclared"
  );
  assert.equal(
    registry.entityFields.find((field) => field.path === "Skill.cd_time")
      .conversionStatus,
    "WorkbookRoundTrip"
  );
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

test("execution projection distinguishes continuation from configuration", async () => {
  const registry = await readJson("config/capability-registry.v0.json");
  const effects = new Map(
    registry.effects.map((action) => [action.key, action])
  );
  const conditions = new Map(
    registry.conditions.map((action) => [action.key, action])
  );

  assert.equal(
    effects.get("Research").parameters.find(
      (parameter) => parameter.key === "searchId"
    ).executionProjection,
    "Node"
  );
  assert.equal(
    effects.get("Research").parameters.find(
      (parameter) => parameter.key === "effectGroupId"
    ).executionProjection,
    "Subtree"
  );
  assert.equal(
    effects.get("Damage").parameters.find(
      (parameter) => parameter.key === "pipeline"
    ).executionProjection,
    "Hidden"
  );
  assert.equal(
    effects.get("SummonAutoTrap").parameters.find(
      (parameter) => parameter.key === "trapId"
    ).executionProjection,
    "Subtree"
  );
  assert.equal(
    conditions.get("HasBuff").parameters.find(
      (parameter) => parameter.key === "buffId"
    ).executionProjection,
    "Node"
  );
  assert.deepEqual(
    registry.executionProjection.edgeRules.find(
      (rule) => rule.sourceField === "fx"
    ),
    {
      sourceField: "fx",
      projection: "Hidden"
    }
  );
});

test("nested value-source contracts are exposed for table learning", async () => {
  const registry = await readJson("config/capability-registry.v0.json");
  const valueSource = registry.nestedTypes.find(
    (item) => item.key === "ValueSource"
  );
  const shapeParameter = registry.fieldSemantics.find(
    (item) => item.path === "Search.shape_param"
  );

  assert.equal(valueSource.encoding.separator, ",");
  assert.deepEqual(valueSource.encoding.fields, [
    "PropId",
    "Scale",
    "Fix"
  ]);
  assert.equal(
    valueSource.fields.find((field) => field.key === "PropId").enumName,
    "NumericType"
  );
  assert.equal(shapeParameter.elementType, "ValueSource");
  assert.deepEqual(
    shapeParameter.indexRoles.map((item) => item.role),
    ["rangeOrLength", "shapeSecondary"]
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

test("plan contract exposes generic non-skill asset modification", async () => {
  const schema = await readJson("contracts/skill-config-plan.schema.json");
  const foundation = await readJson("config/registry-foundation.v0.json");

  assert.ok(
    schema.$defs.Operation.oneOf.some(
      (item) => item.$ref === "#/$defs/ModifyAssetOperation"
    )
  );
  assert.deepEqual(
    schema.$defs.ModifyAssetOperation.allOf[1].required,
    ["operationId", "kind", "asset", "fields"]
  );
  assert.equal(
    schema.$defs.ModifyAssetOperation.allOf[1].properties.kind.const,
    "ModifyAsset"
  );
  assert.ok(foundation.planOperations.includes("ModifyAsset"));
});

test("skill field metadata resolves semantic Plan names and units", async () => {
  const registry = await readJson("config/capability-registry.v0.json");
  const fields = new Map(
    registry.entityFields.map((field) => [field.path, field])
  );

  assert.deepEqual(
    {
      semanticName: fields.get("Skill.cd_time").semanticName,
      kind: fields.get("Skill.cd_time").kind,
      unit: fields.get("Skill.cd_time").unit,
      scale: fields.get("Skill.cd_time").scale
    },
    {
      semanticName: "cooldown",
      kind: "Integer",
      unit: "ms",
      scale: 1
    }
  );
  assert.equal(fields.get("Skill.duration").semanticName, "duration");
  assert.equal(fields.get("Skill.duration").unit, "ms");
});

test("workbook patch is immutable and validation is a separate report", async () => {
  const patch = await readJson("contracts/workbook-patch.schema.json");
  const validation = await readJson(
    "contracts/workbook-patch-validation.schema.json"
  );
  const errors = await readJson("config/workbook-patch-errors.v0.json");

  assert.equal(patch.title, "WorkbookPatch");
  assert.equal(
    Object.hasOwn(patch.properties, "validation"),
    false
  );
  assert.match(patch.properties.patchId.pattern, /\^\[a-f0-9\]/);
  assert.ok(
    patch.properties.base.required.includes("workspaceId")
  );
  assert.ok(
    patch.properties.base.required.includes("sourceHash")
  );
  assert.deepEqual(
    validation.$defs.Check.required,
    ["code", "status", "severity", "required", "message"]
  );
  assert.ok(
    errors.compile.some((item) => item.code === "compiler.no_change")
  );
  assert.ok(
    errors.validation.some(
      (item) => item.code === "validation.source_hash"
    )
  );
});

test("read-only tool input boundaries match the runtime contract", async () => {
  const contract = await readJson("config/read-only-tools.v0.json");
  const tools = new Map(
    contract.tools.map((tool) => [tool.name, tool])
  );
  const graph = tools.get("get_graph");
  const similar = tools.get("search_similar_skills");
  const chain = tools.get("explain_execution_chain");

  assert.equal(graph.inputSchema.properties.depth.minimum, 0);
  assert.equal(graph.inputSchema.properties.depth.maximum, 32);
  assert.deepEqual(
    graph.inputSchema.properties.direction.enum,
    ["out", "in", "both"]
  );
  assert.equal(
    similar.inputSchema.properties.limit.maximum,
    500
  );
  assert.deepEqual(chain.inputSchema.required, ["skill"]);
});
