# 供应商预设 ↔ 实例侧配置:格式对照与归一(llm-pi-ai 迁移轮)

**权威目标(dsh 源码证据,2026-09-06 轮核实)**:dsh 的自定义提供方由
`@deepseek-ai/dsh` 的 `dsh-llm-pi-ai` 适配器从实例 `~/.dsh/settings.yaml` 的
**`llm-pi-ai.providers.<key>`** 读取(适配器注册该命名空间并读 `config.providers`);
官方 DeepSeek 模型由 `dsh-llm-deepseek` 适配器从 **`llm-deepseek:`** 命名空间提供。
**根级 `providers:` 在 dsh 全源码无任何消费方**——2026-09-04/05 轮的同步曾写往根级,
属无效落点,本轮迁移并自动清理(见 §5)。

本表是映射/归一/降级的单一事实源,配套单测 `ProviderConfigMapperTests`、
`ProviderSyncPlanTests`、`ProviderSyncWriterTests`(合并语义)、`ProviderYamlScan` 隐含覆盖。

## 1. 格式对照表(非官方预设 → `llm-pi-ai.providers.<key>`)

| 全局预设字段(api-presets.json) | 实例 llm-pi-ai.providers.<key> | 归一规则 | 降级 |
| --- | --- | --- | --- |
| Id(内部) | — | 不进 yaml | — |
| **ProviderId** | key | 稳定路由(创建时选定,`^[a-z][a-z0-9]*(-[a-z0-9]+)*$`,台账查重);旧档案无路由 → 回填 slug(kind+name) | 空 → slug 兜底 |
| Name | displayName | trim | 空 → 同步跳过(预设校验已拦) |
| Kind(=API 协议) | api | 见 §2 ApiAdapter | 未知 kind → openai-completions |
| BaseUrl | baseURL | 去尾斜杠、scheme 小写 | 空 → 省略(用默认端点,note) |
| ApiKey | apiKeyEnv(+启动注入) | 实例侧只存 env 名(`DSH_PRESET_*`),**密钥值由 DshController 启动实例时注入子进程环境**(Windows 直注;WSL 经 WSLENV `/u`);不落 settings.yaml 也不落实例配置 | 手动启动的实例需自行设置(note) |
| **Models[].Multimodal** | models[].input | 三态:**true → `[text, image]`;false → `[text]`;null(未知)→ 省略**;实例侧已有非空声明保留,`[]` 视为未声明可覆盖;词表对齐 dsh `ModelModalityMap`(仅 text\|image) | 未知 → 省略(路由默认 [text]) |
| —(自动) | models[].reasoningEfforts | **非官方来源一律自动写四档** `{off: null, low: low, high: high, max: max}`(对齐 dsh-thinking-efforts v0.2.0;off 档线上省略 reasoning);实例侧已有声明(含 `false`)**不覆盖** | 仅官方豁免(见 §3) |
| Models[].ContextWindow/MaxTokens | models[].contextWindow/maxTokens | 预设权威(增补合并时替换实例现值) | 空 → 省略(路由默认 262144/32768) |
| DefaultModel(兼容字段) | — | 恒 = Models[0].Id;载入旧档案时反向播种 Models | — |
| Enabled | (不进 yaml) | 仅控制预设是否参与同步 | 停用预设不参与 |
| — | models[].compat / 其他未知键 | **增补合并原样保留**,不因预设更新丢失 | — |

## 2. ApiAdapter(kind → api)

- 精确透传(与 dsh pi-ai 适配器协议门一致):`openai-completions` / `openai-responses` /
  `azure-openai-responses` / `openai-codex-responses` / `anthropic-messages`
- 兼容回落:未知 kind 含 `responses` → `openai-responses`;其余 → `openai-completions`

## 3. 官方内置提供方(DeepSeek 官方)= 仅送密钥

- 官方模型、思考档(`llm-deepseek` 适配器原生四档)、多模态(vision-exp 的
  `inputModalities: [text, image]`)均由实例 `llm-deepseek:` 命名空间**原生提供**,
  预设不重复建模——**官方同步不建 llm-pi-ai 块**。
- 官方同步唯一动作:预设填有密钥时,写/更新 `llm-deepseek.apiKeyEnv: DSH_PRESET_DEEPSEEK_OFFICIAL`
  (段缺失则文件尾追加);密钥值同样经启动注入。未填密钥 → 整单跳过(note)。

## 4. 合并语义(增补式,`ProviderSyncMerge` 纯函数)

- 无 `llm-pi-ai:` 段/无 `providers:` 子段/无 `<key>` 块 → 逐级创建后整块插入(+4 缩进)。
- `<key>` 块已存在 → 增补:
  - 路由字段(displayName/api/apiKeyEnv/baseURL)按规范顺序替换或插入;预设未给(如 BaseUrl 空)保留实例现值;
  - 模型按 id(大小写不敏感)匹配:`name/contextWindow/maxTokens` 以预设为准;
    `compat` 等未知字段逐行保留;`reasoningEfforts` 已有(含 `false`)不覆盖、缺失才补;
    `input` 非空既有声明保留、`[]`/缺失且预设已知时写入;
  - **实例侧多出的模型原样保留**(同步不删,note 明示);预设新模型追加到 models 列表尾。
- 幂等:同一预设重复同步,第二次合并结果与原文件一致 → 不落盘(零备份噪音)。
- 迁移清理:每次同步成功后删除根级 `providers.<key>` 旧块;段空则连段移除(含空行归并)。

## 5. 迁移说明(2026-09-06 轮)

- 2026-09-04/05 轮同步写往根级 `providers:`(当时误读真实 settings.yaml——那些根级块
  实为本应用自身写入)。本轮起写 `llm-pi-ai.providers.<key>`,旧根级块随每次同步自动清除;
  未再同步的旧块(如探针残留 `probe-*`)无消费方、无副作用,可手动删除。
- 上一轮挂账 N7 的"深层非根级 providers 合并按根级新增"由此解决一半;**WSL 实例的
  settings.yaml 位于 Linux 侧 HOME,Windows 侧写入仍不达**(N7 剩余半边,维持挂账)。

## 6. 写入形状示例(settings.yaml)

```yaml
llm-pi-ai:
  providers:
    acme:
      displayName: Acme
      api: openai-completions
      apiKeyEnv: DSH_PRESET_ACME
      baseURL: 'https://api.acme.com/v1'
      models:
        - id: some-vlm
          name: Some VLM
          contextWindow: 1000000
          maxTokens: 384000
          input: [text, image]
          reasoningEfforts:
            off: null
            low: low
            high: high
            max: max
llm-deepseek:
  apiKeyEnv: DSH_PRESET_DEEPSEEK_OFFICIAL   # 仅官方预设同步写这一行
```

## 7. 落点

- 映射纯函数:`src/DshController.Core/ProviderConfigMapper.cs`(InputModalities/WriteReasoningEfforts)
- 校验规则:`src/DshController.Core/ProviderPresetRules.cs`
- 模型目录探针(容量别名 + 多模态三态):`src/DshController.Core/ProviderModelProbe.cs`
- 同步预览计划(官方/非官方分支):`src/DshController.Core/ProviderSyncPlan.cs`
- 合并纯函数:`src/DshController.Core/ProviderSyncMerge.cs` + 行扫描 `ProviderYamlScan.cs`
- 写入编排(备份/原子/无变化不落盘):`src/DshController.Core/ProviderSyncWriter.cs`
- 启动注入:`BackendManager.Start.cs` / `BackendManager.Wsl.cs`(`DSH_PRESET_*` + WSLENV)
- 单测:`tests/DshController.Tests/Provider*.cs` 八件套
