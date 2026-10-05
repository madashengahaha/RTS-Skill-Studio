import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createSkillGraphIndex } from "../src/skill-graph-index.mjs";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

async function fixture(name) {
  return JSON.parse(await readFile(path.join(repoRoot, name), "utf8"));
}

async function createIndex() {
  const graph = await fixture("examples/sample-skill-graph.json");
  const registry = await fixture("config/capability-registry.v0.json");
  return {
    graph,
    registry,
    index: createSkillGraphIndex(graph, registry)
  };
}

function diamondGraph() {
  const node = (key, kind) => ({
    key,
    namespace: key.split(":")[0],
    kind,
    label: key,
    attributes: {},
    semantic: { intents: [], mechanisms: [], tags: [] },
    editLock: null
  });
  const edge = (from, to, field) => ({
    from,
    to,
    field,
    source: { kind: "Snapshot", ref: `${from}.${field}` }
  });

  return {
    schemaVersion: 0,
    graphVersion: "0.1.0",
    workspaceId: "diamond",
    revision: "r1",
    nodes: [
      node("TbSkill:S", "Skill"),
      node("EffectGroup:A", "EffectGroup"),
      node("EffectGroup:B", "EffectGroup"),
      node("TbBuff:C", "Buff"),
      node("EffectGroup:D", "EffectGroup"),
      node("TbEffect:E", "Effect"),
      node("TbDamagePipeline:P", "DamagePipeline")
    ],
    edges: [
      edge("TbSkill:S", "EffectGroup:A", "a"),
      edge("TbSkill:S", "EffectGroup:B", "b"),
      edge("EffectGroup:A", "TbBuff:C", "ac"),
      edge("EffectGroup:B", "TbBuff:C", "bc"),
      edge("TbBuff:C", "EffectGroup:D", "cd"),
      edge("EffectGroup:D", "TbEffect:E", "de"),
      edge("TbEffect:E", "TbDamagePipeline:P", "ep")
    ]
  };
}

test("display-name resolution reports total matches and truncation", async () => {
  const { index } = await createIndex();
  const result = index.resolveAsset({
    namespace: "TbSkill",
    name: "Moon",
    limit: 1
  });

  assert.equal(result.status, "Candidates");
  assert.equal(result.candidateCount, 2);
  assert.equal(result.returnedCount, 1);
  assert.equal(result.truncated, true);
  assert.equal(result.candidates.length, 1);
});

test("asset resolution rejects unknown namespaces and ambiguous input mode", async () => {
  const { index } = await createIndex();

  assert.equal(
    index.resolveAsset({ namespace: "TbUnknown", name: "anything" }).code,
    "UNKNOWN_NAMESPACE"
  );
  assert.equal(
    index.resolveAsset({
      namespace: "TbSkill",
      id: 100101,
      name: "Moonlight"
    }).code,
    "ID_AND_NAME_CONFLICT"
  );
});

test("bounded graph projection never returns dangling edges or exceeds limits", async () => {
  const { index } = await createIndex();
  const result = index.getGraph({
    root: "TbSkill:100101",
    depth: 3,
    direction: "both",
    limit: 3
  });
  const nodeKeys = new Set(result.nodes.map((node) => node.key));

  assert.equal(result.status, "Truncated");
  assert.ok(result.nodes.length <= 3);
  assert.ok(result.edges.length <= 3);
  assert.ok(
    result.edges.every(
      (edge) => nodeKeys.has(edge.from) && nodeKeys.has(edge.to)
    )
  );
});

test("strict graph validation rejects missing semantic, evidence, and wrong kind", async () => {
  const { graph, registry } = await createIndex();

  const missingSemantic = structuredClone(graph);
  delete missingSemantic.nodes[0].semantic;
  assert.throws(
    () => createSkillGraphIndex(missingSemantic, registry),
    /semantic/
  );

  const missingEvidence = structuredClone(graph);
  delete missingEvidence.edges[0].source;
  assert.throws(
    () => createSkillGraphIndex(missingEvidence, registry),
    /source/
  );

  const wrongKind = structuredClone(graph);
  wrongKind.nodes[0].kind = "Effect";
  assert.throws(
    () => createSkillGraphIndex(wrongKind, registry),
    /kind/
  );
});

test("shared subtrees are emitted once and marked as reused", async () => {
  const { registry } = await createIndex();
  const index = createSkillGraphIndex(diamondGraph(), registry);
  const result = index.explainExecutionChain({ skill: "TbSkill:S" });
  const uniqueEdges = new Set(
    result.steps.map((step) => step.edgeSequence)
  );

  assert.equal(result.status, "Ready");
  assert.equal(result.summary.stepCount, 7);
  assert.equal(result.summary.reusedSubtreeCount, 1);
  assert.equal(uniqueEdges.size, result.steps.length);
  assert.equal(
    result.steps.filter(
      (step) => step.from === "EffectGroup:B" && step.to === "TbBuff:C"
    ).length,
    1
  );
});

test("execution chain reports cycles and invalid input without throwing", async () => {
  const { registry } = await createIndex();
  const graph = diamondGraph();
  graph.edges.push({
    from: "TbDamagePipeline:P",
    to: "TbSkill:S",
    field: "cycle",
    source: { kind: "Snapshot", ref: "cycle" }
  });
  const index = createSkillGraphIndex(graph, registry);
  const cycle = index.explainExecutionChain({ skill: "TbSkill:S" });
  const invalid = index.getGraph({
    root: "TbSkill:S",
    direction: "sideways"
  });

  assert.equal(cycle.status, "Partial");
  assert.equal(cycle.summary.cycleDetected, true);
  assert.ok(cycle.steps.some((step) => step.from === "TbDamagePipeline:P"));
  assert.equal(invalid.status, "InvalidRequest");
  assert.equal(invalid.code, "INPUT_SCHEMA_VIOLATION");
});

test("execution chain returns evidence and snapshot identity", async () => {
  const { index } = await createIndex();
  const result = index.explainExecutionChain({ skill: "TbSkill:100101" });

  assert.equal(result.status, "Ready");
  assert.ok(result.steps.length >= 6);
  assert.ok(
    result.steps.every(
      (step) =>
        typeof step.from === "string" &&
        typeof step.field === "string" &&
        typeof step.evidence?.ref === "string"
    )
  );
  assert.deepEqual(result.snapshot, {
    graphVersion: "0.1.0",
    workspaceId: "sample-workspace",
    revision: "sample-r1"
  });
});

test("all read-only tools return the same snapshot identity", async () => {
  const { index } = await createIndex();
  const results = [
    index.getCapabilityContext({ query: "damage" }),
    index.resolveAsset({ namespace: "TbSkill", id: 100101 }),
    index.getGraph({ root: "TbSkill:100101", depth: 1 }),
    index.searchSimilarSkills({ focus: "TbSkill:100101" }),
    index.explainExecutionChain({ skill: "TbSkill:100101" })
  ];

  for (const result of results) {
    assert.deepEqual(result.snapshot, {
      graphVersion: "0.1.0",
      workspaceId: "sample-workspace",
      revision: "sample-r1"
    });
  }
});

test("capability context returns legal namespaces and enum values", async () => {
  const { index } = await createIndex();
  const result = index.getCapabilityContext({ query: "damage" });
  const numericType = result.enums.find((item) => item.name === "NumericType");

  assert.ok(result.entities.some((entity) => entity.namespace === "TbSkill"));
  assert.ok(numericType);
  assert.ok(numericType.values.some((value) => value.name === "Hp"));
});

test("capability context distinguishes no-match from truncation", async () => {
  const { index } = await createIndex();
  const missing = index.getCapabilityContext({
    query: "zzzz-no-such-thing"
  });
  const truncated = index.getCapabilityContext({
    query: "damage",
    limit: 2
  });

  assert.equal(missing.status, "NotFound");
  assert.equal(missing.counts.effects.total, 0);
  assert.equal(truncated.status, "Ready");
  assert.equal(truncated.truncated, true);
  assert.equal(truncated.counts.effects.truncated, true);
  assert.ok(truncated.effects.length <= 2);
});

test("capability context exposes entity-field enums with bounded values", async () => {
  const { index } = await createIndex();
  const skillType = index.getCapabilityContext({ query: "skill_type" });
  const numericType = index.getCapabilityContext({
    enum: "NumericType",
    enumValueLimit: 5
  });

  assert.equal(skillType.entityFields[0].enumName, "ESkillType");
  assert.ok(skillType.enums.some((item) => item.name === "ESkillType"));
  assert.equal(numericType.enums[0].returnedCount, 5);
  assert.equal(numericType.enums[0].truncated, true);
});

test("library API enforces the embedded input schema", async () => {
  const { index } = await createIndex();
  const extra = index.getGraph({
    root: "TbSkill:100101",
    bogusField: 1
  });
  const wrongType = index.getCapabilityContext({ query: 12345 });

  assert.equal(extra.status, "InvalidRequest");
  assert.equal(extra.code, "INPUT_SCHEMA_VIOLATION");
  assert.equal(wrongType.status, "InvalidRequest");
  assert.equal(wrongType.code, "INPUT_SCHEMA_VIOLATION");
});

test("runtime graph validation consumes the embedded graph schema", async () => {
  const { graph, registry } = await createIndex();
  const extraNodeProperty = structuredClone(graph);
  extraNodeProperty.nodes[0].extraProperty = true;
  const emptyKeySuffix = structuredClone(graph);
  const oldKey = emptyKeySuffix.nodes[0].key;
  emptyKeySuffix.nodes[0].key = "TbSkill:";
  for (const edge of emptyKeySuffix.edges) {
    if (edge.from === oldKey) edge.from = emptyKeySuffix.nodes[0].key;
    if (edge.to === oldKey) edge.to = emptyKeySuffix.nodes[0].key;
  }

  assert.throws(
    () => createSkillGraphIndex(extraNodeProperty, registry),
    /additional property/
  );
  assert.throws(
    () => createSkillGraphIndex(emptyKeySuffix, registry),
    /does not match/
  );
});

test("non-skill focus and independent graph limits are structured", async () => {
  const { index } = await createIndex();
  const wrongFocus = index.searchSimilarSkills({
    focus: "TbBuff:10010005"
  });
  const limited = index.getGraph({
    root: "TbSkill:100101",
    depth: 2,
    nodeLimit: 2,
    edgeLimit: 1
  });

  assert.equal(wrongFocus.status, "InvalidRequest");
  assert.equal(wrongFocus.code, "NOT_A_SKILL");
  assert.equal(limited.limits.maxNodes, 2);
  assert.equal(limited.limits.maxEdges, 1);
  assert.ok(limited.nodes.length <= 2);
  assert.ok(limited.edges.length <= 1);
});

test("read-only operations do not mutate the source snapshot", async () => {
  const { graph, index } = await createIndex();
  const before = JSON.stringify(graph);

  index.getCapabilityContext({ query: "damage" });
  index.resolveAsset({ namespace: "TbSkill", id: 100101 });
  index.getGraph({ root: "TbSkill:100101", depth: 2 });
  index.searchSimilarSkills({ focus: "TbSkill:100101" });
  index.explainExecutionChain({ skill: "TbSkill:100101" });

  assert.equal(JSON.stringify(graph), before);
});
