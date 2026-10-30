# 发布所需记录

这里只保留当前版本打包／验证脚本直接使用的 JSON、既有 49 项机制审计，以及旧版套件汇总仍读取的两份基线账本。JSON 原始内容与 SHA-256 关系保持不变；记录中的历史路径是当时的来源，不表示这些文件仍随安装包提供。

| 文件 | 用途 |
|---|---|
| [runtime-room-0.2.5.json](runtime-room-0.2.5.json) | 当前二进制与交付范围 |
| [runtime-blockers-0.2.5.json](runtime-blockers-0.2.5.json) | 完整问题状态，不因精简而关闭 |
| [development-validation-0.2.5.json](development-validation-0.2.5.json) | 升版前验证摘要及原哈希 |
| [release-authorization-0.2.5.json](release-authorization-0.2.5.json) | 本版既有发布请求与哈希约束 |
| [audit-remediation-0.1.7.md](audit-remediation-0.1.7.md) | 历史机制审计，供发布脚本校验 49 个条目；不代表当前完整 Boss 实机认证 |

早期日志、账本和逐轮结果见 [固定历史提交](https://github.com/gengsha/HollowConnect/tree/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/evidence)。维护者另保留原文件本地归档。新测试的原始输出放 `.analysis/` 或 `.test-results/`；需要进入发布流程时，仅提交带版本、哈希、覆盖范围和限制的摘要。旧版专项脚本中保留的证据路径仍用于原专项，不是当前打包依赖。
