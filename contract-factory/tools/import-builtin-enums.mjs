#!/usr/bin/env node

import { readFile } from "node:fs/promises";
import path from "node:path";
import {
  assert,
  externalSourcePath,
  parseArgs,
  readJson,
  sha256,
  writeJson
} from "./lib.mjs";

const args = parseArgs(process.argv.slice(2));
const builtinPath =
  typeof args.builtin === "string"
    ? path.resolve(args.builtin)
    : await externalSourcePath("builtinXml");
const generatedConfigDir =
  typeof args["generated-config-dir"] === "string"
    ? path.resolve(args["generated-config-dir"])
    : await externalSourcePath("unityGeneratedConfig");
const overlayPath = args.overlay ?? "config/enum-overlays.v0.json";
const overlay = await readJson(overlayPath);
const runtimeOverrides = await readJson(
  "config/runtime-action-overrides.v0.json"
);
const externalSources = await readJson("config/external-sources.v0.json");

assert(Array.isArray(overlay.enums), "Enum overlay must contain enums.");
assert(
  Array.isArray(externalSources.unityGeneratedEnums) &&
    externalSources.unityGeneratedEnums.length > 0,
  "External sources must list unityGeneratedEnums."
);

function parseAttributes(source) {
  const attributes = {};
  const pattern = /([A-Za-z_:][A-Za-z0-9_.:-]*)\s*=\s*"([^"]*)"/g;
  for (const match of source.matchAll(pattern)) {
    attributes[match[1]] = match[2];
  }
  return attributes;
}

function parseBuiltinEnums(sourceText) {
  const definitions = new Map();
  const enumPattern = /<enum\b([^>]*)>([\s\S]*?)<\/enum>/g;
  for (const enumMatch of sourceText.matchAll(enumPattern)) {
    const enumAttributes = parseAttributes(enumMatch[1]);
    assert(enumAttributes.name, "Every XML enum must have a name.");

    const values = [];
    const valueNames = new Set();
    const valuePattern = /<var\b([^>]*?)(?:\/>|>\s*<\/var>)/g;
    for (const valueMatch of enumMatch[2].matchAll(valuePattern)) {
      const attributes = parseAttributes(valueMatch[1]);
      assert(
        attributes.name,
        `Enum ${enumAttributes.name} contains a var without a name.`
      );
      assert(
        attributes.value !== undefined && /^-?\d+$/.test(attributes.value),
        `Enum ${enumAttributes.name}.${attributes.name} must have an integer value.`
      );
      assert(
        !valueNames.has(attributes.name),
        `Enum ${enumAttributes.name} contains duplicate var ${attributes.name}.`
      );
      valueNames.add(attributes.name);
      values.push({
        name: attributes.name,
        value: Number.parseInt(attributes.value, 10),
        alias: attributes.alias ?? "",
        comment: attributes.comment ?? null
      });
    }

    assert(values.length > 0, `Enum ${enumAttributes.name} contains no values.`);
    definitions.set(enumAttributes.name, {
      name: enumAttributes.name,
      flags: /^true$/i.test(enumAttributes.flags ?? ""),
      comment: enumAttributes.comment ?? null,
      sourceKind: "BuiltinXml",
      values
    });
  }
  return definitions;
}

function stripComments(sourceText) {
  return sourceText
    .replace(/\/\*[\s\S]*?\*\//g, "")
    .replace(/\/\/.*$/gm, "");
}

function parseGeneratedEnums(sourceText) {
  const definitions = new Map();
  const enumPattern = /\benum\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{([\s\S]*?)\}/g;
  for (const enumMatch of sourceText.matchAll(enumPattern)) {
    const name = enumMatch[1];
    const body = stripComments(enumMatch[2]);
    const values = [];
    const valueNames = new Set();
    for (const item of body.split(",")) {
      const match = item.trim().match(/^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(-?\d+)$/);
      if (!match) continue;
      assert(
        !valueNames.has(match[1]),
        `Enum ${name} contains duplicate member ${match[1]}.`
      );
      valueNames.add(match[1]);
      values.push({
        name: match[1],
        value: Number.parseInt(match[2], 10),
        alias: "",
        comment: null
      });
    }
    assert(values.length > 0, `Generated enum ${name} contains no values.`);
    const prefix = sourceText.slice(Math.max(0, enumMatch.index - 200), enumMatch.index);
    definitions.set(name, {
      name,
      flags: /\[Flags\][\s\S]*$/.test(prefix),
      comment: null,
      sourceKind: "UnityGeneratedEnum",
      values
    });
  }
  return definitions;
}

function assertSameEnumMembers(left, right, label) {
  const leftByName = new Map(left.values.map((value) => [value.name, value]));
  const rightByName = new Map(right.values.map((value) => [value.name, value]));
  for (const [name, value] of leftByName) {
    assert(
      rightByName.has(name),
      `${label} is missing member ${name} from the generated Unity enum.`
    );
    assert(
      rightByName.get(name).value === value.value,
      `${label}.${name} has value ${value.value}; generated Unity enum has ` +
        `${rightByName.get(name).value}.`
    );
  }
  for (const name of rightByName.keys()) {
    assert(
      leftByName.has(name),
      `${label} contains generated member ${name}, which is absent from the source.`
    );
  }
}

function mergeOverlayValue(baseValue, overlayValue) {
  return {
    ...baseValue,
    alias: overlayValue.alias ?? baseValue.alias,
    comment: overlayValue.comment ?? baseValue.comment
  };
}

function applyOverlay(base, overlayValue) {
  if (!base) {
    return {
      name: overlayValue.name,
      flags: overlayValue.flags ?? false,
      comment: overlayValue.comment ?? null,
      sourceKind: "ParameterConvention",
      values: overlayValue.values
    };
  }

  const overlayByName = new Map(
    overlayValue.values.map((value) => [value.name, value])
  );
  const values = base.values.map((value) =>
    overlayByName.has(value.name)
      ? mergeOverlayValue(value, overlayByName.get(value.name))
      : value
  );
  for (const [name, value] of overlayByName) {
    const existing = base.values.find((candidate) => candidate.name === name);
    assert(
      existing && existing.value === value.value,
      `Overlay enum ${overlayValue.name}.${name} conflicts with the runtime definition.`
    );
  }
  return {
    ...base,
    comment: overlayValue.comment ?? base.comment,
    values
  };
}

const builtinText = await readFile(builtinPath, "utf8");
const builtinEnums = parseBuiltinEnums(builtinText);
const generatedSourceFiles = [];
const generatedEnums = new Map();
for (const fileName of externalSources.unityGeneratedEnums) {
  const filePath = path.join(generatedConfigDir, fileName);
  const sourceText = await readFile(filePath, "utf8");
  generatedSourceFiles.push({
    fileName,
    sha256: sha256(sourceText)
  });
  for (const definition of parseGeneratedEnums(sourceText).values()) {
    assert(
      !generatedEnums.has(definition.name),
      `Generated enum ${definition.name} is declared more than once.`
    );
    generatedEnums.set(definition.name, definition);
  }
}

const declaredRuntimeEnum = runtimeOverrides.source?.generatedEnum;
assert(
  typeof declaredRuntimeEnum === "string" &&
    declaredRuntimeEnum.length > 0,
  "Runtime action overrides must declare source.generatedEnum."
);
assert(
  generatedSourceFiles.some(
    (file) => file.fileName === path.basename(declaredRuntimeEnum)
  ),
  `Runtime action override ${declaredRuntimeEnum} is not in unityGeneratedEnums.`
);

const enumMap = new Map(builtinEnums);
for (const generated of generatedEnums.values()) {
  const builtin = enumMap.get(generated.name);
  if (builtin) {
    assertSameEnumMembers(builtin, generated, builtin.name);
  }
  enumMap.set(generated.name, {
    ...generated,
    comment: builtin?.comment ?? generated.comment,
    flags: builtin?.flags ?? generated.flags
  });
}

for (const overlayEnum of overlay.enums) {
  assert(overlayEnum.name, "Every overlay enum must have a name.");
  assert(
    Array.isArray(overlayEnum.values),
    `Overlay enum ${overlayEnum.name} has no values.`
  );
  enumMap.set(
    overlayEnum.name,
    applyOverlay(enumMap.get(overlayEnum.name), overlayEnum)
  );
}

const enums = [...enumMap.values()].sort((left, right) =>
  left.name.localeCompare(right.name, "en")
);
assert(enums.length > 0, "No enums were found.");

const outputPath = "config/enum-values.v0.json";
let existing = null;
if (args.check) {
  existing = await readJson(outputPath);
}

const sourceHash = sha256(builtinText);
const revision =
  typeof args.revision === "string"
    ? args.revision
    : externalSources.sourceRevision ??
      (args.check &&
        existing?.source?.sha256 === sourceHash &&
        JSON.stringify(existing?.source?.unityGenerated?.files) ===
          JSON.stringify(generatedSourceFiles)
        ? existing?.source?.revision
        : undefined);

const snapshot = {
  schemaVersion: 0,
  snapshotVersion: "0.1.0",
  source: {
    fileName: path.basename(builtinPath),
    sha256: sourceHash,
    overlayFileName: path.basename(overlayPath),
    overlaySha256: sha256(JSON.stringify(overlay)),
    unityGenerated: {
      declaredRuntimeEnum,
      files: generatedSourceFiles
    },
    ...(typeof revision === "string" ? { revision } : {})
  },
  enums
};

if (args.check) {
  if (JSON.stringify(existing) !== JSON.stringify(snapshot)) {
    console.error(
      `Enum snapshot is out of date with ${path.basename(builtinPath)} or ` +
        "the generated Unity config enums."
    );
    console.error(
      "run: npm run build:enums -- " +
        `--builtin "${builtinPath}" ` +
        `--generated-config-dir "${generatedConfigDir}"`
    );
    process.exit(1);
  }
  console.log(
    `verified ${outputPath} against ${path.basename(builtinPath)} and ` +
      `${generatedSourceFiles.length} generated Unity enum files ` +
      `(${enums.length} enums).`
  );
} else {
  await writeJson(outputPath, snapshot);
  console.log(
    `Generated ${outputPath} with ${enums.length} enums and ` +
      `${enums.reduce((total, item) => total + item.values.length, 0)} values.`
  );
}
