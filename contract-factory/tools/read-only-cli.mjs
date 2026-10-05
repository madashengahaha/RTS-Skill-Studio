#!/usr/bin/env node

import { createSkillGraphIndex } from "../src/skill-graph-index.mjs";
import { validateSchema } from "../src/schema-lite.mjs";
import { parseArgs, readJson } from "./lib.mjs";

const VALUE_FLAGS = new Set([
  "graph",
  "registry",
  "query",
  "intent",
  "actionKey",
  "enum",
  "enumValueLimit",
  "limit",
  "nodeLimit",
  "edgeLimit",
  "namespace",
  "id",
  "name",
  "root",
  "depth",
  "direction",
  "focus",
  "skill"
]);

for (let index = 0; index < process.argv.length - 2; index += 1) {
  const token = process.argv[index + 2];
  if (!token.startsWith("--")) {
    continue;
  }
  const key = token.slice(2);
  if (!VALUE_FLAGS.has(key)) {
    console.error(`Unknown option: --${key}`);
    process.exit(2);
  }
  const next = process.argv[index + 3];
  if (next === undefined || next.startsWith("--")) {
    console.error(`Option --${key} requires a value.`);
    process.exit(2);
  }
  index += 1;
}

const args = parseArgs(process.argv.slice(2));
const positionals = args._ ?? [];
const toolName = positionals[0];

if (!toolName) {
  console.error(
    "Usage: node tools/read-only-cli.mjs [--graph path] [--registry path] <tool> [--arg value]"
  );
  process.exit(2);
}

const graphPath = args.graph ?? "examples/sample-skill-graph.json";
const registryPath = args.registry ?? "config/capability-registry.v0.json";
const [graph, registry] = await Promise.all([
  readJson(graphPath),
  readJson(registryPath)
]);
const toolContract = await readJson("config/read-only-tools.v0.json");
const index = createSkillGraphIndex(graph, registry);

function numeric(value) {
  if (value === undefined) {
    return undefined;
  }
  if (!/^-?\d+$/.test(value)) {
    throw new Error(`Expected an integer, received: ${value}`);
  }
  return Number.parseInt(value, 10);
}

const methods = {
  get_capability_context: index.getCapabilityContext,
  resolve_asset: index.resolveAsset,
  get_graph: index.getGraph,
  search_similar_skills: index.searchSimilarSkills,
  explain_execution_chain: index.explainExecutionChain
};
const execute = methods[toolName];
const toolDefinition = toolContract.tools.find((tool) => tool.name === toolName);

if (!execute || !toolDefinition) {
  console.error(`Unknown read-only tool: ${toolName}`);
  process.exit(2);
}

try {
  const input = {
    query: args.query,
    intent: args.intent,
    actionKey: args.actionKey,
    enum: args.enum,
    enumValueLimit: numeric(args.enumValueLimit),
    limit: numeric(args.limit),
    nodeLimit: numeric(args.nodeLimit),
    edgeLimit: numeric(args.edgeLimit),
    namespace: args.namespace,
    id: numeric(args.id),
    name: args.name,
    root: args.root,
    depth: numeric(args.depth),
    direction: args.direction,
    focus: args.focus,
    skill: args.skill
  };

  for (const key of Object.keys(input)) {
    if (input[key] === undefined) {
      delete input[key];
    }
  }

  const inputErrors = validateSchema(toolDefinition.inputSchema, input);
  if (inputErrors.length > 0) {
    console.log(
      JSON.stringify(
        {
          tool: toolName,
          readOnly: true,
          status: "InvalidRequest",
          code: "CLI_INPUT_SCHEMA",
          errors: inputErrors,
          snapshot: {
            graphVersion: index.graphVersion,
            workspaceId: index.workspaceId,
            revision: index.revision
          }
        },
        null,
        2
      )
    );
    process.exit(2);
  }

  console.log(JSON.stringify(execute(input), null, 2));
} catch (error) {
  console.log(
    JSON.stringify(
      {
        tool: toolName,
        readOnly: true,
        status: "InvalidRequest",
        code: "CLI_INPUT_ERROR",
        message: error instanceof Error ? error.message : String(error),
        snapshot: {
          graphVersion: index.graphVersion,
          workspaceId: index.workspaceId,
          revision: index.revision
        }
      },
      null,
      2
    )
  );
  process.exit(2);
}
