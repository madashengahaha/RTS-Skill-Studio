using System.Globalization;

namespace RtsSkillStudio.Api.Workspaces;

public static class WorkbookPatchFieldAccessor
{
    public static string Read(
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields,
        string field
    )
    {
        if (
            TryGetActionParameterIndex(field, out int parameterIndex)
            && fields.TryGetValue(
                "action_param",
                out IReadOnlyList<string>? parameters
            )
            && parameterIndex >= 0
            && parameterIndex < parameters.Count
        )
        {
            return parameters[parameterIndex];
        }

        return fields.TryGetValue(
            field,
            out IReadOnlyList<string>? values
        )
            ? values.FirstOrDefault() ?? ""
            : "";
    }

    public static void Apply(
        IDictionary<string, IReadOnlyList<string>> fields,
        string field,
        string value
    )
    {
        if (!TryGetActionParameterIndex(field, out int parameterIndex))
        {
            fields[field] = [value];
            return;
        }

        List<string> parameters = fields.TryGetValue(
            "action_param",
            out IReadOnlyList<string>? existing
        )
            ? existing.ToList()
            : [];
        while (parameters.Count <= parameterIndex)
        {
            parameters.Add("");
        }

        parameters[parameterIndex] = value;
        fields["action_param"] = parameters;
    }

    private static bool TryGetActionParameterIndex(
        string field,
        out int parameterIndex
    )
    {
        const string prefix = "action_param[";
        parameterIndex = -1;
        return field.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && field.EndsWith(']')
            && int.TryParse(
                field[prefix.Length..^1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parameterIndex
            );
    }
}
