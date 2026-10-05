import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { normalizePlan, semanticEquals } from "../src/semantic-ir.mjs";
import {
  evaluateAssertions,
  evaluateForbiddenAssertions
} from "../src/semantic-assertions.mjs";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

async function readPlan(name) {
  return JSON.parse(
    await readFile(path.join(repoRoot, "examples", name), "utf8")
  );
}

test("semantically equivalent periodic damage plans normalize equally", async () => {
  const left = await readPlan("periodic-damage.plan.json");
  const right = await readPlan("periodic-damage-reordered.plan.json");

  assert.equal(semanticEquals(left, right), true);
  assert.deepEqual(normalizePlan(left), normalizePlan(right));
});

test("instant damage is not equivalent to periodic damage", async () => {
  const periodic = await readPlan("periodic-damage.plan.json");
  const wrong = await readPlan("periodic-damage-wrong.plan.json");

  assert.equal(semanticEquals(periodic, wrong), false);
});

test("semantic assertions accept the intended periodic damage plan", async () => {
  const plan = await readPlan("periodic-damage.plan.json");
  const assertions = [
    { kind: "status", equals: "Ready" },
    { kind: "hasIntent", intent: "PeriodicDamage" },
    {
      kind: "paramEquals",
      path: "AddEffectIntent.PeriodicDamage.durationMs",
      equals: 3000
    },
    {
      kind: "paramEquals",
      path: "AddEffectIntent.PeriodicDamage.intervalMs",
      equals: 1000
    }
  ];

  assert.deepEqual(evaluateAssertions(plan, assertions), []);
  assert.deepEqual(
    evaluateForbiddenAssertions(plan, [
      { kind: "hasIntent", intent: "InstantDamage" }
    ]),
    []
  );
});

test("semantic assertions reject the wrong periodic damage plan", async () => {
  const wrong = await readPlan("periodic-damage-wrong.plan.json");

  assert.deepEqual(
    evaluateAssertions(wrong, [
      { kind: "hasIntent", intent: "PeriodicDamage" }
    ]),
    [{ kind: "hasIntent", intent: "PeriodicDamage" }]
  );
  assert.deepEqual(
    evaluateForbiddenAssertions(wrong, [
      { kind: "hasIntent", intent: "InstantDamage" }
    ]),
    [{ kind: "hasIntent", intent: "InstantDamage" }]
  );
});

