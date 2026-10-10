using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TianshuDM.Domain.GameData;

namespace RtsSkillStudio.Agent.Patch;

/// <summary>Versioned value encodings shared by editing and creation. No workbook
/// values or action names select an encoding.</summary>
internal sealed class WorkbookSemanticValueCodec(WorkbookPatchRegistry registry, WorkbookPatchWorkspace workspace)
{
    public static bool IsList(WorkbookPatchField field) => field.Kind is GameDataFieldKind.List or GameDataFieldKind.DelimitedList;

    public WorkbookPatchField Element(WorkbookPatchField field)
    {
        if (!IsList(field)) return field;
        string type = field.RawType.StartsWith("array,", StringComparison.OrdinalIgnoreCase)
            ? field.RawType[6..].Trim() : field.RawType.Split(',').Last().TrimEnd(')').Trim();
        GameDataFieldKind kind = field.ReferenceTarget is not null ? GameDataFieldKind.Reference
            : field.Options.Count > 0 || Enum(type) is not null ? GameDataFieldKind.Enum
            : type.StartsWith("int", StringComparison.OrdinalIgnoreCase) ? GameDataFieldKind.Integer
            : type == "bool" ? GameDataFieldKind.Boolean : GameDataFieldKind.Text;
        if (kind == GameDataFieldKind.Text && !type.StartsWith("string", StringComparison.OrdinalIgnoreCase) && Nested(type) is null)
            throw new InvalidDataException($"类型 {type} 未声明编码。");
        return field with { Kind = kind, RawType = type, Required = true,
            Options = field.Options.Count > 0 ? field.Options : Options(type) };
    }

    public string Scalar(JsonNode? value, string? unit, WorkbookPatchField field)
    {
        if (field.ReferenceTarget is not null || field.Kind == GameDataFieldKind.Reference)
        {
            if (!WorkbookPatchReference.TryResolveTarget(field, out string ns, out _))
                throw new InvalidDataException($"{field.Path} 引用契约缺失或不一致。");
            if (value is not JsonObject target || target["binding"]?.GetValue<string>() != "Existing"
                || !WorkbookPatchReference.NamespaceMatches(target["namespace"]?.GetValue<string>() ?? "", ns))
                throw new InvalidDataException($"{field.Path} 需要 {ns} 的 Existing 引用。");
            int id = target["groupKey"]?.GetValue<int>() ?? target["id"]?.GetValue<int>() ?? 0;
            JsonObject? group = Definitions().SingleOrDefault(item => Text(item, "groupNamespace") == ns);
            bool exists = group is null
                ? workspace.Tables.Any(table => table.Namespace == ns && table.Records.Any(record => record.Id == id))
                : workspace.Tables.Any(table => table.Namespace == Text(group, "namespace") && table.Records.Any(record =>
                    (record.Fields.GetValueOrDefault(Text(group, "groupField")!) ?? []).Contains(id.ToString(CultureInfo.InvariantCulture))));
            if (id <= 0 || !exists) throw new InvalidDataException($"引用 {ns}:{id} 不存在。");
            return id.ToString(CultureInfo.InvariantCulture);
        }
        if (Nested(field.RawType) is { } nested)
        {
            JsonObject input = value as JsonObject ?? throw new InvalidDataException($"{field.Path} 需要 {field.RawType} 对象。");
            JsonObject encoding = nested["encoding"]!.AsObject();
            if (Text(encoding, "kind") != "Delimited") throw new InvalidDataException("未支持的嵌套编码。");
            JsonObject[] members = nested["fields"]!.AsArray().OfType<JsonObject>().ToArray();
            if (input.Count != members.Length || input.Any(pair => !members.Any(member => Text(member, "key") == pair.Key)))
                throw new InvalidDataException($"{field.RawType} 字段不完整或含未知字段。");
            return string.Join(Text(encoding, "separator"), encoding["fields"]!.AsArray().Select(key =>
            {
                string name = key!.GetValue<string>();
                JsonObject member = members.Single(member => Text(member, "key") == name);
                bool isEnum = Text(member, "kind") == "Enum";
                var projected = new WorkbookPatchField(name, field.Path + "." + name, name,
                    isEnum ? GameDataFieldKind.Enum : GameDataFieldKind.Integer, Text(member, "kind")!, true,
                    member["scale"]?.GetValue<decimal>() ?? 1, null, null, null,
                    isEnum ? Options(Text(member, "enumName")!) : [], null);
                return Scalar(input[name], null, projected);
            }));
        }
        if (!WorkbookPatchValueConverter.TryConvert(JsonSerializer.SerializeToElement(value), unit, field,
            registry.ConversionRules, out string raw, out WorkbookPatchValueFailure? failure))
            throw new InvalidDataException(failure?.Message ?? $"无法转换 {field.Path}。");
        return raw;
    }

    public List<string> Map(JsonNode? value, WorkbookPatchField field)
    {
        if (registry.Creation?["editing"]?["mapEncoding"]?.GetValue<string>() != "AlternatingColumns")
            throw new InvalidDataException("未发布 map 编码契约。");
        JsonObject input = value as JsonObject ?? throw new InvalidDataException($"{field.Path} 需要键值对象。");
        string[] types = field.RawType.Split(',').Select(type => type.Trim()).ToArray();
        if (types.Length != 3 || types[0] != "map") throw new InvalidDataException("未支持的 map 类型。");
        WorkbookPatchField Typed(string type) => field with { Kind = Enum(type) is not null ? GameDataFieldKind.Enum
            : type.StartsWith("int", StringComparison.OrdinalIgnoreCase) ? GameDataFieldKind.Integer
            : type == "string" ? GameDataFieldKind.Text : throw new InvalidDataException($"map 类型 {type} 未声明编码。"),
            RawType = type, ReferenceTarget = null, ReferenceNamespace = null, Options = Options(type) };
        var encoded = input.Select(pair => (Key: Scalar(JsonValue.Create(pair.Key), null, Typed(types[1]) with { Scale = 1, Unit = null }),
            Value: Scalar(pair.Value, null, Typed(types[2])))).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        if (encoded.Select(pair => pair.Key).Distinct().Count() != encoded.Length) throw new InvalidDataException("map 含重复的归一化键。");
        return encoded.SelectMany(pair => new[] { pair.Key, pair.Value }).ToList();
    }

    public JsonObject[] Definitions() => (registry.Creation?["entities"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
    private JsonObject? Nested(string type) => (registry.Creation?["nestedTypes"] as JsonArray ?? []).OfType<JsonObject>().SingleOrDefault(item => Text(item, "key") == type);
    private JsonObject? Enum(string type) => (registry.Creation?["enums"] as JsonArray ?? []).OfType<JsonObject>().SingleOrDefault(item => Text(item, "name") == type);
    private IReadOnlyList<GameDataOption> Options(string type) => (Enum(type)?["values"] as JsonArray ?? []).OfType<JsonObject>().Select(item =>
        new GameDataOption(item["value"]!.ToJsonString().Trim('"'), Text(item, "name") ?? "", Text(item, "name"))).ToArray();
    private static string? Text(JsonObject item, string key) => item[key]?.GetValue<string>();
}
