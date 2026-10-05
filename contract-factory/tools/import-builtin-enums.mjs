#!/usr/bin/env node

import { readFile } from "node:fs/promises";
import path from "node:path";
import { assert, parseArgs, readJson, sha256, writeJson } from "./lib.mjs";

const args = parseArgs(process.argv.slice(2));
if (!args.builtin || typeof args.builtin !== "string") {
  console.error(
    "Usage: npm run build:enums -- --builtin /path/to/builtin.xml " +
      "[--overlay config/enum-overlays.v0.json] [--revision revision] [--check]"
  );
  process.exit(2);
}

const sourcePath = path.resolve(args.builtin);
const sourceText = await readFile(sourcePath, "utf8");
const overlay = await readJson(
  args.overlay ?? "config/enum-overlays.v0.json"
);
assert(Array.isArray(overlay.enums), "Enum overlay must contain enums.");

function parseAttributes(source) {
  const attributes = {};
  const pattern = /([A-Za-z_:][A-Za-z0-9_.:-]*)\s*=\s*"([^"]*)"/g;
  for (const match of source.matchAll(pattern)) {
    attributes[match[1]] = match[2];
  }
  return attributes;
}

const enumMap = new Map();
const overlayNames = new Set();
for (const definition of overlay.enums) {
  assert(definition.name, "Every overlay enum must have a name.");
  assert(Array.isArray(definition.values), `Overlay enum ${definition.name} has no values.`);
  enumMap.set(definition.name, {
    name: definition.name,
    flags: definition.flags ?? false,
    comment: definition.comment ?? null,
    values: definition.values
  });
  overlayNames.add(definition.name);
}

const enumPattern = /<enum\b([^>]*)>([\s\S]*?)<\/enum>/g;
for (const enumMatch of sourceText.matchAll(enumPattern)) {
  const enumAttributes = parseAttributes(enumMatch[1]);
  assert(enumAttributes.name, "Every enum must have a name.");

  const values = [];
  const valueNames = new Set();
  const valuePattern = /<var\b([^>]*?)(?:\/>|>\s*<\/var>)/g;
  for (const valueMatch of enumMatch[2].matchAll(valuePattern)) {
    const attributes = parseAttributes(valueMatch[1]);
    assert(attributes.name, `Enum ${enumAttributes.name} contains a var without a name.`);
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
  const xmlDefinition = {
    name: enumAttributes.name,
    flags: /^true$/i.test(enumAttributes.flags ?? ""),
    comment: enumAttributes.comment ?? null,
    values
  };
  if (overlayNames.has(enumAttributes.name)) {
    assert(
      JSON.stringify(enumMap.get(enumAttributes.name)) ===
        JSON.stringify(xmlDefinition),
      `XML enum ${enumAttributes.name} conflicts with its runtime overlay.`
    );
  } else {
    enumMap.set(enumAttributes.name, xmlDefinition);
  }
}

const enums = [...enumMap.values()];
assert(enums.length > 0, "No enums were found in builtin.xml.");
enums.sort((left, right) => left.name.localeCompare(right.name, "en"));

const snapshot = {
  schemaVersion: 0,
  snapshotVersion: "0.1.0",
  source: {
    fileName: path.basename(sourcePath),
    sha256: sha256(sourceText),
    overlayFileName: path.basename(args.overlay ?? "config/enum-overlays.v0.json"),
    overlaySha256: sha256(JSON.stringify(overlay)),
    ...(typeof args.revision === "string" ? { revision: args.revision } : {})
  },
  enums
};

const outputPath = "config/enum-values.v0.json";
if (args.check) {
  const existing = await readJson(outputPath);
  if (JSON.stringify(existing) !== JSON.stringify(snapshot)) {
    console.error(`Enum snapshot is out of date with ${path.basename(sourcePath)}.`);
    console.error(`run: npm run build:enums -- --builtin "${sourcePath}"`);
    process.exit(1);
  }
  console.log(
    `verified ${outputPath} against ${path.basename(sourcePath)} ` +
      `(${enums.length} enums).`
  );
} else {
  await writeJson(outputPath, snapshot);
  console.log(
    `Generated ${outputPath} with ${enums.length} enums and ` +
      `${enums.reduce((total, item) => total + item.values.length, 0)} values.`
  );
}
