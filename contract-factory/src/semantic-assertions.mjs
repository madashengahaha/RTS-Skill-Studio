import { normalizePlan } from "./semantic-ir.mjs";

function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function deepEqual(left, right) {
  return JSON.stringify(left) === JSON.stringify(right);
}

function findOperations(ir, kind) {
  return ir.operations.filter((operation) => operation.kind === kind);
}

function resolveParam(ir, path) {
  const parts = path.split(".");
  const operation = ir.operations.find((item) => item.kind === parts[0]);
  if (!operation) {
    return { found: false };
  }

  if (parts[0] === "AddEffectIntent") {
    const intent = parts[1];
    if (operation.intent !== intent) {
      return { found: false };
    }
    const parameter = parts.slice(2).join(".");
    return {
      found: Object.hasOwn(operation.params, parameter),
      value: operation.params[parameter]
    };
  }

  if (parts[0] === "ModifySkill" || parts[0] === "ModifyEffectIntent") {
    const field = parts.slice(1).join(".");
    return {
      found: Object.hasOwn(operation.fields, field),
      value: operation.fields[field]
    };
  }

  return { found: false };
}

function collectNamespaces(value, namespaces = new Set()) {
  if (Array.isArray(value)) {
    for (const item of value) {
      collectNamespaces(item, namespaces);
    }
    return namespaces;
  }

  if (!isObject(value)) {
    return namespaces;
  }

  if (typeof value.namespace === "string" && typeof value.binding === "string") {
    namespaces.add(value.namespace);
  }

  for (const item of Object.values(value)) {
    collectNamespaces(item, namespaces);
  }
  return namespaces;
}

export function assertionMatches(plan, assertion) {
  const ir = normalizePlan(plan);

  switch (assertion.kind) {
    case "status":
      return ir.status === assertion.equals;
    case "hasOperation":
      return findOperations(ir, assertion.operation).length > 0;
    case "noOperation":
      return findOperations(ir, assertion.operation).length === 0;
    case "hasIntent":
      return ir.operations.some((operation) => operation.intent === assertion.intent);
    case "noIntent":
      return ir.operations.every((operation) => operation.intent !== assertion.intent);
    case "paramEquals": {
      const result = resolveParam(ir, assertion.path);
      return result.found && deepEqual(result.value, assertion.equals);
    }
    case "operationCount": {
      const count = ir.operations.length;
      return (
        (assertion.equals === undefined || count === assertion.equals) &&
        (assertion.min === undefined || count >= assertion.min) &&
        (assertion.max === undefined || count <= assertion.max)
      );
    }
    case "clarificationField":
      return ir.clarifications.some(
        (clarification) => clarification.fieldPath === assertion.fieldPath
      );
    case "unsupportedCode":
      return ir.unsupported.some((item) => item.code === assertion.code);
    case "referenceNamespace":
      return collectNamespaces(ir.operations).has(assertion.namespace);
    default:
      throw new Error(`Unknown assertion kind: ${assertion.kind}`);
  }
}

export function evaluateAssertions(plan, assertions = []) {
  return assertions
    .filter((assertion) => !assertionMatches(plan, assertion))
    .map((assertion) => assertion);
}

export function evaluateForbiddenAssertions(plan, assertions = []) {
  return assertions
    .filter((assertion) => assertionMatches(plan, assertion))
    .map((assertion) => assertion);
}

