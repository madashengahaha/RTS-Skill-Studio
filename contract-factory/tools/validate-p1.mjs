#!/usr/bin/env node

import { createSkillGraphIndex } from "../src/skill-graph-index.mjs";
import { evaluateReadOnlyCase } from "../src/read-only-eval.mjs";
import { validateSchema } from "../src/schema-lite.mjs";
import { assert, duplicateValues, readJson, repoRoot } from "./lib.mjs";

const requiredFiles = [
  "contracts/skill-graph.schema.json",
  "contracts/read-only-results.schema.json",
  "config/read-only-tools.v0.json",
  "evals/read-only-cases.v0.json",
  "evals/read-only-metrics.v0.json"
];

const loaded = {};
for (const file of requiredFiles) {
  loaded[file] = await readJson(file);
}

const registry = await readJson("config/capability-registry.v0.json");
const toolContract = loaded["config/read-only-tools.v0.json"];
const caseSet = loaded["evals/read-only-cases.v0.json"];
const schema = loaded["contracts/skill-graph.schema.json"];
const resultSchema = loaded["contracts/read-only-results.schema.json"];
const metrics = loaded["evals/read-only-metrics.v0.json"];
const graph = await readJson(caseSet.graph);

assert(
  schema.$schema === "https://json-schema.org/draft/2020-12/schema" &&
    schema.type === "object",
  "Skill graph contract must be a JSON Schema object."
);
assert(
  toolContract.policy.mutatesData === false &&
    toolContract.policy.allowsWrites === false,
  "Read-only tool contract must forbid writes."
);

const contractNames = toolContract.tools.map((tool) => tool.name);
assert(
  duplicateValues(contractNames).length === 0,
  "Read-only tool names must be unique."
);
assert(
  [...contractNames].sort().join("|") ===
    [...registry.readOnlyTools].sort().join("|"),
  "Read-only tool contract must exactly match the registry tool list."
);
assert(
  toolContract.resultSchema === "contracts/read-only-results.schema.json",
  "Read-only contract must point to its machine-readable result schema."
);
assert(
  JSON.stringify(registry.readOnlyToolContracts) === JSON.stringify(toolContract),
  "Registry read-only contracts must match config/read-only-tools.v0.json."
);
assert(
  JSON.stringify(registry.skillGraphSchema) === JSON.stringify(schema),
  "Registry graph schema must match contracts/skill-graph.schema.json."
);
assert(
  metrics.modelEvaluation?.status === "NotRun",
  "P1 must explicitly mark model evaluation as NotRun."
);

const toolDefinitions = new Map(
  toolContract.tools.map((tool) => [tool.name, tool])
);
for (const tool of toolContract.tools) {
  assert(
    tool.inputSchema?.type === "object",
    `${tool.name} must define an object inputSchema.`
  );
  const reference = tool.outputSchema?.$ref;
  assert(
    typeof reference === "string" &&
      reference.startsWith("read-only-results.schema.json#/$defs/"),
    `${tool.name} must define a result schema reference.`
  );
  const definitionName = reference.split("#/$defs/")[1];
  assert(
    resultSchema.$defs?.[definitionName],
    `${tool.name} references missing result definition ${definitionName}.`
  );
}

const index = createSkillGraphIndex(graph, registry);
assert(index.nodeCount === graph.nodes.length, "Graph index lost nodes.");
assert(index.edgeCount === graph.edges.length, "Graph index lost edges.");

const graphBefore = JSON.stringify(graph);
const results = caseSet.cases.map((testCase) =>
  evaluateReadOnlyCase(index, testCase)
);
assert(
  JSON.stringify(graph) === graphBefore,
  "Read-only case evaluation mutated the source graph."
);
const failed = results.filter((result) => result.failures.length > 0);
assert(
  failed.length === 0,
  `Read-only case failures: ${failed.map((item) => item.caseId).join(", ")}`
);

for (const result of results) {
  assert(result.result.readOnly === true, `${result.caseId} is not marked read-only.`);
  const testCase = caseSet.cases.find((item) => item.id === result.caseId);
  const definition = toolDefinitions.get(testCase.tool);
  const inputErrors = validateSchema(
    definition.inputSchema,
    testCase.input ?? {}
  );
  if (testCase.expectedInputContractError) {
    assert(
      inputErrors.length > 0,
      `${result.caseId} was expected to violate its input contract.`
    );
  } else {
    assert(
      inputErrors.length === 0,
      `${result.caseId} input violates its tool contract: ${inputErrors.join("; ")}`
    );
  }
  const definitionName = definition.outputSchema.$ref.split("#/$defs/")[1];
  const outputErrors = validateSchema(
    { $ref: `#/$defs/${definitionName}` },
    result.result,
    resultSchema
  );
  assert(
    outputErrors.length === 0,
    `${result.caseId} output violates its tool contract: ${outputErrors.join("; ")}`
  );
}

console.log(
  `validated ${contractNames.length} read-only tools and ${caseSet.cases.length} cases`
);
console.log(`repository: ${repoRoot}`);
