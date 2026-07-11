# TMSpeech 本地改动迁移记录（2026-07-11）

## 迁移目标

将原本没有共同 Git 祖先的本地开发历史迁移到 fork `JoshuaChen2008/TMSpeech` 的上游历史之上，同时保留本地功能边界、完成日期和可审阅的提交顺序。

- 上游与 fork 基点：`983e66183958ea36b401ca71c9f507fb701a1023`
- 本地安全分支：`backup/local-master-20260711`
- 本地安全提交：`276905870e1f43780039e088d051b6b5fee0167e`
- 迁移分支：`agent/migrate-local-history`

远端 `master` 未被改写。迁移时没有合并两条无共同祖先的历史，而是从上游基点重建本地基础差异，再按功能逐项迁移。

## 当前项目功能

TMSpeech 是基于 .NET 6 和 Avalonia 的 Windows 实时语音字幕应用，使用插件架构组织音频源与识别器。本次迁移保留的本地增强包括：

- 引擎配置与识别配置管理，以及 AliyunCloud、LLMAudio 云识别插件。
- DashScope Fun-ASR / Paraformer WebSocket 实时流式识别。
- 数据驱动的 StreamingAsr 插件，内置 Fun-ASR、阿里云 NLS、OpenAI Realtime 和自定义协议预设。
- 插件后台加载与通知服务异步初始化，缩短主窗口可见时间。
- 字幕锁定后的悬浮控制条，以及开始、停止、重启、解锁和退出操作。
- Fun-ASR 静音自动挂起、声音唤醒、预滚音频和失败重试。
- 主窗口控制条退出按钮。

## 提交映射与完成日期

日期使用 UTC+8；原提交 `8ecb5aa` 的 Git 时间为 UTC，表中已换算为 UTC+8。

| 完成时间 | 原提交或来源 | 迁移后提交 | 功能 |
| --- | --- | --- | --- |
| 2026-06-19 15:50:52 | `0d4de4b` + `4b7ccb3` | `25d6acd` | 重建引擎配置、AliyunCloud 和初版 LLMAudio 基线；排除临时误加入的 `TMSpeech` gitlink。 |
| 2026-06-19 17:33:22 | `8ecb5aa` | `2567514` | OpenAI 兼容音频 data URL 修复。 |
| 2026-06-19 17:45:05 | `7c00dfa` | `97d7ae3` | LLMAudio 重写为 DashScope Fun-ASR 实时流式识别。 |
| 2026-06-19 20:39:10 | 未提交的 StreamingAsr 工作区 | `e62972b` | 添加数据驱动的通用流式 ASR 插件；作者日期和提交日期均使用实际完成时间。 |
| 2026-07-11 15:52:21 | `5738b9b` | `ea87c96` | 插件后台加载与通知服务异步初始化。 |
| 2026-07-11 16:12:25 | `f34cb8d` | `9018333` | 锁定后的悬浮控制条和统一操作命令。 |
| 2026-07-11 16:26:54 | `612196b` | `5d486eb` | Fun-ASR 静音挂起、声音唤醒和超时错误处理。 |
| 2026-07-11 16:51:05 | `913b74c` | `ee2c83b` | 主窗口控制条退出按钮。 |

## 敏感信息核验

推送前对工作区、迁移后的完整新增历史和当前变更文件进行了脱敏规则扫描，覆盖：

- GitHub、OpenAI 风格和常见云厂商访问密钥。
- API Key、AccessKeyId、AccessKeySecret、Token、Authorization 和连接字符串的非空赋值。
- JWT、私钥内容和包含用户名密码的 URL。

扫描范围包括 5,157 行新增历史补丁和 40 个变更文件，结果为 **0 个疑似真实凭据命中**。StreamingAsr 和云识别插件的凭据默认值均为空或文档占位符。

`bin/`、`obj/`、运行日志、用户配置和 Debug 发布目录均受 `.gitignore` 保护，未进入迁移提交。

## 验证结果

- 迁移分支与本地安全分支的代码树一致；本文件是唯一新增的迁移文档。
- `git diff --check origin/master..HEAD` 通过。
- `dotnet build TMSpeech.sln -c Debug --no-restore -m:1 -p:UseSharedCompilation=false` 以 exit code 0 完成。
- 最终增量构建报告 105 个既有警告、0 个错误；首次完整编译摘要为 235 个既有警告、0 个错误。
- 已确认 AliyunCloud、LLMAudio 和 StreamingAsr 插件 DLL 均进入 Debug 插件输出目录。
- 已知警告包括 .NET 6 生命周期结束和 `SharpCompress 0.38.0` 的 NU1902 漏洞提示；本次仅迁移历史，不混入依赖升级。
