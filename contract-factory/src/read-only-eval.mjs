function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function deepEqual(left, right) {
  return JSON.stringify(left) === JSON.stringify(right);
}

function resolvePath(value, path) {
  const parts = path
    .split(".")
    .flatMap((part) => {
      const match = /^([^[\]]+)(?:\[(\d+)\])?$/.exec(part);
      if (!match) {
        return [part];
      }
      return match[2] === undefined
        ? [match[1]]
        : [match[1], Number.parseInt(match[2], 10)];
    });
  let current = value;

  for (const part of parts) {
    if (current === null || current === undefined) {
      return { found: false };
    }
    if (!isObject(current) && !Array.isArray(current)) {
      return { found: false };
    }
    if (
      (Array.isArray(current) && typeof part !== "number") ||
      (!Array.isArray(current) && typeof part === "number") ||
      !Object.hasOwn(current, part)
    ) {
      return { found: false };
    }
    current = current[part];
  }

  return { found: true, value: current };
}

function assertionMatches(result, assertion) {
  switch (assertion.kind) {
    case "status":
      return result.status === assertion.equals;
    case "pathEquals": {
      const resolved = resolvePath(result, assertion.path);
      return resolved.found && deepEqual(resolved.value, assertion.equals);
    }
    case "pathIncludes": {
      const resolved = resolvePath(result, assertion.path);
      return (
        resolved.found &&
        Array.isArray(resolved.value) &&
        resolved.value.includes(assertion.equals)
      );
    }
    case "pathArrayFieldContains": {
      const resolved = resolvePath(result, assertion.path);
      return (
        resolved.found &&
        Array.isArray(resolved.value) &&
        resolved.value.some((item) => item?.[assertion.field] === assertion.value)
      );
    }
    case "pathArrayMinLength": {
      const resolved = resolvePath(result, assertion.path);
      return (
        resolved.found &&
        Array.isArray(resolved.value) &&
        resolved.value.length >= assertion.min
      );
    }
    case "pathLengthEquals": {
      const resolved = resolvePath(result, assertion.path);
      return (
        resolved.found &&
        Array.isArray(resolved.value) &&
        resolved.value.length === assertion.equals
      );
    }
    case "pathLengthAtMost": {
      const resolved = resolvePath(result, assertion.path);
      return (
        resolved.found &&
        Array.isArray(resolved.value) &&
        resolved.value.length <= assertion.max
      );
    }
    case "graphClosed": {
      const nodes = resolvePath(result, "nodes");
      const edges = resolvePath(result, "edges");
      if (!nodes.found || !edges.found) {
        return false;
      }
      const keys = new Set(nodes.value.map((node) => node.key));
      return edges.value.every(
        (edge) => keys.has(edge.from) && keys.has(edge.to)
      );
    }
    case "evidenceOnPath": {
      const resolved = resolvePath(result, assertion.path);
      return (
        resolved.found &&
        Array.isArray(resolved.value) &&
        resolved.value.length > 0 &&
        resolved.value.every(
          (item) =>
            item &&
            (item.evidence !== undefined || item.source !== undefined)
        )
      );
    }
    case "readOnlyFlag":
      return result.readOnly === true;
    default:
      throw new Error(`Unknown read-only assertion kind: ${assertion.kind}`);
  }
}

function methodForTool(index, toolName) {
  const methods = {
    get_capability_context: "getCapabilityContext",
    resolve_asset: "resolveAsset",
    get_graph: "getGraph",
    search_similar_skills: "searchSimilarSkills",
    explain_execution_chain: "explainExecutionChain"
  };
  const method = methods[toolName];
  if (!method || typeof index[method] !== "function") {
    throw new Error(`Unknown read-only tool: ${toolName}`);
  }
  return index[method].bind(index);
}

export function evaluateReadOnlyCase(index, testCase) {
  const execute = methodForTool(index, testCase.tool);
  const result = execute(testCase.input ?? {});
  const failures = (testCase.assertions ?? []).filter(
    (assertion) => !assertionMatches(result, assertion)
  );

  return {
    caseId: testCase.id,
    tool: testCase.tool,
    result,
    failures
  };
}
