# Assistant-only 精简报告

本文记录 0.2.32 精简迁移。当前实现见[现行架构](architecture.md)。

日期：2026-11-15  
范围：只保留 Copilot 键、Siri AI 助手、对话、朗读和截图提问。

## 1. 保留的能力

| 能力 | 入口/实现 |
|---|---|
| 短按 Copilot 键唤醒 | `Activation.Tap` → `Interaction.Start(false)`；边缘光效/粒子与右上角输入胶囊启动，开始麦克风聆听 |
| 长按 Copilot 键截图 | `Activation.Down` → `Interaction.Start(true, held: true)`；松开 `Activation.Up` → `Interaction.Release()`；Windows Graphics Capture 截图后可预览、裁剪、遮挡、发送 |
| 语音对话 | Zipformer 流式识别 + SenseVoice 整句复核 → DeepSeek（OpenAI-compatible） |
| 自动朗读 | `Settings.AutoSpeak` + MiniMax Speech-2.8，失败回退 Turbo；也支持 ElevenLabs、本地 Kokoro |
| 键盘提问 | 输入胶囊与回复 Markdown 面板 |
| 屏幕边缘光效/Siri 视觉 | 原有 Direct3D 11 / DirectComposition Overlay，未重写 |
| 设置 | DeepSeek、视觉、语音识别、语音朗读、Copilot 键、截图、性能、隐私、开机启动、托盘 |
| 托盘 | 开始语音、输入问题、截图提问、设置 Copilot 键、设置、退出 |
| Copilot 注册 | 包清单、协议、AppExtension、快速路径、注册诊断保持原样 |

短按和长按的分发仍由 `CopilotGate` 去抖；长按期间短按被抑制，松开只触发一次。无物理 Copilot 键时，可通过托盘或 `--settings` 启动设置。

## 2. 删除的能力

- Dynamic Island（灵动岛）及全部页面、媒体/歌词/通知/文件托盘/计时器/音量/活动状态。
- 智能工作台（写作、翻译、照片、批处理、电影导出）。
- 提醒事项与提醒轮询。
- 写作快捷键 `Ctrl+Alt+Space` 以及“选中文字打开工作台”路径。
- 以上功能的设置页、托盘入口、测试、文档入口。
- 与上述功能绑定的源码文件已删除，不再编译或打包。

保留的 `Settings` 核心字段：服务提供商、TTS、截图、语音识别、视觉、Copilot、性能、隐私、托盘/开机启动。旧 JSON 中已删除的字段会被忽略，不会阻止启动；新保存的 JSON 不再写回这些字段。

## 3. 文件变更

删除：

`IslandActivities.cs`、`IslandMedia.cs`、`IslandMotion.cs`、`IslandOptions.cs`、`IslandOutsideClick.cs`、`IslandPanels.cs`、`IslandSurface.cs`、`IslandSystem.cs`、`IslandView.cs`、`IslandVisuals.cs`、`LocalLyrics.cs`、`PlayerLyrics.cs`、`LyricText.cs`、`Workbench.cs`、`Reminders.cs`、`IntelligenceActions.cs`、`IntelligenceMedia.cs`、`LocalMaterial.cs`。

修改：

| 文件 | 变更 |
|---|---|
| `SystemIntegration.cs` | 只保留 assistant-only `AppHost`；移除 Island、Workbench、Reminders、写作快捷键的热键和菜单；保留 Copilot 激活、托盘、设置、Overlay/Interaction |
| `Program.cs` | 移除写作快捷键注册和选区导入；保留 Copilot 消息/单实例/激活分发 |
| `Interaction.cs` | 移除 `SuspendIsland` 回调；保留语音、截图、对话和朗读 |
| `SettingsWindow.cs` | 移除灵动岛、系统集成、工作台入口和通知权限；页面收敛为助手相关 14 页；保留 Copilot 诊断与启动/托盘 |
| `Settings.cs` | 移除 Island/Media/Volume/Notifications/WritingHotkey 字段；旧 JSON 兼容 |
| `OverlayUi.cs` | 移除“工具”按钮和工作台入口；保留语音胶囊、回复面板、截图缩略图、朗读 |
| `README.md` | 改写为 assistant-only 使用说明 |
| `tests/.../Program.cs` | 移除 Island/歌词/媒体/工作台测试，保留 Copilot、截图状态、API、凭据、ASR/SenseVoice、TTS、云语音、WGC、DirectComposition、助手 Markdown 和设置兼容测试 |

## 4. 验证

`scripts/Test.ps1` 实际运行：**12 项通过 / 0 项失败**，退出码 0。日志 `artifacts/test-assistant-only-final3.log`。另执行一次 `WindowsIntelligence.exe --settings` 冷启动冒烟：进程保持运行、窗口响应，随后关闭测试进程。

### 激活可见性修复

复测发现：助手窗口在 WPF 中已显示，但 `AllowsTransparency` 工具窗口会偶发丢失原生 `WS_VISIBLE`，表现为按下 Copilot 键后进程与窗口都存在却不显示。`Caption.Show` 及首次定位现在调用 `ShowWindow(SW_SHOWNA)` 重新断言原生可见性，不抢焦点。已加入 `AssistantTest` 的 `Desktop.IsWindowVisible` 断言。

### 安装包验证

重新打包并安装 **0.2.32.0**（同一发布者 `CN=JINZAIXI`、同一包族 `WindowsIntelligence.Desktop_fe33b2ncp4518`），Copilot 键映射的 AUMID 未变。用安装版协议 `windowsintelligence-copilot://?state=Tap` 实测：

- 进程从 `C:\Program Files\WindowsApps\WindowsIntelligence.Desktop_0.2.32.0_x64__fe33b2ncp4518` 启动；
- `Windows Intelligence · 助手` 可见，GPU 边缘光效窗口可见；
- 不再创建 Dynamic Island 窗口。

物理 Copilot 键本身仍受 Windows 的按键映射和桌面会话影响，无法由脚本替代。

| 测试 | 结果 |
|---|---|
| Copilot URI、短按/长按去抖、释放 | 通过 |
| 截图状态机、重复上传与重试 | 通过 |
| Multimodal 请求、SSE、视觉能力拦截 | 通过 |
| 凭据管理器写入/读回/清除 | 通过 |
| Zipformer 流式识别 + SenseVoice 复核 | 通过（字符错误 9/79 streaming、3/79 final） |
| Kokoro 本地 TTS 与真实音频设备 | 通过 |
| MiniMax / ElevenLabs / 云语音契约与回退 | 通过 |
| WGC 真实窗口/显示器捕获与自身 UI 排除 | 通过 |
| DirectComposition 边缘光效/粒子与透明命中 | 通过（本机约 55 次/秒，空闲 0 字节回读） |
| 助手窗口不进任务栏/Alt+Tab、Markdown 回复 | 通过 |
| 旧设置 JSON 兼容 | 通过 |

未覆盖：物理 Copilot 硬件键、签名 MSIX 在 Windows 键选择器中的实际可选性、真实麦克风口述/真实截图对话的端到端流程。这些依赖硬件、签名和系统权限，不能用单元测试替代。

## 5. 回滚

完整功能版本次修改前已归档到：

`artifacts/backup-full-20261115-143850/`

包含当时的 `src/WindowsIntelligence`、`tests`、`docs`、`packaging`。需要恢复时从该目录复制对应文件，或使用 Git 回退本次 assistant-only 改动。

## 6. 已知限制

- 删除后不再有灵动岛、媒体、通知或文件托盘；这是本次需求，不是回归。
- 托盘关闭时（设置 `Tray=false`）程序仍可由 Copilot 键或 `--settings` 启动，但没有常驻托盘入口。
- 物理 Copilot 键的短按/长按行为在 Windows 层的分发取决于已签名 MSIX 被选为 Copilot 键应用；本机开发注册不能验收选择器。
- `README` 与旧灵动岛设计文档并存；旧文档描述的是已删除的版本，历史参考价值保留，不代表当前功能。
