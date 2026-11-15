# Windows Intelligence 现行架构

**应用与唤醒。** 应用使用 .NET 10 和 WPF。 `AppHost` 组装右上角助手、托盘和设置。 `CopilotGate` 处理短按、双击和长按，`AssistantHotkey` 注册由用户保存的全局快捷键。

**语音与对话。** `Interaction` 接收 `SpeechRecognizer` 的最终转写并自动提交。实时预览使用 Zipformer，完整语句使用 Qwen3-ASR 或 SenseVoice。 `AiClient` 负责 Chat Completions 和工具调用，`SpeechOutput` 管理朗读，`PowerShellRunner` 在当前用户权限下处理本机操作。

**截图与动效。** `ScreenshotState` 记录每次截图的捕获、预览、提交和取消。原生 `WinNative.dll` 通过 Windows Graphics Capture 获取截图，通过 Direct3D 11 和 DirectComposition 渲染屏幕边缘光。 `Overlay` 维护每台显示器的渲染面。

**部署与配置。** 自包含的 x64 MSIX 包含本地语音模型。应用配置使用本地 JSON，云端凭据由 Windows 凭据管理器保存。仓库内的安装脚本负责核对签名、导入公开证书并注册应用。

[历史架构与功能记录](archive/README.md)
