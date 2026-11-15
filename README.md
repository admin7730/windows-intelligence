# Windows Intelligence

Windows 11 的语音、截图和文字助手。识别在本机完成，问答可连接 DeepSeek 或兼容的模型服务。你也可以要求助手用 PowerShell 处理本机文件或执行其他电脑操作。

## 安装与初次配置

1. 在 [Releases](https://github.com/admin7730/windows-intelligence/releases) 下载 `WindowsIntelligence-0.2.39-x64.zip`。适用于 Windows 11 22H2 或更新版本的 x64 电脑。
2. 解压整个 ZIP，双击 `Install-MSIX.cmd`。首次安装会请求管理员批准。脚本核对 MSIX 的签名和证书，把项目的公开证书加入「本地计算机 → 受信任的人」，然后为当前 Windows 用户安装应用。
3. 从开始菜单启动 **Windows Intelligence**，双击通知区域的图标打开设置。在「模型服务」输入你自己的 DeepSeek API Key 并保存。也可添加兼容 Chat Completions 的服务。
4. 在「语音朗读」中，可选择本地 Kokoro、MiniMax 或 ElevenLabs。使用云端朗读时，在同一页填写对应的 API Key。语音识别模型已包含在安装包中。

## 使用

- 默认使用 Copilot 键。首次绑定时，在「唤醒快捷键」打开 Windows 按键选择器并选择 Windows Intelligence。也可在应用的同一页录制并保存其他快捷键。
- 短按开启语音，说完后自动提交。双击进入静默文字输入。长按截图，按 Esc 关闭助手。
- 可直接说“打开记事本”或“查询 C 盘还剩多少空间”。在「总览」可关闭「允许助手自动操作电脑」。命令以当前 Windows 用户的权限运行，命令输出会作为上下文返回给模型服务。
- 截图可在本地预览、裁剪和遮挡。自动上传截图需要在「隐私与历史」向当前模型服务授权。

应用在 Windows 凭据管理器中保存你填写的 API Key。

## 从源码构建

需要 .NET 10 SDK、Visual Studio 2022 C++ x64 Build Tools、Windows SDK 和 Inno Setup 6。

```powershell
.\scripts\Get-Models.ps1 -Fixtures
.\scripts\Test.ps1
.\scripts\Build.ps1
```

构建产物位于 `artifacts/release/`。[架构](docs/architecture.md) · [第三方软件及模型说明](docs/third-party.md)
