#!/usr/bin/env node

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  ".."
);

async function readJson(relativePath) {
  return JSON.parse(
    await readFile(path.join(repoRoot, relativePath), "utf8")
  );
}

function parseArgs(argv) {
  const options = { planPath: process.env.MODIFY_PLAN_PATH ?? "" };
  for (let index = 0; index < argv.length; index++) {
    if (argv[index] === "--plan") {
      options.planPath = argv[index + 1] ?? "";
      index++;
    }
  }
  return options;
}

function unitMatches(unit, aliases, candidate) {
  const normalizedUnit = unit.trim().toLowerCase();
  const normalizedCandidate = candidate.trim().toLowerCase();
  return (
    normalizedUnit === normalizedCandidate ||
    aliases.some(
      (alias) => alias.trim().toLowerCase() === normalizedCandidate
    )
  );
}

function conversionFactor(defaults, inputUnit, outputUnit) {
  const rule = defaults.conversionRules.find(
    (item) =>
      unitMatches(item.inputUnit, item.inputAliases, inputUnit) &&
      unitMatches(item.outputUnit, item.outputAliases, outputUnit)
  );
  assert(rule, `No conversion rule from ${inputUnit} to ${outputUnit}.`);
  return rule.factor;
}

const options = parseArgs(process.argv.slice(2));
const goldenCases = await readJson("evals/golden-cases.v0.json");
const targetCase = goldenCases.cases.find((item) => item.id === "p0-012");
assert(targetCase, "Golden case p0-012 is missing.");
assert.equal(targetCase.request, "把冷却改成8秒");

const planPath =
  options.planPath || "examples/modify-skill-cooldown.plan.json";
const plan = JSON.parse(
  await readFile(
    path.isAbsolute(planPath) ? planPath : path.join(repoRoot, planPath),
    "utf8"
  )
);
const registry = await readJson("config/capability-registry.v0.json");
const defaults = await readJson("config/default-value-contract.v0.json");
const field = registry.entityFields.find(
  (item) => item.path === "Skill.cd_time"
);
assert(field, "Skill.cd_time is missing from the registry.");
assert.equal(field.semanticName, "cooldown");
assert.equal(field.unit, "ms");
assert.equal(field.scale, 1);

assert.equal(plan.status, "Ready");
assert.equal(plan.operations.length, 1);
const operation = plan.operations[0];
assert.equal(operation.kind, "ModifySkill");
assert.equal(operation.skill.binding, "Existing");
assert.equal(operation.skill.namespace, "TbSkill");
const cooldown = operation.fields.cooldown;
assert(cooldown, "ModifySkill.fields.cooldown is missing.");
assert.equal(cooldown.value, 8);
const factor = conversionFactor(defaults, cooldown.unit, field.unit);
const workbookCellValue = cooldown.value * factor * field.scale;
assert.equal(workbookCellValue, 8000);

console.log(
  JSON.stringify(
    {
      status: "passed",
      caseId: targetCase.id,
      planSource: options.planPath ? "provided-plan" : "accepted-fixture",
      semanticField: field.semanticName,
      semanticValue: cooldown.value,
      semanticUnit: cooldown.unit,
      workbookField: field.path.split(".")[1],
      workbookUnit: field.unit,
      workbookCellValue,
      conversionRule: `${cooldown.unit}->${field.unit}`
    },
    null,
    2
  )
);
