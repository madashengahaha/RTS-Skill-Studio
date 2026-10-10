#!/usr/bin/env node

import {
  assert,
  duplicateValues,
  readJson,
  repoRoot,
  sorted
} from "./lib.mjs";

const requiredFiles = [
  "contracts/skill-config-plan.schema.json",
  "contracts/skill-plan-semantic-review.schema.json",
  "contracts/workbook-patch.schema.json",
  "contracts/workbook-patch-validation.schema.json",
  "contracts/capability-registry.schema.json",
  "contracts/skill-graph.schema.json",
  "config/capability-registry.v0.json",
  "config/enum-values.v0.json",
  "config/enum-overlays.v0.json",
  "config/default-value-contract.v0.json",
  "config/default-mechanism-contract.v0.json",
  "config/read-only-tools.v0.json",
  "config/value-conversion-audit.v0.json",
  "config/workbook-patch-errors.v0.json",
  "evals/golden-cases.schema.json",
  "evals/golden-cases.v0.json",
  "evals/equivalence-rules.v0.json"
];

const loaded = {};
for (const file of requiredFiles) {
  loaded[file] = await readJson(file);
}

const registry = loaded["config/capability-registry.v0.json"];
const defaults = loaded["config/default-value-contract.v0.json"];
const mechanisms = loaded["config/default-mechanism-contract.v0.json"];
const goldenCases = loaded["evals/golden-cases.v0.json"];
const equivalenceRules = loaded["evals/equivalence-rules.v0.json"];
const patchErrors = loaded["config/workbook-patch-errors.v0.json"];
const planSchema = loaded["contracts/skill-config-plan.schema.json"];
const patchSchema = loaded["contracts/workbook-patch.schema.json"];
const patchValidationSchema =
  loaded["contracts/workbook-patch-validation.schema.json"];
const valueConversionAudit =
  loaded["config/value-conversion-audit.v0.json"];

assert(registry.schemaVersion === 0, "Registry schemaVersion must be 0.");
assert(
  registry.registryVersion === "0.1.0",
  "Registry version must start at 0.1.0."
);
assert(
  /^[a-f0-9]{64}$/.test(registry.source.sha256),
  "Registry source hash must be SHA-256."
);
assert(
  typeof registry.source.sourceRevision === "string" &&
    registry.source.sourceRevision.length > 0,
  "Registry source revision must be pinned."
);
assert(
  registry.effects.length >= 39,
  `Expected at least 39 effects, found ${registry.effects.length}.`
);
assert(
  registry.conditions.length >= 11,
  `Expected at least 11 conditions, found ${registry.conditions.length}.`
);
assert(
  Array.isArray(registry.enums) &&
    registry.enums.length === registry.enumReferences.length,
  "Registry enums must cover every referenced enum."
);
assert(
  Array.isArray(registry.entityFields) && registry.entityFields.length > 0,
  "Registry must define entity-field enum bindings."
);
assert(
  Array.isArray(registry.readOnlyToolContracts?.tools),
  "Registry must embed read-only tool contracts."
);
assert(
  registry.skillGraphSchema?.title === "ReadOnlySkillGraph",
  "Registry must embed the read-only skill graph schema."
);

const registryEnumNames = new Set();
for (const definition of registry.enums) {
  assert(
    typeof definition.name === "string" && definition.name.length > 0,
    "Every registry enum must have a name."
  );
  assert(
    !registryEnumNames.has(definition.name),
    `Registry contains duplicate enum ${definition.name}.`
  );
  assert(
    Array.isArray(definition.values) && definition.values.length > 0,
    `Registry enum ${definition.name} must contain values.`
  );
  assert(
    typeof definition.flags === "boolean",
    `Registry enum ${definition.name} must declare flags.`
  );
  registryEnumNames.add(definition.name);
}
for (const enumName of registry.enumReferences) {
  assert(
    registryEnumNames.has(enumName),
    `Registry enum values are missing ${enumName}.`
  );
}

for (const field of registry.entityFields) {
  if (field.enumName) {
    assert(
      registryEnumNames.has(field.enumName),
      `Entity field ${field.path} references missing enum ${field.enumName}.`
    );
  }
  if (field.scale !== undefined) {
    assert(
      Number.isInteger(field.scale) && field.scale > 0,
      `Entity field ${field.path} scale must be a positive integer.`
    );
    assert(
      typeof field.conversionStatus === "string",
      `Entity field ${field.path} must declare conversionStatus.`
    );
  }
}

assert(
  valueConversionAudit.schemaVersion === 0,
  "Value conversion audit schemaVersion must be 0."
);
const conversionStatuses = new Set(valueConversionAudit.statuses);
assert(
  conversionStatuses.has(valueConversionAudit.defaultStatus),
  "Value conversion audit defaultStatus is invalid."
);
const conversionAuditKeys = new Set();
for (const entry of valueConversionAudit.entries) {
  assert(
    conversionStatuses.has(entry.status),
    `Value conversion audit has invalid status ${entry.status}.`
  );
  assert(
    Number.isInteger(entry.scale) && entry.scale > 0,
    "Value conversion audit scale must be a positive integer."
  );
  assert(
    Array.isArray(entry.evidence),
    "Value conversion audit evidence must be an array."
  );
  const key =
    entry.category === "entity-field"
      ? `entity-field:${entry.path}`
      : `${entry.category}:${entry.actionKey}:${entry.parameterKey}`;
  assert(
    !conversionAuditKeys.has(key),
    `Value conversion audit entry is duplicated: ${key}.`
  );
  conversionAuditKeys.add(key);
}

for (const [category, actions] of [
  ["effect", registry.effects],
  ["condition", registry.conditions]
]) {
  for (const action of actions) {
    for (const parameter of action.parameters) {
      if (Number.isInteger(parameter.scale)) {
        assert(
          conversionStatuses.has(parameter.conversionStatus),
          `${category}.${action.key}.${parameter.key} has invalid conversionStatus.`
        );
        assert(
          Array.isArray(parameter.conversionEvidence),
          `${category}.${action.key}.${parameter.key} conversionEvidence must be an array.`
        );
      }
    }
  }
}

const damageFixedDamage = registry.effects
  .find((action) => action.key === "Damage")
  ?.parameters.find((parameter) => parameter.key === "fixedDamage");
assert(
  damageFixedDamage?.scale === 10000 &&
    damageFixedDamage.conversionStatus === "ConfigDeclared",
  "Damage.fixedDamage must remain config-declared until runtime code is audited."
);
const skillCooldown = registry.entityFields.find(
  (field) => field.path === "Skill.cd_time"
);
assert(
  skillCooldown?.scale === 1 &&
    skillCooldown.conversionStatus === "WorkbookRoundTrip",
  "Skill.cd_time must be marked WorkbookRoundTrip."
);

for (const rule of defaults.conversionRules) {
  assert(
    Array.isArray(rule.inputAliases) && rule.inputAliases.length > 0,
    `Conversion rule ${rule.key} must declare input aliases.`
  );
  assert(
    Array.isArray(rule.outputAliases) && rule.outputAliases.length > 0,
    `Conversion rule ${rule.key} must declare output aliases.`
  );
}
const semanticFieldNames = new Map();
for (const field of registry.entityFields.filter((item) => item.semanticName)) {
  const entity = field.path.split(".")[0];
  const key = `${entity}.${field.semanticName}`;
  assert(
    !semanticFieldNames.has(key),
    `Entity field semantic name is duplicated: ${key}.`
  );
  semanticFieldNames.set(key, field.path);
}
for (const expected of [
  ["Skill.cooldown", "Skill.cd_time"],
  ["Skill.duration", "Skill.duration"]
]) {
  assert(
    semanticFieldNames.get(expected[0]) === expected[1],
    `Registry must resolve ${expected[0]} to ${expected[1]}.`
  );
}

const skillWeights = registry.similarityScoring?.skillWeights;
assert(
  skillWeights &&
    ["intents", "mechanisms", "tags", "references"].every(
      (key) => typeof skillWeights[key] === "number" && skillWeights[key] >= 0
    ),
  "Registry similarity scoring weights are required."
);

for (const [category, actions] of [
  ["effects", registry.effects],
  ["conditions", registry.conditions]
]) {
  const keys = actions.map((action) => action.key);
  const values = actions.map((action) => action.legacyValue);
  assert(
    duplicateValues(keys).length === 0,
    `${category} contain duplicate action keys.`
  );
  assert(
    duplicateValues(values).length === 0,
    `${category} contain duplicate legacy values.`
  );

  for (const action of actions) {
    assert(
      Array.isArray(action.semantic?.intents) &&
        action.semantic.intents.length > 0,
      `${category}.${action.key} has no semantic intent.`
    );
    assert(
      typeof action.semantic?.mechanism === "string" &&
        action.semantic.mechanism.length > 0,
      `${category}.${action.key} has no mechanism.`
    );
  }
}

const intentKeys = new Set(registry.intents.map((intent) => intent.key));
for (const action of [...registry.effects, ...registry.conditions]) {
  for (const intent of action.semantic.intents) {
    assert(
      intentKeys.has(intent),
      `${action.key} references unknown intent ${intent}.`
    );
  }
}

const mechanismIntentKeys = mechanisms.mechanisms.map(
  (mechanism) => mechanism.intent
);
assert(
  duplicateValues(mechanismIntentKeys).length === 0,
  "Default mechanism contract has duplicate intents."
);
assert(
  sorted(mechanismIntentKeys).join("|") === sorted(intentKeys).join("|"),
  "Default mechanism contract must cover every registry intent exactly once."
);

assert(
  defaults.contractVersion === "0.2.0",
  "Default value contract version must be 0.2.0 (creation policy)."
);
assert(defaults.creation?.allocationPolicy === "MaxPlusOne", "Creation must declare deterministic allocation.");
assert(defaults.creation?.cyclePolicy === "Reject", "Creation must declare cycle handling.");
assert(defaults.creation.entities.filter((entity) => entity.root).length === 1, "Creation needs exactly one root entity contract.");
for (const entity of defaults.creation.entities) {
  assert(registry.entities.some((item) => item.namespace === entity.namespace), `Unknown creation namespace ${entity.namespace}.`);
  assert(duplicateValues(entity.fields.map((field) => field.semanticName)).length === 0, `Duplicate semantic fields in ${entity.namespace}.`);
  assert(entity.fields.some((field) => field.key === entity.identityField), `Missing identity field in ${entity.namespace}.`);
  for (const key of Object.keys(entity.initialFields))
    assert(entity.fields.some((field) => field.key === key), `Unknown creation default ${entity.namespace}.${key}.`);
}
assert(
  defaults.fields.some((field) => field.path === "Skill.skill_type"),
  "Default value contract must define Skill.skill_type."
);

assert(
  goldenCases.caseSetVersion === "0.1.0",
  "Golden case set version must be 0.1.0."
);
assert(
  goldenCases.cases.length === 30,
  `P0 requires exactly 30 golden cases, found ${goldenCases.cases.length}.`
);
assert(
  duplicateValues(goldenCases.cases.map((item) => item.id)).length === 0,
  "Golden case identifiers must be unique."
);

for (const testCase of goldenCases.cases) {
  assert(
    /^p0-\d{3}$/.test(testCase.id),
    `Golden case id ${testCase.id} has an invalid format.`
  );
  assert(
    testCase.assertions.some(
      (assertion) =>
        assertion.kind === "status" &&
        assertion.equals === testCase.expectedStatus
    ),
    `${testCase.id} must assert its expected status.`
  );
}

for (const schema of [
  planSchema,
  patchSchema,
  patchValidationSchema
]) {
  assert(
    schema.$schema === "https://json-schema.org/draft/2020-12/schema",
    `${schema.title} must use JSON Schema draft 2020-12.`
  );
  assert(
    schema.type === "object",
    `${schema.title} must describe an object.`
  );
}

assert(
  !Object.prototype.hasOwnProperty.call(
    patchSchema.properties,
    "validation"
  ),
  "WorkbookPatch must not embed mutable validation results."
);
assert(
  patchSchema.properties.base.required.includes("workspaceId") &&
    patchSchema.properties.base.required.includes("sourceHash"),
  "WorkbookPatch base must include workspaceId and sourceHash."
);
assert(
  planSchema.$defs.Base.required.includes("sourceHash"),
  "SkillConfigPlan base must include sourceHash."
);
assert(
  patchValidationSchema.$defs.Check.required.includes("required") &&
    patchValidationSchema.$defs.Check.required.includes("severity"),
  "Patch validation checks must declare required and severity."
);

const patchErrorCodes = [
  ...patchErrors.compile,
  ...patchErrors.validation
].map((item) => item.code);
assert(
  patchErrorCodes.length > 0 &&
    new Set(patchErrorCodes).size === patchErrorCodes.length,
  "WorkbookPatch error codes must be non-empty and unique."
);
for (const item of patchErrors.compile) {
  assert(
    item.code.startsWith("compiler."),
    `Compile error code must start with compiler.: ${item.code}`
  );
}
for (const item of patchErrors.validation) {
  assert(
    item.code.startsWith("validation."),
    `Validation error code must start with validation.: ${item.code}`
  );
}

assert(
  equivalenceRules.assertionPolicy.exactJson === false,
  "Semantic equivalence must not require exact JSON."
);

console.log(
  `validated ${registry.effects.length} effects, ${registry.conditions.length} conditions, ` +
    `${registry.intents.length} intents, and ${goldenCases.cases.length} golden cases`
);
console.log(`repository: ${repoRoot}`);
