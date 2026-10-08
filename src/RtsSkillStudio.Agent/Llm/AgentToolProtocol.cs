using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Net;

namespace RtsSkillStudio.Agent.Llm;

public sealed record AgentToolDefinition(
    string Name,
    string Description,
    string InputSchemaJson
);

public sealed record AgentToolCall(
    string Name,
    JsonNode? Arguments
);

public sealed record AgentToolExecution(
    string Name,
    string ArgumentsJson,
    string ResultJson,
    bool IsError
);

public sealed record AgentAssetIdentity(
    string Key,
    string Label,
    string? Name
)
{
    public string CanonicalName =>
        string.IsNullOrWhiteSpace(Name) ? Label : Name;
}

public static partial class AgentToolProtocol
{
    private const string EnvelopeType = "skill_studio_tool_calls";

    public static string BuildInstructions(
        IReadOnlyList<AgentToolDefinition> tools
    )
    {
        var lines = new List<string>
        {
            "【只读工具循环】",
            "需要事实、引用、字段或链路证据时，只能请求下列只读工具。",
            "需要调用工具时，回复末尾只输出一个 json 代码块，结构必须是：",
            """{"type":"skill_studio_tool_calls","calls":[{"name":"get_graph","arguments":{"root":"TbSkill:100101","depth":32}}]}""",
            "一次最多请求 3 个工具。工具执行后你会收到 TOOL_RESULTS，再继续判断或给出最终回答。",
            "证据足够时直接正常回答，不要继续请求工具。",
            "可用工具:"
        };

        foreach (AgentToolDefinition tool in tools)
        {
            lines.Add($"- {tool.Name}: {tool.Description}");
            lines.Add($"  inputSchema: {tool.InputSchemaJson}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static AgentAssetIdentity? FindAuthoritativeIdentity(
        IEnumerable<AgentToolExecution> executions
    )
    {
        foreach (
            AgentToolExecution execution in executions
                .Where(
                    execution =>
                        !execution.IsError
                        && string.Equals(
                            execution.Name,
                            "get_graph",
                            StringComparison.Ordinal
                        )
                )
                .Reverse()
        )
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    execution.ResultJson
                );
                if (
                    !TryGetProperty(
                        document.RootElement,
                        "nodes",
                        out JsonElement nodes
                    )
                    || nodes.ValueKind != JsonValueKind.Array
                )
                {
                    continue;
                }

                JsonElement? focus = nodes
                    .EnumerateArray()
                    .Cast<JsonElement?>()
                    .FirstOrDefault(
                        node =>
                            node is { ValueKind: JsonValueKind.Object } value
                            && TryGetProperty(
                                value,
                                "isFocus",
                                out JsonElement isFocus
                            )
                            && isFocus.ValueKind == JsonValueKind.True
                    );
                if (
                    focus is not { ValueKind: JsonValueKind.Object } focusNode
                )
                {
                    continue;
                }

                string key = ReadString(focusNode, "key") ?? "";
                string label = ReadString(focusNode, "label") ?? "";
                string? name = ReadFirstFieldValue(
                    focusNode,
                    "name",
                    "__remark_2"
                );
                if (
                    string.IsNullOrWhiteSpace(key)
                    || (
                        string.IsNullOrWhiteSpace(label)
                        && string.IsNullOrWhiteSpace(name)
                    )
                )
                {
                    continue;
                }

                return new AgentAssetIdentity(
                    key,
                    string.IsNullOrWhiteSpace(label) ? name! : label,
                    name
                );
            }
            catch (JsonException)
            {
                // Ignore malformed tool output and keep looking.
            }
        }

        return null;
    }

    public static IReadOnlyList<AgentAssetIdentity> FindReferencedIdentities(
        IEnumerable<AgentToolExecution> executions
    )
    {
        var identities = new Dictionary<string, AgentAssetIdentity>(
            StringComparer.Ordinal
        );
        foreach (
            AgentToolExecution execution in executions.Where(
                execution =>
                    !execution.IsError
                    && string.Equals(
                        execution.Name,
                        "get_graph",
                        StringComparison.Ordinal
                    )
            )
        )
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    execution.ResultJson
                );
                if (
                    !TryGetProperty(
                        document.RootElement,
                        "nodes",
                        out JsonElement nodes
                    )
                    || nodes.ValueKind != JsonValueKind.Array
                )
                {
                    continue;
                }

                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    string key = ReadString(node, "key") ?? "";
                    string label = ReadString(node, "label") ?? "";
                    string? name = ReadFirstFieldValue(
                        node,
                        "name",
                        "__remark_2",
                        "__remark_3"
                    );
                    if (
                        string.IsNullOrWhiteSpace(key)
                        || (
                            string.IsNullOrWhiteSpace(label)
                            && string.IsNullOrWhiteSpace(name)
                        )
                    )
                    {
                        continue;
                    }

                    identities[key] = new AgentAssetIdentity(
                        key,
                        string.IsNullOrWhiteSpace(label) ? name! : label,
                        name
                    );
                }
            }
            catch (JsonException)
            {
                // Ignore malformed tool output.
            }
        }

        return identities.Values.ToArray();
    }

    public static IReadOnlyList<string> FindExecutorActionKeys(
        IEnumerable<AgentToolExecution> executions,
        int limit = 3
    )
    {
        var actionKeys = new List<string>();
        foreach (
            AgentToolExecution execution in executions
                .Where(
                    execution =>
                        !execution.IsError
                        && string.Equals(
                            execution.Name,
                            "get_graph",
                            StringComparison.Ordinal
                        )
                )
        )
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    execution.ResultJson
                );
                if (
                    !TryGetProperty(
                        document.RootElement,
                        "nodes",
                        out JsonElement nodes
                    )
                    || nodes.ValueKind != JsonValueKind.Array
                )
                {
                    continue;
                }

                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    string? executor = ReadFirstFieldValue(
                        node,
                        "__executor"
                    );
                    string? actionKey = ExtractActionKey(executor);
                    if (
                        string.IsNullOrWhiteSpace(actionKey)
                        || actionKeys.Contains(
                            actionKey,
                            StringComparer.OrdinalIgnoreCase
                        )
                    )
                    {
                        continue;
                    }

                    actionKeys.Add(actionKey);
                    if (actionKeys.Count >= limit)
                    {
                        return actionKeys;
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed tool output and keep looking.
            }
        }

        return actionKeys;
    }

    public static bool HasParameterContractEvidence(
        IEnumerable<AgentToolExecution> executions
    )
    {
        foreach (
            AgentToolExecution execution in executions.Where(
                execution =>
                    !execution.IsError
                    && string.Equals(
                        execution.Name,
                        "get_capability_context",
                        StringComparison.Ordinal
                    )
            )
        )
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    execution.ResultJson
                );
                if (
                    !TryGetProperty(
                        document.RootElement,
                        "effects",
                        out JsonElement effects
                    )
                    || effects.ValueKind != JsonValueKind.Array
                )
                {
                    continue;
                }

                if (
                    effects.EnumerateArray().Any(
                        effect =>
                            TryGetProperty(
                                effect,
                                "parameters",
                                out JsonElement parameters
                            )
                            && parameters.ValueKind == JsonValueKind.Array
                            && parameters.GetArrayLength() > 0
                    )
                )
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Ignore malformed tool output and keep looking.
            }
        }

        return false;
    }

    public static string BuildIdentityInstructions(
        AgentAssetIdentity? identity
    )
    {
        if (identity is null)
        {
            return "";
        }

        return string.Join(
            Environment.NewLine,
            "【权威资产身份】",
            $"assetKey: {identity.Key}",
            $"label: {identity.Label}",
            $"name: {identity.Name ?? "<not provided>"}",
            "回答的标题、名称字段和 __remark_2 字段只要出现名称，就必须逐字使用 label 或 name 的原文。",
            $"本次必须原样包含：{identity.CanonicalName}",
            "禁止翻译、音译、改写、缩写、替换同义词，或根据模型记忆补全另一个名称。",
            "如果 label 和 name 不同，分别原样列出，不得合成新名称。"
        );
    }

    public static bool TextContainsAuthoritativeName(
        string text,
        AgentAssetIdentity identity
    )
    {
        string normalizedText = NormalizeNameForComparison(text);
        return IsNamePresent(normalizedText, identity.Label)
            || IsNamePresent(normalizedText, identity.Name);
    }

    public static string BuildIdentityRepairInstructions(
        AgentAssetIdentity identity
    )
    {
        return string.Join(
            Environment.NewLine,
            "【名称一致性修正】",
            "上一版回答没有逐字包含当前资产的权威名称。",
            $"权威 assetKey: {identity.Key}",
            $"权威 label: {identity.Label}",
            $"权威 name: {identity.Name ?? "<not provided>"}",
            $"必须逐字写入标题和名称字段：{identity.CanonicalName}",
            "请基于已经收到的 TOOL_RESULTS 重新输出完整回答。",
            "提及该资产时必须逐字复制上述 label 或 name，不得继续使用上一版中的近似名称、翻译名或模型记忆名称。"
        );
    }

    public static string NormalizeAssetIdentityText(
        string text,
        AgentAssetIdentity identity
    )
    {
        string normalized = DecodeUnicodeEscapes(text);
        normalized = IdentityHeadingRegex().Replace(
            normalized,
            match =>
                match.Groups["prefix"].Value
                + identity.CanonicalName
        );
        normalized = IdentityNameLineRegex().Replace(
            normalized,
            match =>
                match.Groups["prefix"].Value
                + identity.CanonicalName
        );
        normalized = IdentityNameTableRegex().Replace(
            normalized,
            match =>
                match.Groups["prefix"].Value
                + identity.CanonicalName
                + match.Groups["suffix"].Value
        );
        normalized = Regex.Replace(
            normalized,
            $@"\*\*[^\r\n*]*{Regex.Escape(identity.Key)}[^\r\n*]*\*\*",
            $"**{identity.CanonicalName} (`{identity.Key}`)**",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );

        if (TextContainsAuthoritativeName(normalized, identity))
        {
            return normalized;
        }

        return $"**权威资产名称（Excel 证据）**：`{identity.CanonicalName}`"
            + Environment.NewLine
            + Environment.NewLine
            + normalized;
    }

    public static string NormalizeReferencedIdentityText(
        string text,
        IEnumerable<AgentAssetIdentity> identities
    )
    {
        string normalized = text;
        foreach (AgentAssetIdentity identity in identities)
        {
            int keySeparator = identity.Key.LastIndexOf(':');
            string spacedKey = keySeparator > 0
                ? identity.Key[..keySeparator]
                    + ": "
                    + identity.Key[(keySeparator + 1)..]
                : identity.Key;
            if (
                !normalized.Contains(
                    identity.Key,
                    StringComparison.OrdinalIgnoreCase
                )
                && !normalized.Contains(
                    spacedKey,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            int separator = keySeparator;
            if (separator <= 0)
            {
                continue;
            }

            string keyPattern =
                Regex.Escape(identity.Key[..separator])
                + @"\s*:\s*"
                + Regex.Escape(identity.Key[(separator + 1)..]);
            normalized = Regex.Replace(
                normalized,
                $@"\*\*[^*\r\n]{{1,80}}\*\*\s*(?<key>[（(]\s*{keyPattern}\s*[）)])",
                match =>
                    identity.CanonicalName
                    + " "
                    + match.Groups["key"].Value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
            normalized = Regex.Replace(
                normalized,
                $@"(?<name>[\p{{IsCJKUnifiedIdeographs}}A-Za-z0-9_·\- \*]{{2,80}}?)\s*(?<key>[（(]\s*{keyPattern}\s*[）)])",
                match =>
                    identity.CanonicalName
                    + " "
                    + match.Groups["key"].Value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
        }

        return normalized;
    }

    public static string BuildFinalInstructions()
    {
        return string.Join(
            Environment.NewLine,
            "【最终回答】",
            "只读工具轮次已经用尽，不能再请求工具。",
            "请根据已经收到的 TOOL_RESULTS 直接给出最终回答。",
            "不要输出 skill_studio_tool_calls JSON 块。",
            "如果证据不足，请明确说明缺少什么，不要补造步骤、ID 或值。"
        );
    }

    public static IReadOnlyList<AgentToolCall> Parse(string text)
    {
        foreach (Match match in JsonFenceRegex().Matches(text).Reverse())
        {
            string json = match.Groups["json"].Value.Trim();
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (
                    root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out JsonElement type)
                    || type.ValueKind != JsonValueKind.String
                    || !string.Equals(
                        type.GetString(),
                        EnvelopeType,
                        StringComparison.Ordinal
                    )
                    || !root.TryGetProperty("calls", out JsonElement calls)
                    || calls.ValueKind != JsonValueKind.Array
                )
                {
                    continue;
                }

                var result = new List<AgentToolCall>();
                foreach (JsonElement call in calls.EnumerateArray())
                {
                    if (
                        call.ValueKind != JsonValueKind.Object
                        || !call.TryGetProperty("name", out JsonElement name)
                        || name.ValueKind != JsonValueKind.String
                    )
                    {
                        continue;
                    }

                    JsonNode? arguments = call.TryGetProperty(
                        "arguments",
                        out JsonElement argumentsElement
                    )
                        ? JsonNode.Parse(argumentsElement.GetRawText())
                        : new JsonObject();
                    result.Add(
                        new AgentToolCall(
                            name.GetString() ?? "",
                            arguments
                        )
                    );
                }
                return result;
            }
            catch (JsonException)
            {
                // Try the previous JSON fence.
            }
        }

        return ParseDsml(text);
    }

    private static string? ExtractActionKey(string? executor)
    {
        if (string.IsNullOrWhiteSpace(executor))
        {
            return null;
        }

        return executor
            .Split('·', StringSplitOptions.TrimEntries)
            .FirstOrDefault(
                part =>
                    part.Length > 1
                    && char.IsAsciiLetter(part[0])
                    && part.All(
                        character =>
                            char.IsAsciiLetterOrDigit(character)
                            || character == '_'
                    )
            );
    }

    private static string? ReadFirstFieldValue(
        JsonElement node,
        params string[] propertyNames
    )
    {
        if (
            !TryGetProperty(node, "fields", out JsonElement fields)
            || fields.ValueKind != JsonValueKind.Object
        )
        {
            return null;
        }

        foreach (string propertyName in propertyNames)
        {
            if (
                !TryGetProperty(
                    fields,
                    propertyName,
                    out JsonElement value
                )
                || value.ValueKind != JsonValueKind.Array
            )
            {
                continue;
            }

            string? first = value
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement node, string propertyName)
    {
        return TryGetProperty(node, propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetProperty(
        JsonElement node,
        string propertyName,
        out JsonElement value
    )
    {
        if (node.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (JsonProperty property in node.EnumerateObject())
        {
            if (
                string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsNamePresent(
        string normalizedText,
        string? candidate
    )
    {
        return !string.IsNullOrWhiteSpace(candidate)
            && normalizedText.Contains(
                NormalizeNameForComparison(candidate),
                StringComparison.Ordinal
            );
    }

    private static string NormalizeNameForComparison(string value)
    {
        return NameSeparatorRegex().Replace(value, "");
    }

    private static string DecodeUnicodeEscapes(string value)
    {
        return UnicodeEscapeRegex().Replace(
            value,
            match =>
            {
                string hex = match.Groups["hex"].Value;
                return char.ConvertFromUtf32(
                    Convert.ToInt32(hex, 16)
                );
            }
        );
    }

    public static string RemoveToolCallBlock(string text)
    {
        string withoutJson = JsonFenceRegex().Replace(
            text,
            match =>
            {
                string json = match.Groups["json"].Value.Trim();
                try
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    return document.RootElement.TryGetProperty(
                            "type",
                            out JsonElement type
                        )
                        && type.ValueKind == JsonValueKind.String
                        && string.Equals(
                            type.GetString(),
                            EnvelopeType,
                            StringComparison.Ordinal
                        )
                        ? ""
                        : match.Value;
                }
                catch (JsonException)
                {
                    return match.Value;
                }
            }
        ).Trim();
        string normalized = withoutJson.Replace('\uFF5C', '|');
        return DsmlCallsRegex().Replace(normalized, "").Trim();
    }

    public static string FormatToolResults(
        IReadOnlyList<AgentToolExecution> executions
    )
    {
        var rows = executions.Select(
            execution => new
            {
                name = execution.Name,
                arguments = ParseJsonOrString(execution.ArgumentsJson),
                isError = execution.IsError,
                result = ParseJsonOrString(execution.ResultJson)
            }
        );
        return "TOOL_RESULTS"
            + Environment.NewLine
            + JsonSerializer.Serialize(
                rows,
                new JsonSerializerOptions { WriteIndented = false }
            );
    }

    private static object? ParseJsonOrString(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<object>(json);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static IReadOnlyList<AgentToolCall> ParseDsml(string text)
    {
        string normalized = text.Replace('\uFF5C', '|');
        var calls = new List<AgentToolCall>();
        foreach (Match callsMatch in DsmlCallsRegex().Matches(normalized))
        {
            foreach (
                Match invokeMatch in DsmlInvokeRegex().Matches(
                    callsMatch.Groups["body"].Value
                )
            )
            {
                var parameters = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase
                );
                foreach (
                    Match parameterMatch in DsmlParameterRegex().Matches(
                        invokeMatch.Groups["body"].Value
                    )
                )
                {
                    parameters[parameterMatch.Groups["name"].Value] =
                        WebUtility.HtmlDecode(
                            parameterMatch.Groups["value"].Value.Trim()
                        );
                }

                string name = parameters.GetValueOrDefault(
                    "name",
                    invokeMatch.Groups["name"].Value
                );
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                JsonNode? arguments = null;
                if (
                    parameters.TryGetValue(
                        "arguments",
                        out string? argumentsText
                    )
                    && !string.IsNullOrWhiteSpace(argumentsText)
                )
                {
                    try
                    {
                        arguments = JsonNode.Parse(argumentsText);
                    }
                    catch (JsonException)
                    {
                        arguments = JsonValue.Create(argumentsText);
                    }
                }

                if (arguments is null)
                {
                    var obj = new JsonObject();
                    foreach (
                        KeyValuePair<string, string> parameter in parameters
                            .Where(
                                parameter =>
                                    !string.Equals(
                                        parameter.Key,
                                        "name",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                            )
                    )
                    {
                        obj[parameter.Key] = JsonValue.Create(
                            parameter.Value
                        );
                    }
                    arguments = obj;
                }

                calls.Add(new AgentToolCall(name, arguments));
            }
        }

        return calls;
    }

    [GeneratedRegex(
        @"```json\s*(?<json>.*?)\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    )]
    private static partial Regex JsonFenceRegex();

    [GeneratedRegex(@"[\s\-_·—–:：,，.。/\\()（）\[\]【】]+")]
    private static partial Regex NameSeparatorRegex();

    [GeneratedRegex(
        @"(?m)^(?<prefix>#{1,6}\s+(?:(?!Tb[A-Za-z]+:)[^\r\n：:])*技能(?:(?!Tb[A-Za-z]+:)[^\r\n：:])*[：:]\s*)(?<value>.+)$"
    )]
    private static partial Regex IdentityHeadingRegex();

    [GeneratedRegex(
        @"(?im)^(?<prefix>\s*(?:[-*]\s*)?`?(?:\*\*)?(?:名称|技能名称|别名|备注|__remark_2|label|name)(?:\*\*)?`?[^:*|\r\n]*?[:：](?:\*\*)?\s*)(?<value>[^\r\n]+)$"
    )]
    private static partial Regex IdentityNameLineRegex();

    [GeneratedRegex(
        @"(?im)^(?<prefix>\s*\|\s*`?(?:\*\*)?\s*(?:名称|技能名称|别名|备注|__remark_2|label|name)\s*(?:\*\*)?`?[^|]*\|\s*)(?<value>[^|]+?)(?<suffix>\s*\|.*)$"
    )]
    private static partial Regex IdentityNameTableRegex();

    [GeneratedRegex(@"\\u(?<hex>[0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscapeRegex();

    [GeneratedRegex(
        @"<\|+\s*DSML\s*\|+\s*calls>(?<body>.*?)</\|+\s*DSML\s*\|+\s*calls>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    )]
    private static partial Regex DsmlCallsRegex();

    [GeneratedRegex(
        @"<\|+\s*DSML\s*\|+\s*invoke\s+name=""(?<name>[^""]+)"">(?<body>.*?)</\|+\s*DSML\s*\|+\s*invoke>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    )]
    private static partial Regex DsmlInvokeRegex();

    [GeneratedRegex(
        @"<\|+\s*DSML\s*\|+\s*parameter\s+name=""(?<name>[^""]+)""[^>]*>(?<value>.*?)</\|+\s*DSML\s*\|+\s*parameter>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    )]
    private static partial Regex DsmlParameterRegex();
}
