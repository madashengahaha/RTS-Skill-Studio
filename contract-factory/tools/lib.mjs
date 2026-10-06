import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  ".."
);

export async function readJson(relativeOrAbsolutePath) {
  const fullPath = path.isAbsolute(relativeOrAbsolutePath)
    ? relativeOrAbsolutePath
    : path.join(repoRoot, relativeOrAbsolutePath);
  return JSON.parse(await readFile(fullPath, "utf8"));
}

export async function writeJson(relativeOrAbsolutePath, value) {
  const fullPath = path.isAbsolute(relativeOrAbsolutePath)
    ? relativeOrAbsolutePath
    : path.join(repoRoot, relativeOrAbsolutePath);
  await writeFile(fullPath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

export async function externalSourcePath(name) {
  const sources = await readJson("config/external-sources.v0.json");
  const relativePath = sources.paths?.[name];
  assert(
    typeof relativePath === "string" && relativePath.length > 0,
    `External source ${name} is not configured.`
  );
  const root =
    process.env[sources.projectRootEnvironmentVariable] ??
    sources.defaultProjectRoot;
  assert(root, "External project root is not configured.");
  return path.resolve(root, relativePath);
}

export function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

export function parseArgs(argv) {
  const args = {};
  const positionals = [];
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith("--")) {
      positionals.push(token);
      continue;
    }
    const key = token.slice(2);
    const next = argv[index + 1];
    if (next === undefined || next.startsWith("--")) {
      args[key] = true;
    } else {
      args[key] = next;
      index += 1;
    }
  }
  if (positionals.length > 0) {
    args._ = positionals;
  }
  return args;
}

export function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

export function duplicateValues(values) {
  const seen = new Set();
  const duplicates = new Set();
  for (const value of values) {
    if (seen.has(value)) {
      duplicates.add(value);
    }
    seen.add(value);
  }
  return [...duplicates];
}

export function sorted(values) {
  return [...values].sort((left, right) =>
    String(left).localeCompare(String(right), "en")
  );
}
