# 供应商预设 ↔ 实例侧配置：格式对照与归一（改版·供应商同步 / 配置格式对齐）

权威形状取自真实实例 `~/.dsh/settings.yaml`（providers 段）与本应用全局预设模型
（`api-presets.json`）。本表是映射/归一/降级的单一事实源，配套单测
`ProviderConfigMapperTests` 断言其中关键规则。

## 1. 格式对照表

| 全局预设字段（api-presets.json） | 实例 settings.yaml providers.<key> | 归一规则 | 降级 |
| --- | --- | --- | --- |
| Id（内部） | key | 不直接进 yaml；key = slug(kind + "-" + name) | — |
| Name | displayName | trim | 空 → 同步跳过（预设校验已拦） |
| Kind | api | 见 §2 ApiAdapter | 未知 kind → openai-completions（note） |
| BaseUrl | baseURL | 去尾斜杠、scheme 小写 | 空 → 省略（用默认端点，note） |
| ApiKey | apiKeyEnv（+环境变量注入） | 实例侧只存 env 名，不存密钥值 | 密钥未注入实例环境 → note 提示 |
| DefaultModel | models[0].id（+name） | 单模型入形 | 空 → 省略 models 段（note） |
| Enabled | （不进 yaml） | 仅控制预设是否参与同步 | 停用预设不参与 |
| — | models[].contextWindow / maxTokens / compat | 预设不持有 → 宽松段保留 | 合并时原样保留不动 |
| — | 目标条目其他未知键 | Extra 宽松段 | 合并时原样保留不动 |

## 2. ApiAdapter（kind → api 宽松归一，参考 PluginCompat 宽松段先例）

- kind 含 `responses`（如 openai-responses / responses）→ `openai-responses`
- 其余（deepseek / openai-compatible / 未知）→ `openai-completions`

## 3. 键与密钥命名归一

- providerKey：**ASCII slug**（仅 [a-z0-9]，其余折叠为 '-'）；中文等非 ASCII 名段不参与键（防 YAML 键乱码），同名冲突以 displayName 区分；空兜底 `preset`
- apiKeyEnv：`DSH_PRESET_` + key 大写、'-'→'_'（如 `DSH_PRESET_DEEPSEEK_DEEPSEEK`）

## 4. 降级策略（明确清单）

1. 未知 kind → `openai-completions`，note：`未知类别已按 openai-completions 适配`
2. 预设含 ApiKey → 只写 apiKeyEnv 名，note：密钥需实例环境注入
3. DefaultModel 空 → 省略 models 段，note：可在实例侧手动补模型
4. BaseUrl 空 → 省略 baseURL，note：将使用实例默认端点
5. 宽松段（contextWindow/maxTokens/compat/未知键）→ 合并保留，不因预设更新丢失

## 5. 落点

- 映射纯函数：`src/DshController.Core/ProviderConfigMapper.cs`
- 单测：`tests/DshController.Tests/ProviderConfigMapperTests.cs`
- YAML 读出/合并/写入：`同步预览窗` / `写入生效(redteam)` 小类承接（本表为其契约）
