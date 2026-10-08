#!/usr/bin/env node

import { createHash } from "node:crypto";
import {
  copyFile,
  mkdir,
  readFile,
  writeFile
} from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  ".."
);
const sourceRoot = path.resolve(
  process.env.RTS_SKILL_CONTRACT_FACTORY_ROOT ??
    process.env.RTS_SKILL_AGENT_ROOT ??
    path.join(repoRoot, "contract-factory")
);
const destinationRoot = path.join(
  repoRoot,
  "contracts",
  "rts-skill-agent"
);

const files = [
  "contracts/workbook-patch.schema.json",
  "contracts/workbook-patch-validation.schema.json",
  "contracts/capability-registry.schema.json",
  "contracts/read-only-results.schema.json",
  "contracts/skill-config-plan.schema.json",
  "contracts/skill-graph.schema.json",
  "config/capability-registry.v0.json",
  "config/default-mechanism-contract.v0.json",
  "config/default-value-contract.v0.json",
  "config/enum-overlays.v0.json",
  "config/enum-values.v0.json",
  "config/read-only-tools.v0.json",
  "config/registry-foundation.v0.json",
  "config/semantic-overlay.v0.json",
  "config/value-conversion-audit.v0.json",
  "config/workbook-patch-errors.v0.json",
  "evals/equivalence-rules.v0.json",
  "evals/golden-cases.schema.json",
  "evals/golden-cases.v0.json",
  "evals/read-only-cases.v0.json",
  "evals/read-only-metrics.v0.json",
  "examples/sample-skill-graph.json"
];

function sha256(buffer) {
  return createHash("sha256").update(buffer).digest("hex");
}

const manifest = {
  sourceRoot: path.relative(repoRoot, sourceRoot).split(path.sep).join("/"),
  files: []
};

for (const relativePath of files) {
  const source = path.join(sourceRoot, relativePath);
  const destination = path.join(destinationRoot, relativePath);
  const contents = await readFile(source);
  await mkdir(path.dirname(destination), { recursive: true });
  await copyFile(source, destination);
  manifest.files.push({
    path: relativePath,
    sha256: sha256(contents)
  });
}

manifest.files.sort((left, right) => left.path.localeCompare(right.path, "en"));
manifest.combinedSha256 = sha256(
  Buffer.from(
    manifest.files
      .map((file) => `${file.path}:${file.sha256}`)
      .join("\n"),
    "utf8"
  )
);
await writeFile(
  path.join(repoRoot, "contracts", "MANIFEST.json"),
  `${JSON.stringify(manifest, null, 2)}\n`,
  "utf8"
);

console.log(
  `synced ${manifest.files.length} contract files from ${sourceRoot}`
);
