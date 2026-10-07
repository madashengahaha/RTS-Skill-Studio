using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtsSkillStudio.Agent.Patch;

public static class WorkbookPatchJsonUtilities
{
    public static string Canonicalize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonNode node = JsonNode.Parse(document.RootElement.GetRawText())
            ?? throw new JsonException("JSON 根节点不能为空。");
        return Canonicalize(node);
    }

    public static string CanonicalizeWithoutProperty(
        string json,
        string propertyName
    )
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonNode node = JsonNode.Parse(document.RootElement.GetRawText())
            ?? throw new JsonException("JSON 根节点不能为空。");
        if (node is JsonObject value)
        {
            value.Remove(propertyName);
        }

        return Canonicalize(node);
    }

    public static string Sha256(string value)
    {
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value))
            )
            .ToLowerInvariant();
    }

    public static string ComputePatchId(WorkbookPatchDocument patch)
    {
        string json = WorkbookPatchJson.Serialize(patch);
        return Sha256(
            CanonicalizeWithoutProperty(json, "patchId")
        );
    }

    public static string ComputePlanHash(string planJson)
    {
        return Sha256(Canonicalize(planJson));
    }

    private static string Canonicalize(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonObject value)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            bool first = true;
            foreach (
                KeyValuePair<string, JsonNode?> property in value
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
            )
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(JsonSerializer.Serialize(property.Key));
                builder.Append(':');
                builder.Append(Canonicalize(property.Value));
            }

            builder.Append('}');
            return builder.ToString();
        }

        if (node is JsonArray array)
        {
            return "["
                + string.Join(",", array.Select(Canonicalize))
                + "]";
        }

        return node.ToJsonString();
    }
}
