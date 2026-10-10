# 完整技能链路创建

`CreateSkillChain` 将一个语义资产图编译成单个 WorkbookPatch，复用现有的
校验、Excel diff、临时副本验证、工作副本事务写入和撤销流程。

创建规则来自版本化的 `default-value-contract.v0.json` 中的 `creation`：
支持 Skill、Search、Effect、Condition、Buff、Bullet、Trap，动作和参数来自
capability registry。新增实体和分组分别按其命名空间当前最大 ID 加一分配，
模型只声明 localKey。实体字段必须与当前 Excel 表头类型一致。

一个 Plan 只包含一个创建操作。`root` 是根技能的 localKey 字符串；`nodes`
声明实体，`groups` 声明虚拟分组。`fields` 与 `parameters` 都是语义键到
Value 对象的映射。引用新资产使用 Local namespace/localKey；引用既有资产
使用 Existing namespace/id，引用既有分组使用 namespace/groupKey。

编译器要求所有新增实体和分组从根技能可达，技能有执行入口，分组非空，
没有循环。动作参数按元数据的索引、枚举、缩放和引用类型编码。分组成员
必须属于本次新增分组，避免隐式修改既有共享组。Patch 校验重新编译源 Plan
并比较完整结果，拒绝即使重新计算哈希的人工篡改。

Excel 写入追加数据行并保留既有行，复用末行的格式而不复制值或公式，更新
工作表 dimension。事务保存原文件，撤销恢复原文件字节。正式工作区写入
仍由 Studio 的确认按钮触发。

## 验证范围

自动测试覆盖自动技能 → 搜索 → 效果组 → 伤害，以及
技能 → 添加 Buff → Buff 周期效果组 → 伤害的多级编译。
覆盖跨表写入回读、原数据行保留、确定性分配、撤销后的字节哈希恢复；
未知引用、错误命名空间、遗漏动作参数、孤立节点、循环、无入口、枚举错误、
ID 溢出、表头漂移、过期源与 Patch 篡改均应拒绝。

真实 Excel 验收产物位于忽略目录 `.studio-work/creation-real/`，原 Unity
配置目录不参与写入。`verified-*.json` 记录各步骤结果，`generated-excel/`
保存创建后的 Skill、Search、Effect 表供检查。

2026-10-09 真实表格副本验收：自动技能冷却 8 秒，圆形搜索半径 3 世界单位，
敌方英雄最多一个目标，使用既有伤害管线 1001，固定伤害 50、攻击倍率 0。
分配 Skill 12340019、Search 98010004、Effect 123450851、EffectGroup 12345086。
26 个字段通过临时验证和事务写入回读，图包含 6 个节点、6 条边，所有节点均
存在。撤销后源哈希恢复为
`e6cae109193c6fe8ef63733a5046f6449f7914a3cfb64a08d0033602d025ae45`。
这次验收由 Codex 根据需求直接构造语义 Plan，不需要人工填写 Excel；它不能
作为 Studio 内置本地模型自主生成成功的证明。
可复用示例在 `contract-factory/examples/create-skill-chain.plan.json`；其中基线
是占位值，API 编译时会按当前工作区重设基线，既有管线引用需在目标工作区存在。

## 当前边界

尚不支持创建中的 map 值、未声明序列化的嵌套类型和重复动作参数。
不包含新增英雄挂接或表现资源；这些需要相应字段和操作支持。
Excel 原生结构化 Table 范围扩展尚未验证。真实目标表使用普通工作表。
编译和表格回读验证不等同于 Unity 运行时战斗验证。

Agent 保留每轮工具证据，枚举查询不夹带全部动作和字段；无效 Plan 最多
按当前 JSON Schema 自动修复两次，每次重新校验。模型仍可能输出语义错误，
因此不能跳过确定性编译、diff 审阅与用户确认。
支持结构化响应的提供方可配置 `SupportsJsonSchema=true`，修复请求使用
`response_format`；默认仅为本地 Ollama 开启，其他提供方须明确支持后开启。

本地 `qwen3.5:4b` 的自主生成验收未通过，按用户要求停止以它作为验收目标。

2026-10-09 云端验收：原代理返回 `cc_switch_auth_error`，因为全局 Codex
路由选择 OpenAI Official，而 Studio 仍使用 API Key 占位凭据和 DeepSeek 模型。
Studio 现在只读加载明确选定的 CC Switch API Key 配置，按其 `meta.apiFormat`
选择协议后直连云端，不修改全局路由、登录或数据库。5257 的模型列表与聊天均
返回 200，默认云端为 DeepSeek-Flash。

云端根据自然语言生成完整 Skill → Search → EffectGroup → Damage Plan；
第一版存在嵌套 Fix 提前缩放的问题，语义验收在写入前拦截。向模型反馈后，
修正版本的冷却、半径、目标、伤害与倍率全部符合请求，25 个字段通过临时
验证、隔离工作副本事务写入、链路回读和撤销。撤销后哈希完全恢复。
产物：`.studio-work/creation-real/cloud-verified-*.json` 和
`cloud-generated-excel/`。这是一条带一次反馈纠错的云端成功案例，不代表
全部机制或首次生成成功率已经达标；尚未做 Unity 战斗运行验收。
