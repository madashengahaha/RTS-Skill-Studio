#!/usr/bin/env node

import { readFile } from "node:fs/promises";
import path from "node:path";
import {
  assert,
  duplicateValues,
  externalSourcePath,
  parseArgs,
  readJson,
  repoRoot,
  sha256,
  sorted,
  writeJson
} from "./lib.mjs";

const args = parseArgs(process.argv.slice(2));
const sourcePath =
  typeof args.schema === "string"
    ? path.resolve(args.schema)
    : await externalSourcePath("heroAuthoringSchema");
const externalSources = await readJson("config/external-sources.v0.json");
const sourceText = await readFile(sourcePath, "utf8");
const sourceSchema = JSON.parse(sourceText);
const foundation = await readJson("config/registry-foundation.v0.json");
const overlay = await readJson("config/semantic-overlay.v0.json");
const runtimeActionOverrides = await readJson(
  "config/runtime-action-overrides.v0.json"
);
const valueConversionAudit = await readJson(
  "config/value-conversion-audit.v0.json"
);
const executionProjection = await readJson(
  "config/execution-projection.v0.json"
);
const defaultValueContract = await readJson("config/default-value-contract.v0.json");
const mechanismContract = await readJson("config/default-mechanism-contract.v0.json");
const enumSnapshot = await readJson(
  args.enums ?? "config/enum-values.v0.json"
);
const readOnlyToolContract = await readJson("config/read-only-tools.v0.json");
const skillGraphSchema = await readJson("contracts/skill-graph.schema.json");

assert(
  Number.isInteger(sourceSchema.version) && sourceSchema.version > 0,
  "Source schema version must be a positive integer."
);
assert(Array.isArray(sourceSchema.effects), "Source schema must contain effects.");
assert(
  Array.isArray(sourceSchema.conditions),
  "Source schema must contain conditions."
);
assert(
  Array.isArray(sourceSchema.damageStages),
  "Source schema must contain damageStages."
);
assert(Array.isArray(enumSnapshot.enums), "Enum snapshot must contain enums.");
assert(
  Array.isArray(readOnlyToolContract.tools),
  "Read-only tool contract must contain tools."
);
assert(
  Array.isArray(runtimeActionOverrides.removeActions),
  "Runtime action overrides must contain removeActions."
);
assert(
  Array.isArray(runtimeActionOverrides.addActions),
  "Runtime action overrides must contain addActions."
);
assert(
  runtimeActionOverrides.patchActions &&
    typeof runtimeActionOverrides.patchActions === "object" &&
    !Array.isArray(runtimeActionOverrides.patchActions),
  "Runtime action overrides must contain patchActions."
);
assert(
  runtimeActionOverrides.patchConditions &&
    typeof runtimeActionOverrides.patchConditions === "object" &&
    !Array.isArray(runtimeActionOverrides.patchConditions),
  "Runtime action overrides must contain patchConditions."
);
assert(
  valueConversionAudit.schemaVersion === 0,
  "Value conversion audit schemaVersion must be 0."
);
assert(
  Array.isArray(valueConversionAudit.statuses) &&
    valueConversionAudit.statuses.length > 0,
  "Value conversion audit must declare statuses."
);
assert(
  Array.isArray(valueConversionAudit.entries),
  "Value conversion audit must contain entries."
);
assert(
  executionProjection.schemaVersion === foundation.schemaVersion,
  "Execution projection schemaVersion must match the foundation schemaVersion."
);
assert(
  Array.isArray(executionProjection.parameterRules),
  "Execution projection must contain parameterRules."
);
assert(
  Array.isArray(executionProjection.edgeRules),
  "Execution projection must contain edgeRules."
);
assert(
  readOnlyToolContract.tools.map((tool) => tool.name).sort().join("|") ===
    [...foundation.readOnlyTools].sort().join("|"),
  "Read-only tool contract must match the foundation tool list."
);

const intentKeys = new Set(foundation.intents.map((intent) => intent.key));
const mechanismIntents = new Set(
  mechanismContract.mechanisms.map((mechanism) => mechanism.intent)
);

for (const intent of intentKeys) {
  assert(
    mechanismIntents.has(intent),
    `Default mechanism contract is missing intent ${intent}.`
  );
}

const defaultValueFields = new Set(
  defaultValueContract.fields.map((field) => field.path)
);
assert(
  defaultValueFields.has("Skill.skill_type"),
  "Default value contract must define the Skill.skill_type default."
);

const executionProjectionValues = new Set(["Subtree", "Node", "Hidden"]);
const valueConversionStatuses = new Set(valueConversionAudit.statuses);
assert(
  valueConversionStatuses.has(valueConversionAudit.defaultStatus),
  "Value conversion audit defaultStatus is invalid."
);
for (const entry of valueConversionAudit.entries) {
  assert(
    valueConversionStatuses.has(entry.status),
    `Value conversion audit entry has invalid status: ${entry.status}.`
  );
  assert(
    entry.status !== "RuntimeCodeVerified" ||
      (Array.isArray(entry.evidence) && entry.evidence.length > 0),
    "RuntimeCodeVerified conversion entries must include runtime evidence."
  );
}

function conversionAuditKey(category, ownerKey, parameterKey) {
  return `${category}:${ownerKey}:${parameterKey}`;
}

const valueConversionByActionParameter = new Map(
  valueConversionAudit.entries
    .filter(
      (entry) =>
        entry.category === "effect" || entry.category === "condition"
    )
    .map((entry) => [
      conversionAuditKey(
        entry.category,
        entry.actionKey,
        entry.parameterKey
      ),
      entry
    ])
);
const valueConversionByEntityField = new Map(
  valueConversionAudit.entries
    .filter((entry) => entry.category === "entity-field")
    .map((entry) => [entry.path, entry])
);

function conversionMetadata(category, ownerKey, parameterKey, scale) {
  if (!Number.isInteger(scale)) {
    return {};
  }

  const entry = valueConversionByActionParameter.get(
    conversionAuditKey(category, ownerKey, parameterKey)
  );
  assert(
    !entry || entry.scale === scale,
    `${category}.${ownerKey}.${parameterKey} conversion scale does not match the audit entry.`
  );
  return {
    unit: entry?.unit ?? null,
    conversionStatus:
      entry?.status ?? valueConversionAudit.defaultStatus,
    conversionEvidence: entry?.evidence ?? []
  };
}

function entityConversionMetadata(field) {
  if (!Number.isInteger(field.scale)) {
    return {};
  }

  const entry = valueConversionByEntityField.get(field.path);
  assert(
    !entry || entry.scale === field.scale,
    `${field.path} conversion scale does not match the audit entry.`
  );
  return {
    unit: entry?.unit ?? field.unit ?? null,
    conversionStatus:
      entry?.status ?? valueConversionAudit.defaultStatus,
    conversionEvidence: entry?.evidence ?? []
  };
}

for (const [ruleSetName, rules] of [
  ["parameterRules", executionProjection.parameterRules],
  ["edgeRules", executionProjection.edgeRules]
]) {
  for (const rule of rules) {
    assert(
      typeof rule === "object" && rule !== null && !Array.isArray(rule),
      `Execution projection ${ruleSetName} entries must be objects.`
    );
    assert(
      executionProjectionValues.has(rule.projection),
      `Execution projection ${ruleSetName} contains invalid projection ${rule.projection}.`
    );
    const selectors =
      ruleSetName === "parameterRules"
        ? [
            rule.category,
            rule.actionKey,
            rule.parameterKey,
            rule.referenceTarget
          ]
        : [rule.role, rule.sourceField];
    assert(
      selectors.some((value) => typeof value === "string" && value.length > 0),
      `Execution projection ${ruleSetName} entries must define a selector.`
    );
  }
}
assert(
  executionProjectionValues.has(
    executionProjection.defaultParameterProjection
  ),
  "Execution projection defaultParameterProjection is invalid."
);
assert(
  executionProjectionValues.has(executionProjection.defaultEdgeProjection),
  "Execution projection defaultEdgeProjection is invalid."
);

function matchesExecutionRule(rule, category, action, parameter) {
  return (
    (!rule.category || rule.category === category) &&
    (!rule.actionKey || rule.actionKey === action.key) &&
    (!rule.parameterKey || rule.parameterKey === parameter.key) &&
    (!rule.referenceTarget ||
      rule.referenceTarget === parameter.referenceTarget)
  );
}

function parameterExecutionProjection(category, action, parameter) {
  const rule = executionProjection.parameterRules.find((candidate) =>
    matchesExecutionRule(candidate, category, action, parameter)
  );
  return rule?.projection ?? executionProjection.defaultParameterProjection;
}

function decorateActions(actions, annotations, category) {
  const actionKeys = actions.map((action) => action.key);
  const annotationKeys = Object.keys(annotations);
  const missing = actionKeys.filter((key) => !annotations[key]);
  const unknown = annotationKeys.filter((key) => !actionKeys.includes(key));
  const duplicateKeys = duplicateValues(actionKeys);
  const duplicateValuesFound = duplicateValues(
    actions.map((action) => action.legacyValue)
  );

  assert(
    missing.length === 0,
    `${category} semantic overlay is missing: ${missing.join(", ")}`
  );
  assert(
    unknown.length === 0,
    `${category} semantic overlay contains unknown keys: ${unknown.join(", ")}`
  );
  assert(
    duplicateKeys.length === 0,
    `${category} contains duplicate keys: ${duplicateKeys.join(", ")}`
  );
  assert(
    duplicateValuesFound.length === 0,
    `${category} contains duplicate numeric values: ${duplicateValuesFound.join(", ")}`
  );

  return actions
    .map((action) => {
      const semantic = annotations[action.key];
      for (const intent of semantic.intents) {
        assert(
          intentKeys.has(intent),
          `${category}.${action.key} references unknown intent ${intent}.`
        );
      }

      return {
        key: action.key,
        legacyValue: action.legacyValue,
        label: action.label,
        description: action.description ?? null,
        warning: action.warning ?? null,
        minParameterCount: action.minParameterCount,
        maxParameterCount: action.maxParameterCount ?? null,
        parameters: (action.parameters ?? []).map((parameter) => ({
          index: parameter.index,
          key: parameter.key,
          label: parameter.label,
          description: parameter.description ?? null,
          kind: parameter.kind,
          required: parameter.required ?? true,
          referenceTarget: parameter.referenceTarget ?? null,
          enumName: parameter.enumName ?? null,
          scale: parameter.scale ?? null,
          repeating: parameter.repeating ?? false,
          repeatStep:
            parameter.repeatStep ?? (parameter.repeating ? 1 : 0),
          allowsMultipleEnumValues: parameter.allowsMultipleEnumValues ?? false,
          ...(parameter.kind === "Reference"
            ? {
                executionProjection: parameterExecutionProjection(
                  category,
                  action,
                  parameter
                )
              }
            : {}),
          ...(parameter.defaultValue !== undefined
            ? { defaultValue: parameter.defaultValue }
            : {}),
          ...conversionMetadata(
            category,
            action.key,
            parameter.key,
            parameter.scale
          )
        })),
        semantic
      };
    })
    .sort((left, right) => left.legacyValue - right.legacyValue);
}

function applyRuntimeActionOverrides(actions, overrides) {
  const removed = new Set(overrides.removeActions);
  const byKey = new Map(
    actions
      .filter((action) => !removed.has(action.key))
      .map((action) => [action.key, action])
  );

  for (const action of overrides.addActions) {
    assert(
      !byKey.has(action.key),
      `Runtime action override ${action.key} already exists in the source schema.`
    );
    byKey.set(action.key, action);
  }

  for (const [key, patch] of Object.entries(overrides.patchActions)) {
    const action = byKey.get(key);
    assert(action, `Runtime action override references unknown action ${key}.`);
    byKey.set(key, {
      ...action,
      ...patch,
      parameters: patch.parameters ?? action.parameters
    });
  }

  return [...byKey.values()];
}

function applyRuntimePatches(actions, patches, category) {
  const byKey = new Map(actions.map((action) => [action.key, action]));
  for (const [key, patch] of Object.entries(patches)) {
    const action = byKey.get(key);
    assert(action, `Runtime ${category} override references unknown action ${key}.`);
    byKey.set(key, {
      ...action,
      ...patch,
      parameters: patch.parameters ?? action.parameters
    });
  }
  return [...byKey.values()];
}

const runtimeEffects = applyRuntimeActionOverrides(
  sourceSchema.effects,
  runtimeActionOverrides
);
const runtimeConditions = applyRuntimePatches(
  sourceSchema.conditions,
  runtimeActionOverrides.patchConditions,
  "condition"
);

const effects = decorateActions(
  runtimeEffects,
  overlay.effects,
  "effect"
);
const conditions = decorateActions(
  runtimeConditions,
  overlay.conditions,
  "condition"
);
const entityFields = foundation.entityFields.map((field) => ({
  ...field,
  ...entityConversionMetadata(field)
}));

for (const entry of valueConversionAudit.entries) {
  if (entry.category === "entity-field") {
    assert(
      entityFields.some((field) => field.path === entry.path),
      `Value conversion audit references unknown entity field ${entry.path}.`
    );
    continue;
  }

  const actions =
    entry.category === "condition" ? conditions : effects;
  const action = actions.find((candidate) => candidate.key === entry.actionKey);
  assert(
    action,
    `Value conversion audit references unknown ${entry.category} ${entry.actionKey}.`
  );
  assert(
    action.parameters.some(
      (parameter) =>
        parameter.key === entry.parameterKey &&
        parameter.scale === entry.scale
    ),
    `Value conversion audit references unknown parameter ${entry.actionKey}.${entry.parameterKey} or mismatched scale.`
  );
}

for (const rule of executionProjection.parameterRules) {
  const candidates =
    rule.category === "condition"
      ? conditions
      : rule.category === "effect"
        ? effects
        : [...effects, ...conditions];
  const matches = candidates.some(
    (action) =>
      (!rule.actionKey || rule.actionKey === action.key) &&
      action.parameters.some(
        (parameter) =>
          (!rule.parameterKey || rule.parameterKey === parameter.key) &&
          (!rule.referenceTarget ||
            rule.referenceTarget === parameter.referenceTarget)
      )
  );
  assert(
    matches,
    `Execution projection parameter rule does not match an action parameter: ${JSON.stringify(rule)}`
  );
}

const enumReferences = sorted(
  new Set([
    ...[...effects, ...conditions]
      .flatMap((action) => action.parameters)
      .map((parameter) => parameter.enumName)
      .filter(Boolean),
    ...entityFields
      .map((field) => field.enumName)
      .filter(Boolean)
  ])
);

const enumDefinitions = new Map(
  enumSnapshot.enums.map((item) => [item.name, item])
);
for (const enumName of enumReferences) {
  assert(
    enumDefinitions.has(enumName),
    `Enum snapshot is missing referenced enum ${enumName}.`
  );
}

function assertActionCoverage(actions, enumName, category) {
  const definition = enumDefinitions.get(enumName);
  assert(definition, `Enum snapshot is missing ${enumName}.`);
  const expected = definition.values.filter((item) => item.name !== "None");
  const expectedNames = new Set(expected.map((item) => item.name));
  const actualNames = new Set(actions.map((action) => action.key));
  const missing = [...expectedNames].filter((key) => !actualNames.has(key));
  const unknown = [...actualNames].filter((key) => !expectedNames.has(key));
  assert(
    missing.length === 0,
    `${category} is missing runtime actions: ${missing.join(", ")}.`
  );
  assert(
    unknown.length === 0,
    `${category} contains actions absent from ${enumName}: ${unknown.join(", ")}.`
  );

  const expectedValues = new Map(
    expected.map((item) => [item.name, item.value])
  );
  for (const action of actions) {
    assert(
      action.legacyValue === expectedValues.get(action.key),
      `${category}.${action.key} uses legacyValue ${action.legacyValue}, ` +
        `expected ${expectedValues.get(action.key)}.`
    );
  }
}

assertActionCoverage(effects, "EffectActionType", "effects");
assertActionCoverage(conditions, "EConditionType", "conditions");
const enums = enumReferences.map((name) => enumDefinitions.get(name));

const outputPath = "config/capability-registry.v0.json";
let existingRegistry = null;
try {
  existingRegistry = await readJson(outputPath);
} catch {
  existingRegistry = null;
}

const sourceHash = sha256(sourceText);
const sourceRevision =
  typeof args.revision === "string"
    ? args.revision
    : externalSources.sourceRevision ??
      (args.check && existingRegistry?.source?.sha256 === sourceHash
        ? existingRegistry?.source?.sourceRevision
        : undefined);

const registry = {
  schemaVersion: foundation.schemaVersion,
  registryVersion: foundation.registryVersion,
  source: {
    heroAuthoringSchemaVersion: sourceSchema.version,
    sha256: sourceHash,
    sourcePath: path.basename(sourcePath),
    ...(typeof sourceRevision === "string" ? { sourceRevision } : {})
  },
  entities: foundation.entities,
  planOperations: foundation.planOperations,
  readOnlyTools: foundation.readOnlyTools,
  readOnlyToolContracts: readOnlyToolContract,
  executionProjection: {
    defaultParameterProjection:
      executionProjection.defaultParameterProjection,
    defaultEdgeProjection: executionProjection.defaultEdgeProjection,
    parameterRules: executionProjection.parameterRules,
    edgeRules: executionProjection.edgeRules
  },
  nestedTypes: foundation.nestedTypes,
  fieldSemantics: foundation.fieldSemantics,
  valueSources: foundation.valueSources,
  similarityScoring: foundation.similarityScoring,
  skillGraphSchema,
  entityFields,
  intents: foundation.intents,
  effects,
  conditions,
  damageStages: sourceSchema.damageStages.map((stage) => ({
    key: stage.key,
    legacyValue: stage.legacyValue,
    label: stage.label
  })),
  enums,
  enumReferences
};

if (args.check) {
  if (!existingRegistry || JSON.stringify(existingRegistry) !== JSON.stringify(registry)) {
    console.error(
      `Capability registry is out of date with ${path.basename(sourcePath)}.`
    );
    console.error(`source sha256: ${registry.source.sha256}`);
    console.error(
      `run: npm run build:registry -- --schema "${sourcePath}" ` +
        `--enums "${args.enums ?? "config/enum-values.v0.json"}"` +
        (typeof sourceRevision === "string"
          ? ` --revision "${sourceRevision}"`
          : " --revision <source-revision>")
    );
    process.exit(1);
  }

  console.log(
    `verified ${outputPath} against ${path.basename(sourcePath)} ` +
      `(${effects.length} effects, ${conditions.length} conditions).`
  );
} else {
  await writeJson(outputPath, registry);
  console.log(
    `Generated ${outputPath} from ${path.basename(sourcePath)} ` +
      `(${effects.length} effects, ${conditions.length} conditions).`
  );
}
