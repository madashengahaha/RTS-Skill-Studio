function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function typeMatches(type, value) {
  switch (type) {
    case "object":
      return isObject(value);
    case "array":
      return Array.isArray(value);
    case "string":
      return typeof value === "string";
    case "integer":
      return Number.isInteger(value);
    case "number":
      return typeof value === "number" && Number.isFinite(value);
    case "boolean":
      return typeof value === "boolean";
    case "null":
      return value === null;
    default:
      return true;
  }
}

export function validateSchema(schema, value, rootSchema = schema, path = "$") {
  const errors = [];

  if (schema.$ref) {
    const prefix = "#/$defs/";
    if (!schema.$ref.startsWith(prefix)) {
      errors.push(`${path}: unsupported $ref ${schema.$ref}`);
      return errors;
    }
    const name = schema.$ref.slice(prefix.length);
    const target = rootSchema.$defs?.[name];
    if (!target) {
      errors.push(`${path}: unresolved $ref ${schema.$ref}`);
      return errors;
    }
    return validateSchema(target, value, rootSchema, path);
  }

  if (schema.const !== undefined && value !== schema.const) {
    errors.push(`${path}: expected const ${JSON.stringify(schema.const)}`);
  }

  if (Array.isArray(schema.enum) && !schema.enum.includes(value)) {
    errors.push(`${path}: value is not in enum`);
  }

  if (schema.type !== undefined) {
    const types = Array.isArray(schema.type) ? schema.type : [schema.type];
    if (!types.some((type) => typeMatches(type, value))) {
      errors.push(`${path}: expected type ${types.join("|")}`);
      return errors;
    }
  }

  if (typeof value === "string") {
    if (schema.minLength !== undefined && value.length < schema.minLength) {
      errors.push(`${path}: string is shorter than ${schema.minLength}`);
    }
    if (schema.pattern !== undefined && !new RegExp(schema.pattern).test(value)) {
      errors.push(`${path}: string does not match ${schema.pattern}`);
    }
  }

  if (typeof value === "number") {
    if (schema.minimum !== undefined && value < schema.minimum) {
      errors.push(`${path}: number is below ${schema.minimum}`);
    }
    if (schema.maximum !== undefined && value > schema.maximum) {
      errors.push(`${path}: number is above ${schema.maximum}`);
    }
  }

  if (Array.isArray(value)) {
    if (schema.minItems !== undefined && value.length < schema.minItems) {
      errors.push(`${path}: array has fewer than ${schema.minItems} items`);
    }
    if (schema.maxItems !== undefined && value.length > schema.maxItems) {
      errors.push(`${path}: array has more than ${schema.maxItems} items`);
    }
    if (schema.uniqueItems) {
      const unique = new Set(value.map((item) => JSON.stringify(item)));
      if (unique.size !== value.length) {
        errors.push(`${path}: array items are not unique`);
      }
    }
    if (schema.items) {
      value.forEach((item, index) => {
        errors.push(
          ...validateSchema(schema.items, item, rootSchema, `${path}[${index}]`)
        );
      });
    }
  }

  if (isObject(value)) {
    for (const required of schema.required ?? []) {
      if (!Object.hasOwn(value, required)) {
        errors.push(`${path}.${required}: required property is missing`);
      }
    }

    if (schema.properties) {
      for (const [key, childSchema] of Object.entries(schema.properties)) {
        if (Object.hasOwn(value, key)) {
          errors.push(
            ...validateSchema(childSchema, value[key], rootSchema, `${path}.${key}`)
          );
        }
      }
    }

    if (schema.additionalProperties === false) {
      const allowed = new Set(Object.keys(schema.properties ?? {}));
      for (const key of Object.keys(value)) {
        if (!allowed.has(key)) {
          errors.push(`${path}.${key}: additional property is not allowed`);
        }
      }
    }
  }

  for (const item of schema.allOf ?? []) {
    errors.push(...validateSchema(item, value, rootSchema, path));
  }

  if (Array.isArray(schema.oneOf)) {
    const matches = schema.oneOf.filter(
      (candidate) => validateSchema(candidate, value, rootSchema, path).length === 0
    );
    if (matches.length !== 1) {
      errors.push(`${path}: expected exactly one oneOf branch`);
    }
  }

  return errors;
}
