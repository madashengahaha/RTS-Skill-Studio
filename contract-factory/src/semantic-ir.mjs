function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function compareText(left, right) {
  return String(left).localeCompare(String(right), "en");
}

function stableObject(value) {
  if (Array.isArray(value)) {
    return value.map(stableObject);
  }

  if (!isObject(value)) {
    return value;
  }

  return Object.fromEntries(
    Object.entries(value)
      .sort(([left], [right]) => compareText(left, right))
      .map(([key, item]) => [key, stableObject(item)])
  );
}

function valueOf(envelope) {
  if (!isObject(envelope) || !("value" in envelope)) {
    return envelope;
  }

  return envelope.value;
}

function normalizeValueMap(values) {
  if (!isObject(values)) {
    return {};
  }

  return stableObject(
    Object.fromEntries(
      Object.entries(values).map(([key, envelope]) => [key, valueOf(envelope)])
    )
  );
}

function normalizeRef(ref) {
  if (!isObject(ref)) {
    return null;
  }

  const normalized = {
    binding: ref.binding,
    namespace: ref.namespace
  };

  if (ref.id !== undefined) {
    normalized.id = ref.id;
  }

  if (ref.localKey !== undefined) {
    normalized.localKey = ref.localKey;
  }

  if (Array.isArray(ref.candidates)) {
    normalized.candidates = ref.candidates
      .map((candidate) => candidate.key)
      .sort(compareText);
  }

  return stableObject(normalized);
}

function normalizeTarget(target) {
  if (!isObject(target)) {
    return null;
  }

  const normalized = {};
  if (target.team !== undefined) {
    normalized.team = valueOf(target.team);
  }
  if (target.unitTypes !== undefined) {
    normalized.unitTypes = target.unitTypes.map(valueOf);
  }
  if (target.shape !== undefined) {
    normalized.shape = valueOf(target.shape);
  }
  if (target.radius !== undefined) {
    normalized.radius = valueOf(target.radius);
  }
  if (target.count !== undefined) {
    normalized.count = valueOf(target.count);
  }
  if (target.priority !== undefined) {
    normalized.priority = target.priority.map(valueOf);
  }

  return stableObject(normalized);
}

function normalizeSearch(search) {
  if (!isObject(search)) {
    return null;
  }

  const normalized = {};
  if (search.existing !== undefined) {
    normalized.existing = normalizeRef(search.existing);
  }
  if (search.intent !== undefined) {
    normalized.intent = normalizeTarget(search.intent);
  }

  return stableObject(normalized);
}

function normalizeOperation(operation) {
  const base = {
    kind: operation.kind
  };

  switch (operation.kind) {
    case "CreateSkill":
      return stableObject({
        ...base,
        owner: normalizeRef(operation.owner),
        skillType: valueOf(operation.skillType),
        target: normalizeTarget(operation.target)
      });
    case "ModifySkill":
      return stableObject({
        ...base,
        skill: normalizeRef(operation.skill),
        fields: normalizeValueMap(operation.fields)
      });
    case "AddEffectIntent":
      return stableObject({
        ...base,
        owner: normalizeRef(operation.owner),
        intent: operation.intent,
        params: normalizeValueMap(operation.params),
        target: normalizeTarget(operation.target),
        search: normalizeSearch(operation.search)
      });
    case "ModifyEffectIntent":
    case "ModifyConditionIntent":
      return stableObject({
        ...base,
        owner: normalizeRef(operation.owner),
        intentRef: operation.intentRef,
        fields: normalizeValueMap(operation.fields)
      });
    case "DeleteEffectIntent":
    case "DeleteConditionIntent":
      return stableObject({
        ...base,
        owner: normalizeRef(operation.owner),
        intentRef: operation.intentRef
      });
    case "AddConditionIntent":
      return stableObject({
        ...base,
        owner: normalizeRef(operation.owner),
        intent: operation.intent,
        params: normalizeValueMap(operation.params)
      });
    case "LinkExisting":
    case "RemoveLink":
      return stableObject({
        ...base,
        parent: normalizeRef(operation.parent),
        field: operation.field,
        parameterIndex: operation.parameterIndex,
        target: normalizeRef(operation.target)
      });
    case "ReorderMembers":
      return stableObject({
        ...base,
        group: normalizeRef(operation.group),
        orderedMembers: operation.orderedMembers.map(normalizeRef)
      });
    default:
      return stableObject({
        ...base,
        operation
      });
  }
}

function operationSortKey(operation) {
  return JSON.stringify(operation);
}

export function normalizePlan(plan) {
  if (!isObject(plan)) {
    throw new TypeError("Plan must be an object.");
  }

  const operations = Array.isArray(plan.operations)
    ? plan.operations.map(normalizeOperation).sort((left, right) =>
        compareText(operationSortKey(left), operationSortKey(right))
      )
    : [];

  const clarifications = Array.isArray(plan.clarifications)
    ? plan.clarifications
        .map((item) =>
          stableObject({
            fieldPath: item.fieldPath,
            options: (item.options ?? []).map((option) => option.key).sort(compareText)
          })
        )
        .sort((left, right) =>
          compareText(JSON.stringify(left), JSON.stringify(right))
        )
    : [];

  const unsupported = Array.isArray(plan.unsupported)
    ? plan.unsupported
        .map((item) =>
          stableObject({
            code: item.code,
            closestMechanism: item.closestMechanism,
            requiredRuntimeCapability: item.requiredRuntimeCapability
          })
        )
        .sort((left, right) =>
          compareText(JSON.stringify(left), JSON.stringify(right))
        )
    : [];

  return stableObject({
    status: plan.status,
    focus: normalizeRef(plan.request?.focus),
    operations,
    clarifications,
    unsupported
  });
}

export function semanticEquals(leftPlan, rightPlan) {
  return JSON.stringify(normalizePlan(leftPlan)) === JSON.stringify(normalizePlan(rightPlan));
}

