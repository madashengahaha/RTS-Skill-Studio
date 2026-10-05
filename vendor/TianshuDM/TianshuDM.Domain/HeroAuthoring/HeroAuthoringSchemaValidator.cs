namespace TianshuDM.Domain.HeroAuthoring;

public static class HeroAuthoringSchemaValidator
{
    public static IReadOnlyList<HeroAuthoringSchemaIssue> Validate(HeroAuthoringSemanticSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var issues = new List<HeroAuthoringSchemaIssue>();
        if (schema.Version <= 0)
        {
            issues.Add(new HeroAuthoringSchemaIssue("INVALID_VERSION", "英雄配置语义版本必须大于 0。"));
        }

        ValidateActions("EFFECT", schema.Effects, issues);
        ValidateActions("CONDITION", schema.Conditions, issues);
        ValidateCodeTypes("DAMAGE_STAGE", schema.DamageStages, issues);
        ValidateCreationTemplates(schema.CreationTemplates ?? [], issues);
        return issues;
    }

    private static void ValidateCreationTemplates(
        IReadOnlyList<HeroAuthoringCreationTemplateDefinition> templates,
        List<HeroAuthoringSchemaIssue> issues)
    {
        foreach (IGrouping<string, HeroAuthoringCreationTemplateDefinition> duplicate in templates
                     .GroupBy(template => template.Key, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(
                new HeroAuthoringSchemaIssue(
                    "DUPLICATE_CREATION_TEMPLATE_KEY",
                    $"创建模板重复定义了 {duplicate.Key}。",
                    Subject: duplicate.Key));
        }

        foreach (HeroAuthoringCreationTemplateDefinition template in templates)
        {
            if (string.IsNullOrWhiteSpace(template.Key)
                || string.IsNullOrWhiteSpace(template.Label)
                || string.IsNullOrWhiteSpace(template.ParentNamespace)
                || string.IsNullOrWhiteSpace(template.ParentField)
                || string.IsNullOrWhiteSpace(template.TargetNamespace))
            {
                issues.Add(
                    new HeroAuthoringSchemaIssue(
                        "INVALID_CREATION_TEMPLATE",
                        $"创建模板 {template.Key} 缺少名称、父节点、引用字段或目标节点定义。",
                        Subject: template.Key));
            }

            if (template.Mode == HeroAuthoringCreationMode.Group
                && template.TargetNamespace is not ("EffectGroup" or "ConditionGroup"))
            {
                issues.Add(
                    new HeroAuthoringSchemaIssue(
                        "INVALID_CREATION_TEMPLATE",
                        $"分组创建模板 {template.Key} 的目标必须是 EffectGroup 或 ConditionGroup。",
                    Subject: template.Key));
            }

            if (template.Mode == HeroAuthoringCreationMode.GroupMember
                && (template.ParentNamespace, template.TargetNamespace) is not ("EffectGroup", "TbEffect")
                    and not ("ConditionGroup", "TbCondition"))
            {
                issues.Add(
                    new HeroAuthoringSchemaIssue(
                        "INVALID_GROUP_MEMBER_TEMPLATE",
                        $"组成员模板 {template.Key} 的父节点和目标节点类型不匹配。",
                        Subject: template.Key));
            }

            if (template.Mode == HeroAuthoringCreationMode.ParameterNode
                && (template.ParentNamespace is not ("TbEffect" or "TbCondition")
                    || template.ParentField != "action_param"
                    || template.TargetNamespace is not ("TbBuff" or "TbBullet" or "TbTrap" or "TbSearch" or "TbSkillResource")
                    || string.IsNullOrWhiteSpace(template.ActionKey)
                    || template.ParameterIndex is null or < 0))
            {
                issues.Add(
                    new HeroAuthoringSchemaIssue(
                        "INVALID_PARAMETER_NODE_TEMPLATE",
                        $"参数引用模板 {template.Key} 必须定义动作类型和有效参数位置。",
                        Subject: template.Key));
            }

            if (template.Mode == HeroAuthoringCreationMode.ParameterGroup
                && (template.ParentNamespace is not ("TbEffect" or "TbCondition")
                    || template.ParentField != "action_param"
                    || template.TargetNamespace is not ("EffectGroup" or "ConditionGroup")
                    || string.IsNullOrWhiteSpace(template.ActionKey)
                    || template.ParameterIndex is null or < 0))
            {
                issues.Add(
                    new HeroAuthoringSchemaIssue(
                        "INVALID_PARAMETER_GROUP_TEMPLATE",
                        $"参数分组模板 {template.Key} 必须定义动作类型、有效参数位置和分组类型。",
                        Subject: template.Key));
            }
        }
    }

    private static void ValidateActions(
        string category,
        IReadOnlyList<HeroAuthoringActionDefinition> definitions,
        List<HeroAuthoringSchemaIssue> issues)
    {
        ValidateUnique(
            category,
            definitions.Select(definition => (definition.Key, definition.LegacyValue)),
            issues);
        foreach (HeroAuthoringActionDefinition definition in definitions)
        {
            if (definition.MinParameterCount < 0
                || definition.MaxParameterCount is { } maximum
                && maximum < definition.MinParameterCount)
            {
                issues.Add(
                    new HeroAuthoringSchemaIssue(
                        "INVALID_PARAMETER_RANGE",
                        $"{definition.Key} 的参数数量范围无效。",
                        Subject: definition.Key));
            }

            int[] duplicateIndexes = definition.Parameters
                .GroupBy(parameter => parameter.Index)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            foreach (int index in duplicateIndexes)
            {
                issues.Add(
                    new HeroAuthoringSchemaIssue(
                        "DUPLICATE_PARAMETER_INDEX",
                        $"{definition.Key} 重复定义了参数 {index}。",
                        Subject: definition.Key));
            }

            foreach (HeroAuthoringParameterDefinition parameter in definition.Parameters)
            {
                if (parameter.Index < 0)
                {
                    issues.Add(
                        new HeroAuthoringSchemaIssue(
                            "INVALID_PARAMETER_INDEX",
                            $"{definition.Key} 的参数序号不能小于 0。",
                            Subject: definition.Key));
                }

                if (parameter.Kind == HeroAuthoringParameterKind.Reference
                    && string.IsNullOrWhiteSpace(parameter.ReferenceTarget))
                {
                    issues.Add(
                        new HeroAuthoringSchemaIssue(
                            "REFERENCE_TARGET_REQUIRED",
                            $"{definition.Key}.{parameter.Key} 缺少引用目标。",
                            Subject: definition.Key));
                }

                if (parameter.Kind == HeroAuthoringParameterKind.Enum
                    && string.IsNullOrWhiteSpace(parameter.EnumName))
                {
                    issues.Add(
                        new HeroAuthoringSchemaIssue(
                            "ENUM_NAME_REQUIRED",
                            $"{definition.Key}.{parameter.Key} 缺少枚举名称。",
                            Subject: definition.Key));
                }

                if (parameter.AllowsMultipleEnumValues
                    && (parameter.Kind != HeroAuthoringParameterKind.Enum
                        || string.IsNullOrWhiteSpace(parameter.EnumName)))
                {
                    issues.Add(
                        new HeroAuthoringSchemaIssue(
                            "MULTI_ENUM_REQUIRES_ENUM",
                            $"{definition.Key}.{parameter.Key} 只有枚举参数允许配置多选。",
                            Subject: definition.Key));
                }

                if (parameter.Kind == HeroAuthoringParameterKind.ScaledInteger
                    && parameter.Scale is null or <= 0)
                {
                    issues.Add(
                        new HeroAuthoringSchemaIssue(
                            "SCALE_REQUIRED",
                            $"{definition.Key}.{parameter.Key} 缺少有效缩放系数。",
                            Subject: definition.Key));
                }

                if (parameter.Repeating && parameter.RepeatStep <= 0)
                {
                    issues.Add(
                        new HeroAuthoringSchemaIssue(
                            "INVALID_REPEAT_STEP",
                            $"{definition.Key}.{parameter.Key} 的重复步长必须大于 0。",
                            Subject: definition.Key));
                }
            }
        }
    }

    private static void ValidateCodeTypes(
        string category,
        IReadOnlyList<HeroAuthoringCodeTypeDefinition> definitions,
        List<HeroAuthoringSchemaIssue> issues) =>
        ValidateUnique(
            category,
            definitions.Select(definition => (definition.Key, definition.LegacyValue)),
            issues);

    private static void ValidateUnique(
        string category,
        IEnumerable<(string Key, int LegacyValue)> entries,
        List<HeroAuthoringSchemaIssue> issues)
    {
        (string Key, int LegacyValue)[] values = entries.ToArray();
        foreach (IGrouping<string, (string Key, int LegacyValue)> duplicate in values
                     .GroupBy(value => value.Key, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(
                new HeroAuthoringSchemaIssue(
                    "DUPLICATE_KEY",
                    $"{category} 重复定义了 {duplicate.Key}。",
                    Subject: duplicate.Key));
        }

        foreach (IGrouping<int, (string Key, int LegacyValue)> duplicate in values
                     .GroupBy(value => value.LegacyValue)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(
                new HeroAuthoringSchemaIssue(
                    "DUPLICATE_LEGACY_VALUE",
                    $"{category} 重复使用了编号 {duplicate.Key}。",
                    Subject: duplicate.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
