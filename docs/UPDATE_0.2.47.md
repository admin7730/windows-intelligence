# Windows Intelligence 0.2.47 · 任务派发

Windows Intelligence 现在可以把文字或语音任务派发给已安装的 DeepSeek Harness、ChatGPT / Codex，或 WorkBuddy 官方云端任务 API。入口位于助手输入区、托盘菜单和设置的“任务派发”。

## 使用方式

1. 打开“派发任务”，选择执行工具。
2. 对 Harness 或 ChatGPT / Codex，选择本机工作目录。目录会被保存，后续可再次选择。任务只发送本次填写的内容，不附带整个 Windows Intelligence 对话。
3. 输入任务后点击“派发”；也可点击“语音派发”，说完后使用现有 ASR 自动提交，无需人工确认转写。
4. 左侧作业列表显示排队、执行、返回内容和错误。点击已结束的本机任务，再点“继续此任务”，可沿用该工具的会话和工作目录。点“新任务”创建独立任务。
5. 关闭作业窗口后任务继续运行；正常退出整个 Windows Intelligence 会取消它启动的本机子进程。重启后未结束的任务标记为中断，不自动重做。进程崩溃、断电等异常退出不能保证外部子进程已停止，请先检查外部工具状态再重发。

本机任务串行执行，最多 20 个待处理任务，作业记录最多 50 条。记录遵循“在本机保存文本对话历史”设置；关闭该设置不再写入作业历史。外部工具自身的原生会话存储和云端保留策略由相应工具管理。

## 三个目标的能力边界

| 目标 | 执行位置 | 登录 / 凭据 | 继续任务 |
| --- | --- | --- | --- |
| DeepSeek Harness | 所选本机目录，官方 `dsh headless --json` | 复用 Harness 原有配置；可将 Windows Intelligence 已保存的官方 DeepSeek Key 仅传入本次子进程环境 | 支持，使用原生 session ID |
| ChatGPT / Codex | 所选本机目录，官方 `codex exec --json` | 使用 Codex 已有登录；需要已安装且已登录的 Codex | 支持，使用原生 thread ID |
| WorkBuddy Cloud | WorkBuddy 远端沙箱 | 单独的官方 OAuth Access Token，保存在 Windows 凭据管理器；普通模型 API Key 不通用 | 此版只支持创建任务及查询状态，续聊在 WorkBuddy 中进行 |

WorkBuddy 不要求本机安装客户端。它的云端任务 API 不能访问这里选择的 Windows 文件夹，因此选择 WorkBuddy 时目录选择会禁用，不上传本机文件。此版没有接入 WorkBuddy 本地助手，也没有把云端派发假装成本地操作。

“ChatGPT / Codex”指 Codex 官方本地编码执行入口，不是向普通 ChatGPT 网页对话或 ChatGPT 云端工作区投递任务。云端工作区需要另外的官方授权和 API，本版未实现。

## 权限与记录

- “只读任务”使用工具的原生 `read-only` 模式；允许修改时使用 `workspace-write`，同时要求 Windows Intelligence 的“电脑自动操作”已开启。
- 工作目录决定执行位置；具体沙箱、网络、文件读写范围仍由工具自身及其配置决定，目录选择不等于完整的文件访问隔离。
- Harness 使用 headless 配置，不修改桌面共享插件或桌面配置。路径可在设置中覆盖，支持 `.exe`、`.cmd`、`.bat`。
- 任务内容通过标准输入传递，不拼接为 PowerShell 命令。取消仅针对 Windows Intelligence 启动的子进程，不关闭用户的 Harness 或 Codex 主窗口。
- 展示工具调用、命令、输出与最终答复，不保存模型内部推理事件。已注入的凭据在显示和记录前隐藏，API Key 不写入任务或安装包。
- WorkBuddy 没有已验证的只读执行参数，不能用提示词代替权限隔离，因此不提供云端只读承诺。云任务也要求“电脑自动操作”已开启。
- WorkBuddy 提交回执只表示接收成功；网络异常时结果标记为未确认，不自动重复创建。状态接口不返回完整回答，云端完成后需到 WorkBuddy 查看产物。

## 验证结果

- 本机真实 DeepSeek Harness 和 ChatGPT / Codex 均在指定临时工作目录成功完成只读派发并返回结束事件。
- 自动测试覆盖 stdin 边界、中文内容、原生会话续接、跨目录续聊拒绝、取消、重启、权限开关、凭据隐藏、内部推理事件过滤、云端回执/状态区别、异常大响应与界面渲染。
- WorkBuddy 仅完成官方协议模拟测试。本机没有 WorkBuddy OAuth 授权，尚未进行真实账户任务测试；不宣称已授权或已接通真实云任务。
- 原有语音、快捷键、截图、对话历史和简繁英设置继续使用原实现。

## 官方资料

- [DeepSeek Harness 源码与 CLI](https://github.com/deepseek-ai/deepseek-harness)
- [Codex 非交互执行](https://learn.chatgpt.com/docs/non-interactive-mode.md)
- [WorkBuddy Open API 与 OAuth 授权](https://open.workbuddy.cn/docs/openapi)
