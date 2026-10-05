const MAX_LIMIT = 500;
const DEFAULT_DEPTH = 1;
const MAX_DEPTH = 5;
const DIRECTIONS = new Set(["out", "in", "both"]);
const EVIDENCE_KINDS = new Set([
  "Excel",
  "RuntimeBinding",
  "Schema",
  "Snapshot"
]);

function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function deepFreeze(value) {
  if (!isObject(value) && !Array.isArray(value)) {
    return value;
  }

  Object.freeze(value);
  for (const item of Object.values(value)) {
    deepFreeze(item);
  }
  return value;
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function assertString(value, label) {
  assert(typeof value === "string" && value.length > 0, `${label} must be a non-empty string.`);
}

function parseLimit(value, fallback) {
  if (value === undefined) {
    return fallback;
  }

  assert(Number.isInteger(value) && value > 0, "limit must be a positive integer.");
  return Math.min(value, MAX_LIMIT);
}

function parseDepth(value) {
  if (value === undefined) {
    return DEFAULT_DEPTH;
  }

  assert(
    Number.isInteger(value) && value >= 0 && value <= MAX_DEPTH,
    `depth must be an integer between 0 and ${MAX_DEPTH}.`
  );
  return value;
}

function parseDirection(value) {
  const direction = value ?? "out";
  assert(DIRECTIONS.has(direction), "direction must be out, in, or both.");
  return direction;
}

function normalizeText(value) {
  return String(value ?? "")
    .toLowerCase()
    .replace(/[\s_-]+/g, "");
}

function textContains(haystack, needle) {
  const normalizedNeedle = normalizeText(needle);
  if (!normalizedNeedle) {
    return true;
  }

  return normalizeText(haystack).includes(normalizedNeedle);
}

function overlapScore(left, right) {
  const leftSet = new Set(left);
  const rightSet = new Set(right);
  const union = new Set([...leftSet, ...rightSet]);
  if (union.size === 0) {
    return { score: 0, shared: [] };
  }

  const shared = [...leftSet].filter((item) => rightSet.has(item));
  return {
    score: shared.length / union.size,
    shared
  };
}

function stableNode(node) {
  return {
    key: node.key,
    namespace: node.namespace,
    kind: node.kind,
    label: node.label,
    attributes: node.attributes,
    semantic: node.semantic,
    editLock: node.editLock
  };
}

function stableEdge(edge, sequence) {
  return {
    sequence,
    from: edge.from,
    to: edge.to,
    field: edge.field,
    parameterIndex: edge.parameterIndex ?? null,
    source: edge.source
  };
}

function validateStringArray(value, label, unique = false) {
  assert(Array.isArray(value), `${label} must be an array.`);
  value.forEach((item, index) => assertString(item, `${label}[${index}]`));
  if (unique) {
    assert(new Set(value).size === value.length, `${label} must not contain duplicates.`);
  }
}

function validateRegistry(registry) {
  assert(isObject(registry), "Capability registry must be an object.");
  assertString(registry.registryVersion, "registry.registryVersion");
  assert(Array.isArray(registry.entities), "registry.entities must be an array.");
  assert(Array.isArray(registry.intents), "registry.intents must be an array.");
  assert(Array.isArray(registry.effects), "registry.effects must be an array.");
  assert(Array.isArray(registry.conditions), "registry.conditions must be an array.");
  assert(Array.isArray(registry.enums), "registry.enums must be an array.");
  assert(Array.isArray(registry.entityFields), "registry.entityFields must be an array.");
  assert(
    isObject(registry.readOnlyToolContracts),
    "registry.readOnlyToolContracts must be an object."
  );
  assert(
    Array.isArray(registry.readOnlyToolContracts.tools),
    "registry.readOnlyToolContracts.tools must be an array."
  );
  assert(
    isObject(registry.skillGraphSchema),
    "registry.skillGraphSchema must be an object."
  );

  const weights = registry.similarityScoring?.skillWeights;
  assert(isObject(weights), "registry.similarityScoring.skillWeights is required.");
  for (const key of ["intents", "mechanisms", "tags", "references"]) {
    assert(
      typeof weights[key] === "number" && weights[key] >= 0,
      `registry.similarityScoring.skillWeights.${key} must be non-negative.`
    );
  }

  for (const entity of registry.entities) {
    assert(isObject(entity), "Every registry entity must be an object.");
    assertString(entity.key, "registry entity key");
    assertString(entity.namespace, `registry entity ${entity.key} namespace`);
  }

  const contractTools = registry.readOnlyToolContracts.tools.map(
    (tool) => tool.name
  );
  assert(
    [...contractTools].sort().join("|") ===
      [...registry.readOnlyTools].sort().join("|"),
    "registry read-only tool contracts must match readOnlyTools."
  );
}

function validateGraph(graph, registry) {
  const schemaErrors = validateSchema(registry.skillGraphSchema, graph);
  assert(
    schemaErrors.length === 0,
    `Skill graph violates skill-graph.schema.json: ${schemaErrors.join("; ")}`
  );
  assert(isObject(graph), "Skill graph must be an object.");
  assert(graph.schemaVersion === 0, "Skill graph schemaVersion must be 0.");
  assertString(graph.graphVersion, "Skill graph graphVersion");
  assertString(graph.workspaceId, "Skill graph workspaceId");
  assertString(graph.revision, "Skill graph revision");
  assert(Array.isArray(graph.nodes), "Skill graph nodes must be an array.");
  assert(Array.isArray(graph.edges), "Skill graph edges must be an array.");

  const entitiesByName = new Map(
    registry.entities.map((entity) => [entity.namespace, entity])
  );
  const keys = new Set();

  for (const node of graph.nodes) {
    assert(isObject(node), "Every graph node must be an object.");
    assertString(node.key, "Node key");
    assertString(node.namespace, `Node ${node.key} namespace`);
    assertString(node.kind, `Node ${node.key} kind`);
    assert(typeof node.label === "string", `Node ${node.key} label must be a string.`);
    assert(isObject(node.attributes), `Node ${node.key} attributes must be an object.`);
    assert(!keys.has(node.key), `Duplicate graph node key: ${node.key}`);
    assert(
      node.key.startsWith(`${node.namespace}:`),
      `Node key ${node.key} must start with namespace ${node.namespace}.`
    );

    const entity = entitiesByName.get(node.namespace);
    assert(entity, `Node ${node.key} uses unknown namespace ${node.namespace}.`);
    assert(
      node.kind === entity.key,
      `Node ${node.key} kind ${node.kind} does not match entity ${entity.key}.`
    );

    assert(isObject(node.semantic), `Node ${node.key} semantic is required.`);
    validateStringArray(node.semantic.intents, `Node ${node.key} semantic.intents`, true);
    validateStringArray(
      node.semantic.mechanisms,
      `Node ${node.key} semantic.mechanisms`,
      true
    );
    validateStringArray(node.semantic.tags, `Node ${node.key} semantic.tags`, true);

    if (node.editLock !== null) {
      assert(isObject(node.editLock), `Node ${node.key} editLock must be null or an object.`);
      assertString(node.editLock.owner, `Node ${node.key} editLock.owner`);
      assertString(node.editLock.acquiredAt, `Node ${node.key} editLock.acquiredAt`);
    }

    keys.add(node.key);
  }

  graph.edges.forEach((edge, index) => {
    assert(isObject(edge), "Every graph edge must be an object.");
    assertString(edge.from, `Edge ${index} from`);
    assertString(edge.to, `Edge ${index} to`);
    assertString(edge.field, `Edge ${index} field`);
    assert(keys.has(edge.from), `Edge source node does not exist: ${edge.from}`);
    assert(keys.has(edge.to), `Edge target node does not exist: ${edge.to}`);
    if (edge.parameterIndex !== undefined && edge.parameterIndex !== null) {
      assert(
        Number.isInteger(edge.parameterIndex) && edge.parameterIndex >= 0,
        `Edge ${index} parameterIndex must be null or a non-negative integer.`
      );
    }
    assert(isObject(edge.source), `Edge ${index} source is required.`);
    assert(
      EVIDENCE_KINDS.has(edge.source.kind),
      `Edge ${index} source.kind is invalid.`
    );
    assertString(edge.source.ref, `Edge ${index} source.ref`);
  });
}

function actionMatches(action, query) {
  if (!query) {
    return true;
  }

  return [
    action.key,
    action.label,
    action.description,
    action.warning,
    action.semantic?.mechanism,
    ...(action.semantic?.intents ?? []),
    ...(action.semantic?.tags ?? [])
  ].some((value) => textContains(value, query));
}

export function createSkillGraphIndex(graph, registry) {
  validateRegistry(registry);
  validateGraph(graph, registry);

  const snapshot = deepFreeze(structuredClone(graph));
  const registrySnapshot = deepFreeze(structuredClone(registry));
  const nodesByKey = new Map(snapshot.nodes.map((node) => [node.key, node]));
  const entitiesByNamespace = new Map(
    registrySnapshot.entities.map((entity) => [entity.namespace, entity])
  );
  const enumByName = new Map(
    registrySnapshot.enums.map((definition) => [definition.name, definition])
  );
  const toolContracts = new Map(
    registrySnapshot.readOnlyToolContracts.tools.map((tool) => [tool.name, tool])
  );
  const skillEntity = registrySnapshot.entities.find(
    (entity) => entity.key === "Skill"
  );
  assert(skillEntity, "Registry must define a Skill entity.");
  const skillKind = skillEntity.key;
  const outEdges = new Map();
  const inEdges = new Map();

  snapshot.edges.forEach((edge, sequence) => {
    const outbound = outEdges.get(edge.from) ?? [];
    const inbound = inEdges.get(edge.to) ?? [];
    outbound.push({ edge, sequence });
    inbound.push({ edge, sequence });
    outEdges.set(edge.from, outbound);
    inEdges.set(edge.to, inbound);
  });

  function snapshotIdentity() {
    return {
      graphVersion: snapshot.graphVersion,
      workspaceId: snapshot.workspaceId,
      revision: snapshot.revision
    };
  }

  function safeTool(toolName, execute) {
    return (input = {}) => {
      const contract = toolContracts.get(toolName);
      if (!contract) {
        return {
          tool: toolName,
          readOnly: true,
          status: "InvalidRequest",
          code: "TOOL_CONTRACT_MISSING",
          snapshot: snapshotIdentity()
        };
      }

      const inputErrors = validateSchema(contract.inputSchema, input);
      if (inputErrors.length > 0) {
        return {
          tool: toolName,
          readOnly: true,
          status: "InvalidRequest",
          code: "INPUT_SCHEMA_VIOLATION",
          errors: inputErrors,
          snapshot: snapshotIdentity()
        };
      }

      try {
        return execute(input);
      } catch (error) {
        return {
          tool: toolName,
          readOnly: true,
          status: "InvalidRequest",
          code: "INVALID_TOOL_INPUT",
          message: error instanceof Error ? error.message : String(error),
          snapshot: snapshotIdentity()
        };
      }
    };
  }

  function getEdges(key, direction) {
    const edges = [];
    if (direction === "out" || direction === "both") {
      edges.push(...(outEdges.get(key) ?? []));
    }
    if (direction === "in" || direction === "both") {
      edges.push(...(inEdges.get(key) ?? []));
    }
    return edges.sort((left, right) => {
      const fieldOrder = left.edge.field.localeCompare(right.edge.field, "en");
      return fieldOrder === 0 ? left.sequence - right.sequence : fieldOrder;
    });
  }

  function selectedEnumNames(actions, entityFields) {
    const names = new Set(
      [
        ...actions.flatMap((action) => action.parameters ?? []),
        ...entityFields
      ]
        .map((item) => item.enumName)
        .filter(Boolean)
    );
    return [...names].sort((left, right) => left.localeCompare(right, "en"));
  }

  function enumResults(names, valueLimit) {
    return names
      .map((name) => enumByName.get(name))
      .filter(Boolean)
      .map((definition) => {
        const values = definition.values.slice(0, valueLimit);
        return {
          name: definition.name,
          flags: definition.flags,
          comment: definition.comment,
          totalCount: definition.values.length,
          returnedCount: values.length,
          truncated: values.length < definition.values.length,
          values
        };
      });
  }

  function compactEntities() {
    return registrySnapshot.entities.map((entity) => ({
      key: entity.key,
      namespace: entity.namespace,
      kind: entity.kind,
      shared: entity.shared
    }));
  }

  function getCapabilityContext(input = {}) {
    const query = String(input.query ?? "").trim();
    const limit = parseLimit(input.limit, 10);
    const enumValueLimit = parseLimit(input.enumValueLimit, 20);
    const intentKeys = registrySnapshot.intents.map((intent) => intent.key);
    const enumNames = new Set(registrySnapshot.enums.map((item) => item.name));

    if (input.intent && !intentKeys.includes(input.intent)) {
      return {
        tool: "get_capability_context",
        readOnly: true,
        status: "NotFound",
        code: "UNKNOWN_INTENT",
        snapshot: snapshotIdentity(),
        registryVersion: registrySnapshot.registryVersion,
        source: registrySnapshot.source,
        query: query || null,
        entities: compactEntities(),
        entityFields: [],
        intents: [],
        effects: [],
        conditions: [],
        enums: [],
        counts: {
          entities: registrySnapshot.entities.length,
          entityFields: 0,
          intents: 0,
          effects: 0,
          conditions: 0,
          enums: 0
        },
        truncated: false
      };
    }

    const actionKeys = [
      ...registrySnapshot.effects.map((action) => action.key),
      ...registrySnapshot.conditions.map((action) => action.key)
    ];
    if (input.actionKey && !actionKeys.includes(input.actionKey)) {
      return {
        tool: "get_capability_context",
        readOnly: true,
        status: "NotFound",
        code: "UNKNOWN_ACTION",
        snapshot: snapshotIdentity(),
        registryVersion: registrySnapshot.registryVersion,
        source: registrySnapshot.source,
        query: query || null,
        entities: compactEntities(),
        entityFields: [],
        intents: [],
        effects: [],
        conditions: [],
        enums: [],
        counts: {
          entities: registrySnapshot.entities.length,
          entityFields: 0,
          intents: 0,
          effects: 0,
          conditions: 0,
          enums: 0
        },
        truncated: false
      };
    }

    if (input.enum && !enumNames.has(input.enum)) {
      return {
        tool: "get_capability_context",
        readOnly: true,
        status: "NotFound",
        code: "UNKNOWN_ENUM",
        snapshot: snapshotIdentity(),
        registryVersion: registrySnapshot.registryVersion,
        source: registrySnapshot.source,
        query: query || null,
        entities: compactEntities(),
        entityFields: [],
        intents: [],
        effects: [],
        conditions: [],
        enums: [],
        counts: {
          entities: registrySnapshot.entities.length,
          entityFields: 0,
          intents: 0,
          effects: 0,
          conditions: 0,
          enums: 0
        },
        truncated: false
      };
    }

    const entities = compactEntities();
    const matchedIntents = registrySnapshot.intents
      .filter((intent) => !input.intent || intent.key === input.intent)
      .filter(() => !input.enum)
      .filter(
        (intent) =>
          !query ||
          textContains(intent.key, query) ||
          textContains(intent.label, query)
      );
    const matchedEffects = registrySnapshot.effects
      .filter((action) => !input.actionKey || action.key === input.actionKey)
      .filter(
        (action) =>
          !input.enum ||
          action.parameters.some(
            (parameter) => parameter.enumName === input.enum
          )
      )
      .filter((action) => {
        if (!input.intent) {
          return actionMatches(action, query);
        }
        return action.semantic.intents.includes(input.intent) && actionMatches(action, query);
      });
    const matchedConditions = registrySnapshot.conditions
      .filter((action) => !input.actionKey || action.key === input.actionKey)
      .filter(
        (action) =>
          !input.enum ||
          action.parameters.some(
            (parameter) => parameter.enumName === input.enum
          )
      )
      .filter((action) => {
        if (!input.intent) {
          return actionMatches(action, query);
        }
        return action.semantic.intents.includes(input.intent) && actionMatches(action, query);
      });
    const actionEnumNames = new Set(
      [...matchedEffects, ...matchedConditions]
        .flatMap((action) => action.parameters ?? [])
        .map((parameter) => parameter.enumName)
        .filter(Boolean)
    );
    const matchedEntityFields = registrySnapshot.entityFields
      .filter(
        (field) =>
          !input.enum || field.enumName === input.enum
      )
      .filter(
        (field) =>
          (!input.actionKey && !input.intent) ||
          actionEnumNames.has(field.enumName)
      )
      .filter(
        (field) =>
          !query ||
          textContains(field.path, query) ||
          textContains(field.enumName, query)
      );

    const intents = matchedIntents.slice(0, limit);
    const effects = matchedEffects.slice(0, limit);
    const conditions = matchedConditions.slice(0, limit);
    const entityFields = matchedEntityFields.slice(0, limit);
    const selectedNames = input.enum
      ? [input.enum]
      : selectedEnumNames([...effects, ...conditions], entityFields);
    const enums = enumResults(selectedNames, enumValueLimit);
    const counts = {
      entities: entities.length,
      entityFields: {
        total: matchedEntityFields.length,
        returned: entityFields.length,
        truncated: entityFields.length < matchedEntityFields.length
      },
      intents: {
        total: matchedIntents.length,
        returned: intents.length,
        truncated: intents.length < matchedIntents.length
      },
      effects: {
        total: matchedEffects.length,
        returned: effects.length,
        truncated: effects.length < matchedEffects.length
      },
      conditions: {
        total: matchedConditions.length,
        returned: conditions.length,
        truncated: conditions.length < matchedConditions.length
      },
      enums: {
        total: selectedNames.length,
        returned: enums.length,
        truncated: enums.some((definition) => definition.truncated)
      }
    };
    const truncated = [
      counts.entityFields,
      counts.intents,
      counts.effects,
      counts.conditions,
      counts.enums
    ].some((count) => count.truncated);

    return {
      tool: "get_capability_context",
      readOnly: true,
      status:
        intents.length + effects.length + conditions.length + entityFields.length > 0
          ? "Ready"
          : "NotFound",
      snapshot: snapshotIdentity(),
      registryVersion: registrySnapshot.registryVersion,
      source: registrySnapshot.source,
      query: query || null,
      entities,
      entityFields,
      intents,
      effects,
      conditions,
      enums,
      counts,
      truncated
    };
  }

  function resolveAsset(input = {}) {
    const namespace = input.namespace;
    const limit = parseLimit(input.limit, 10);

    if (typeof namespace !== "string" || namespace.length === 0) {
      return {
        tool: "resolve_asset",
        readOnly: true,
        status: "InvalidRequest",
        code: "NAMESPACE_REQUIRED",
        snapshot: snapshotIdentity()
      };
    }

    if (!entitiesByNamespace.has(namespace)) {
      return {
        tool: "resolve_asset",
        readOnly: true,
        status: "InvalidRequest",
        code: "UNKNOWN_NAMESPACE",
        namespace,
        availableNamespaces: registrySnapshot.entities
          .map((entity) => entity.namespace)
          .sort((left, right) => left.localeCompare(right, "en")),
        snapshot: snapshotIdentity()
      };
    }

    if (input.id !== undefined && input.name !== undefined) {
      return {
        tool: "resolve_asset",
        readOnly: true,
        status: "InvalidRequest",
        code: "ID_AND_NAME_CONFLICT",
        snapshot: snapshotIdentity()
      };
    }

    if (input.id !== undefined) {
      assert(Number.isInteger(input.id), "Asset id must be an integer.");
      const key = `${namespace}:${input.id}`;
      const node = nodesByKey.get(key);
      if (!node) {
        return {
          tool: "resolve_asset",
          readOnly: true,
          status: "NotFound",
          namespace,
          id: input.id,
          snapshot: snapshotIdentity()
        };
      }

      return {
        tool: "resolve_asset",
        readOnly: true,
        status: "Resolved",
        match: "StableId",
        candidateCount: 1,
        returnedCount: 1,
        truncated: false,
        candidates: [stableNode(node)],
        snapshot: snapshotIdentity()
      };
    }

    const name = String(input.name ?? "").trim();
    if (!name) {
      return {
        tool: "resolve_asset",
        readOnly: true,
        status: "InvalidRequest",
        code: "ID_OR_NAME_REQUIRED",
        snapshot: snapshotIdentity()
      };
    }

    const namespaceNodes = snapshot.nodes.filter(
      (node) => node.namespace === namespace
    );
    const exactMatches = namespaceNodes.filter(
      (node) => node.label.toLowerCase() === name.toLowerCase()
    );
    const matches = (
      exactMatches.length > 0
        ? exactMatches
        : namespaceNodes.filter((node) => textContains(node.label, name))
    ).sort((left, right) => left.key.localeCompare(right.key, "en"));
    const candidates = matches.slice(0, limit).map(stableNode);

    if (candidates.length === 0) {
      return {
        tool: "resolve_asset",
        readOnly: true,
        status: "NotFound",
        namespace,
        name,
        snapshot: snapshotIdentity()
      };
    }

    return {
      tool: "resolve_asset",
      readOnly: true,
      status: "Candidates",
      match: "DisplayName",
      namespace,
      name,
      candidateCount: matches.length,
      returnedCount: candidates.length,
      truncated: matches.length > candidates.length,
      candidates,
      snapshot: snapshotIdentity()
    };
  }

  function getGraph(input = {}) {
    const root = input.root;
    const depth = parseDepth(input.depth);
    const direction = parseDirection(input.direction);
    const limit = parseLimit(input.limit, 200);
    const nodeLimit = parseLimit(input.nodeLimit, limit);
    const edgeLimit = parseLimit(input.edgeLimit, limit);

    if (typeof root !== "string" || !nodesByKey.has(root)) {
      return {
        tool: "get_graph",
        readOnly: true,
        status: "NotFound",
        root,
        snapshot: snapshotIdentity()
      };
    }

    const visited = new Set([root]);
    const orderedKeys = [root];
    const selectedEdges = new Map();
    const queue = [{ key: root, depth: 0 }];
    let truncated = false;

    while (queue.length > 0) {
      const current = queue.shift();
      if (current.depth >= depth) {
        continue;
      }

      for (const item of getEdges(current.key, direction)) {
        if (selectedEdges.has(item.sequence)) {
          continue;
        }

        const nextKey =
          item.edge.from === current.key ? item.edge.to : item.edge.from;
        const isNewNode = !visited.has(nextKey);

        if (isNewNode && visited.size >= nodeLimit) {
          truncated = true;
          continue;
        }

        if (selectedEdges.size >= edgeLimit) {
          truncated = true;
          continue;
        }

        selectedEdges.set(item.sequence, item);

        if (isNewNode) {
          visited.add(nextKey);
          orderedKeys.push(nextKey);
          queue.push({ key: nextKey, depth: current.depth + 1 });
        }
      }
    }

    return {
      tool: "get_graph",
      readOnly: true,
      status: truncated ? "Truncated" : "Ready",
      root,
      depth,
      direction,
      limits: {
        maxNodes: nodeLimit,
        maxEdges: edgeLimit
      },
      nodes: orderedKeys.map((key) => stableNode(nodesByKey.get(key))),
      edges: [...selectedEdges.keys()]
        .sort((left, right) => left - right)
        .map((sequence) => stableEdge(snapshot.edges[sequence], sequence)),
      truncated,
      snapshot: snapshotIdentity()
    };
  }

  function searchSimilarSkills(input = {}) {
    const focusKey = input.focus;
    const limit = parseLimit(input.limit, 5);
    const focus = nodesByKey.get(focusKey);

    if (!focus) {
      return {
        tool: "search_similar_skills",
        readOnly: true,
        status: "NotFound",
        code: "ASSET_NOT_FOUND",
        focus: focusKey,
        snapshot: snapshotIdentity()
      };
    }
    if (focus.kind !== skillKind) {
      return {
        tool: "search_similar_skills",
        readOnly: true,
        status: "InvalidRequest",
        code: "NOT_A_SKILL",
        focus: focusKey,
        snapshot: snapshotIdentity()
      };
    }

    const weights = registrySnapshot.similarityScoring.skillWeights;
    const focusNamespaces = [
      ...new Set((outEdges.get(focusKey) ?? []).map((item) => item.edge.to.split(":")[0]))
    ];
    const results = snapshot.nodes
      .filter((node) => node.kind === skillKind && node.key !== focusKey)
      .map((node) => {
        const intent = overlapScore(focus.semantic.intents, node.semantic.intents);
        const mechanism = overlapScore(
          focus.semantic.mechanisms,
          node.semantic.mechanisms
        );
        const tag = overlapScore(focus.semantic.tags, node.semantic.tags);
        const references = overlapScore(
          focusNamespaces,
          [
            ...new Set(
              (outEdges.get(node.key) ?? []).map((item) => item.edge.to.split(":")[0])
            )
          ]
        );
        const score =
          intent.score * weights.intents +
          mechanism.score * weights.mechanisms +
          tag.score * weights.tags +
          references.score * weights.references;
        const reasons = [];
        if (intent.shared.length > 0) {
          reasons.push(`shared intents: ${intent.shared.join(", ")}`);
        }
        if (mechanism.shared.length > 0) {
          reasons.push(`shared mechanisms: ${mechanism.shared.join(", ")}`);
        }
        if (tag.shared.length > 0) {
          reasons.push(`shared tags: ${tag.shared.join(", ")}`);
        }
        if (references.shared.length > 0) {
          reasons.push(`shared references: ${references.shared.join(", ")}`);
        }

        return {
          key: node.key,
          label: node.label,
          score: Number(score.toFixed(4)),
          reasons,
          editLock: node.editLock
        };
      })
      .filter((result) => result.score > 0)
      .sort(
        (left, right) =>
          right.score - left.score || left.key.localeCompare(right.key, "en")
      )
      .slice(0, limit);

    return {
      tool: "search_similar_skills",
      readOnly: true,
      status: results.length > 0 ? "Ready" : "NotFound",
      focus: focusKey,
      scoringVersion: registrySnapshot.similarityScoring.version,
      results,
      snapshot: snapshotIdentity()
    };
  }

  function explainExecutionChain(input = {}) {
    const skillKey = input.skill;
    const limit = parseLimit(input.limit, 100);
    const root = nodesByKey.get(skillKey);

    if (!root) {
      return {
        tool: "explain_execution_chain",
        readOnly: true,
        status: "NotFound",
        code: "ASSET_NOT_FOUND",
        skill: skillKey,
        snapshot: snapshotIdentity()
      };
    }
    if (root.kind !== skillKind) {
      return {
        tool: "explain_execution_chain",
        readOnly: true,
        status: "InvalidRequest",
        code: "NOT_A_SKILL",
        skill: skillKey,
        snapshot: snapshotIdentity()
      };
    }

    const steps = [];
    const unresolvedTargets = [];
    const emittedEdges = new Set();
    const expandedNodes = new Set();
    let cycleDetected = false;
    let truncated = false;

    function walk(key, depth, path) {
      if (expandedNodes.has(key)) {
        return;
      }
      expandedNodes.add(key);

      for (const item of getEdges(key, "out")) {
        if (emittedEdges.has(item.sequence)) {
          continue;
        }
        if (steps.length >= limit) {
          truncated = true;
          return;
        }

        const target = nodesByKey.get(item.edge.to);
        if (!target) {
          unresolvedTargets.push(item.edge.to);
          continue;
        }

        const cycle = path.has(item.edge.to);
        const reusedSubtree = !cycle && expandedNodes.has(item.edge.to);
        emittedEdges.add(item.sequence);
        steps.push({
          sequence: steps.length,
          edgeSequence: item.sequence,
          depth,
          from: key,
          field: item.edge.field,
          parameterIndex: item.edge.parameterIndex ?? null,
          to: item.edge.to,
          toKind: target.kind,
          toLabel: target.label,
          reusedSubtree,
          evidence: item.edge.source
        });

        if (cycle) {
          cycleDetected = true;
          continue;
        }
        if (reusedSubtree) {
          continue;
        }

        const nextPath = new Set(path);
        nextPath.add(item.edge.to);
        walk(item.edge.to, depth + 1, nextPath);
      }
    }

    walk(skillKey, 0, new Set([skillKey]));

    return {
      tool: "explain_execution_chain",
      readOnly: true,
      status:
        truncated || cycleDetected || unresolvedTargets.length > 0
          ? "Partial"
          : "Ready",
      skill: stableNode(root),
      steps,
      summary: {
        stepCount: steps.length,
        maxDepth: steps.reduce((maximum, step) => Math.max(maximum, step.depth), 0),
        reusedSubtreeCount: steps.filter((step) => step.reusedSubtree).length,
        unresolvedTargetCount: unresolvedTargets.length,
        cycleDetected
      },
      unresolvedTargets,
      snapshot: snapshotIdentity()
    };
  }

  return Object.freeze({
    graphVersion: snapshot.graphVersion,
    workspaceId: snapshot.workspaceId,
    revision: snapshot.revision,
    nodeCount: snapshot.nodes.length,
    edgeCount: snapshot.edges.length,
    getCapabilityContext: safeTool(
      "get_capability_context",
      getCapabilityContext
    ),
    resolveAsset: safeTool("resolve_asset", resolveAsset),
    getGraph: safeTool("get_graph", getGraph),
    searchSimilarSkills: safeTool(
      "search_similar_skills",
      searchSimilarSkills
    ),
    explainExecutionChain: safeTool(
      "explain_execution_chain",
      explainExecutionChain
    )
  });
}
import { validateSchema } from "./schema-lite.mjs";
