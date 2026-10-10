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
            TryGetIndex(field, out string baseField, out int parameterIndex)
            && fields.TryGetValue(
                baseField,
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
        if (!TryGetIndex(field, out string baseField, out int parameterIndex))
        {
            fields[field] = [value];
            return;
        }

        List<string> parameters = fields.TryGetValue(
            baseField,
            out IReadOnlyList<string>? existing
        )
            ? existing.ToList()
            : [];
        while (parameters.Count <= parameterIndex)
        {
            parameters.Add("");
        }

        parameters[parameterIndex] = value;
        fields[baseField] = parameters;
    }

    public static bool TryGetIndex(
        string field,
        out string baseField,
        out int parameterIndex
    )
    {
        int bracket = field.LastIndexOf('[');
        baseField = bracket > 0 ? field[..bracket] : field;
        parameterIndex = -1;
        return bracket > 0
            && field.EndsWith(']')
            && int.TryParse(
                field[(bracket + 1)..^1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parameterIndex
            ) && parameterIndex >= 0;
    }
}
