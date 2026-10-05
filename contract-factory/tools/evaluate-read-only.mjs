#!/usr/bin/env node

import { createSkillGraphIndex } from "../src/skill-graph-index.mjs";
import { evaluateReadOnlyCase } from "../src/read-only-eval.mjs";
import { validateSchema } from "../src/schema-lite.mjs";
import { readJson, repoRoot } from "./lib.mjs";

const caseSet = await readJson("evals/read-only-cases.v0.json");
const toolContract = await readJson("config/read-only-tools.v0.json");
const resultSchema = await readJson(toolContract.resultSchema);
const metricsDefinition = await readJson("evals/read-only-metrics.v0.json");
const graph = await readJson(caseSet.graph);
const registry = await readJson("config/capability-registry.v0.json");
const index = createSkillGraphIndex(graph, registry);
const graphBefore = JSON.stringify(graph);
const toolDefinitions = new Map(
  toolContract.tools.map((tool) => [tool.name, tool])
);

const evaluations = [];
for (const testCase of caseSet.cases) {
  const evaluation = evaluateReadOnlyCase(index, testCase);
  const definition = toolDefinitions.get(testCase.tool);
  const inputErrors = validateSchema(
    definition.inputSchema,
    testCase.input ?? {}
  );
  const definitionName = definition.outputSchema.$ref.split("#/$defs/")[1];
  const outputErrors = validateSchema(
    { $ref: `#/$defs/${definitionName}` },
    evaluation.result,
    resultSchema
  );
  const inputFailure =
    testCase.expectedInputContractError
      ? inputErrors.length === 0
      : inputErrors.length > 0;

  evaluations.push({
    ...evaluation,
    inputFailures: inputFailure ? inputErrors : [],
    outputFailures: outputErrors,
    passed:
      evaluation.failures.length === 0 &&
      !inputFailure &&
      outputErrors.length === 0
  });
}

if (JSON.stringify(graph) !== graphBefore) {
  console.error("Read-only evaluation mutated the source graph.");
  process.exit(1);
}

const failed = evaluations.filter((evaluation) => !evaluation.passed);
if (failed.length > 0) {
  console.error(`Read-only evaluations failed in ${repoRoot}:`);
  for (const failure of failed) {
    console.error(
      `${failure.caseId} (${failure.tool}) failed: ` +
        JSON.stringify({
          assertions: failure.failures,
          input: failure.inputFailures,
          output: failure.outputFailures
        })
    );
  }
  process.exit(1);
}

function resolvePath(value, path) {
  const parts = path
    .split(".")
    .flatMap((part) => {
      const match = /^([^[\]]+)(?:\[(\d+)\])?$/.exec(part);
      return match?.[2] === undefined
        ? [match?.[1] ?? part]
        : [match[1], Number.parseInt(match[2], 10)];
    });
  let current = value;
  for (const part of parts) {
    if (
      current === null ||
      current === undefined ||
      !Object.hasOwn(current, part)
    ) {
      return undefined;
    }
    current = current[part];
  }
  return current;
}

const explanationPoints = caseSet.cases.flatMap((testCase) =>
  (testCase.explanationPoints ?? []).map((point) => ({
    caseId: testCase.id,
    point
  }))
);
const matchedExplanationPoints = explanationPoints.filter(({ caseId, point }) => {
  const evaluation = evaluations.find((item) => item.caseId === caseId);
  const values = resolvePath(evaluation?.result, point.path);
  return (
    Array.isArray(values) &&
    values.some((item) => item?.[point.field] === point.value)
  );
});
const evidenceSteps = evaluations.flatMap((evaluation) =>
  Array.isArray(evaluation.result.steps) ? evaluation.result.steps : []
);
const missingEvidenceSteps = evidenceSteps.filter(
  (step) => step.evidence === undefined || step.evidence === null
);
const falseConclusionWithoutEvidence = evaluations.filter(
  (evaluation) =>
    ["Ready", "Partial"].includes(evaluation.result.status) &&
    Array.isArray(evaluation.result.steps) &&
    evaluation.result.steps.some((step) => step.evidence === undefined)
).length;

const metrics = {
  deterministicCasePassRate:
    evaluations.filter((evaluation) => evaluation.passed).length /
    evaluations.length,
  deterministicExplanationPointRecall:
    explanationPoints.length === 0
      ? 1
      : matchedExplanationPoints.length / explanationPoints.length,
  stepEvidencePresenceRate:
    evidenceSteps.length === 0
      ? 1
      : (evidenceSteps.length - missingEvidenceSteps.length) /
        evidenceSteps.length,
  capabilityNoMatchHonestyRate:
    evaluations.filter(
      (evaluation) =>
        caseSet.cases.find((item) => item.id === evaluation.caseId)?.metricTags?.includes(
          "capability-no-match"
        )
    ).length === 0
      ? 1
      : evaluations.filter(
          (evaluation) =>
            caseSet.cases
              .find((item) => item.id === evaluation.caseId)
              ?.metricTags?.includes("capability-no-match") && evaluation.passed
        ).length /
        evaluations.filter(
          (evaluation) =>
            caseSet.cases
              .find((item) => item.id === evaluation.caseId)
              ?.metricTags?.includes("capability-no-match")
        ).length,
  falseConclusionWithoutEvidence
};

const report = {
  repository: repoRoot,
  graph: caseSet.graph,
  metricsVersion: metricsDefinition.metricsVersion,
  cases: evaluations.length,
  passed: evaluations.filter((evaluation) => evaluation.passed).length,
  metrics,
  modelEvaluation: metricsDefinition.modelEvaluation
};

console.log(JSON.stringify(report, null, 2));
