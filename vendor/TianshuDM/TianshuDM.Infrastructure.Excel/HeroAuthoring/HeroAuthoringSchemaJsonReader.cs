using System.Text.Json;
using System.Text.Json.Serialization;
using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Infrastructure.Excel.HeroAuthoring;

public sealed class HeroAuthoringSchemaJsonReader(string path) : IHeroAuthoringSemanticSchemaSource
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public HeroAuthoringSemanticSchema Read()
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到英雄配置语义注册表。", path);
        }

        HeroAuthoringSemanticSchema schema;
        try
        {
            schema = JsonSerializer.Deserialize<HeroAuthoringSemanticSchema>(
                         File.ReadAllText(path),
                         SerializerOptions)
                     ?? throw new InvalidDataException("英雄配置语义注册表内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"英雄配置语义注册表 JSON 无效：{exception.Message}", exception);
        }

        IReadOnlyList<HeroAuthoringSchemaIssue> issues = HeroAuthoringSchemaValidator.Validate(schema);
        if (issues.Any(issue => issue.Severity == HeroAuthoringIssueSeverity.Error))
        {
            throw new InvalidDataException(
                "英雄配置语义注册表无效：" +
                string.Join("；", issues.Select(issue => issue.Message)));
        }

        return schema;
    }
}
