# 上游来源与许可

本项目的问题定义、客服规则表及 C# 工作流逻辑派生自 TypeSafe 官方 WorkflowEvals：

- 原作者：TypeSafe / typesafe-ai 及 WorkflowEvals 贡献者。
- 来源：[WorkflowEvals](https://github.com/typesafe-ai/WorkflowEvals)
- 冻结提交：`0ac3b8ad845429f0d8e064ecfb2430a47c5a25cb`。
- 上游代码采用 Apache License 2.0，完整许可保留于 UPSTREAM-LICENSE。
- 本地修改：将问题和声明式规则保存为 JSON，将节点门控、策略和评分逻辑移植为 C#，新增离线估价、预算记账、断点恢复、自测和报告。

数据集许可独立于代码许可；prepare 保存每个数据快照的 README 和 dataset.json，按其中声明核对许可。原始数据下载至 git 忽略的 artifacts 目录。

分词配置来自 DeepSeek 官方公开的 `deepseek_v4_tokenizer.zip`，下载地址和 SHA-256 记录于 estimate.json。tokenizer-fixtures.json 是使用该配置与 Hugging Face tokenizers 对本地自拟文本计算的独立测试向量；运行评测和自测仅使用 .NET。

本工具是独立的 SDK 兼容评测，不代表 TypeSafe 或 DeepSeek 官方认证结果。
