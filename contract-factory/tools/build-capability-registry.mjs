#!/usr/bin/env node

import { readFile } from "node:fs/promises";
import path from "node:path";
import {
  assert,
  duplicateValues,
  parseArgs,
  readJson,
  repoRoot,
  sha256,
  sorted,
  writeJson
} from "./lib.mjs";

const args = parseArgs(process.argv.slice(2));
if (!args.schema || typeof args.schema !== "string") {
  console.error(
    "Usage: npm run build:registry -- --schema /path/to/hero-authoring-schema.json " +
      "[--enums config/enum-values.v0.json] [--revision source-revision]"
  );
  process.exit(2);
}

const sourcePath = path.resolve(args.schema);
const sourceText = await readFile(sourcePath, "utf8");
const sourceSchema = JSON.parse(sourceText);
const foundation = await readJson("config/registry-foundation.v0.json");
const overlay = await readJson("config/semantic-overlay.v0.json");
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
          repeatStep: parameter.repeatStep ?? 0,
          allowsMultipleEnumValues: parameter.allowsMultipleEnumValues ?? false
        })),
        semantic
      };
    })
    .sort((left, right) => left.legacyValue - right.legacyValue);
}

const effects = decorateActions(
  sourceSchema.effects,
  overlay.effects,
  "effects"
);
const conditions = decorateActions(
  sourceSchema.conditions,
  overlay.conditions,
  "conditions"
);
const enumReferences = sorted(
  new Set([
    ...[...effects, ...conditions]
      .flatMap((action) => action.parameters)
      .map((parameter) => parameter.enumName)
      .filter(Boolean),
    ...foundation.entityFields.map((field) => field.enumName)
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
    : existingRegistry?.source?.sha256 === sourceHash
      ? existingRegistry?.source?.sourceRevision
      : undefined;

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
  valueSources: foundation.valueSources,
  similarityScoring: foundation.similarityScoring,
  skillGraphSchema,
  entityFields: foundation.entityFields,
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
