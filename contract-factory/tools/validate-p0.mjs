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
  "contracts/authoring-patch.schema.json",
  "contracts/capability-registry.schema.json",
  "contracts/skill-graph.schema.json",
  "config/capability-registry.v0.json",
  "config/enum-values.v0.json",
  "config/enum-overlays.v0.json",
  "config/default-value-contract.v0.json",
  "config/default-mechanism-contract.v0.json",
  "config/read-only-tools.v0.json",
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
const planSchema = loaded["contracts/skill-config-plan.schema.json"];
const patchSchema = loaded["contracts/authoring-patch.schema.json"];

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
  assert(
    registryEnumNames.has(field.enumName),
    `Entity field ${field.path} references missing enum ${field.enumName}.`
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
  defaults.contractVersion === "0.1.0",
  "Default value contract version must be 0.1.0."
);
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

for (const schema of [planSchema, patchSchema]) {
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
  equivalenceRules.assertionPolicy.exactJson === false,
  "Semantic equivalence must not require exact JSON."
);

console.log(
  `validated ${registry.effects.length} effects, ${registry.conditions.length} conditions, ` +
    `${registry.intents.length} intents, and ${goldenCases.cases.length} golden cases`
);
console.log(`repository: ${repoRoot}`);
