Windows Intelligence · 安装到另一台电脑

适用于 Windows 11 22H2 或更新版本的 x64 设备。

1. 把压缩包解压到一个文件夹。
2. 双击 Install-MSIX.cmd。首次安装时，Windows 会请求管理员批准。
3. 脚本核对 MSIX 和证书，再将公开证书加入「本地计算机 → 受信任的人」，然后为当前 Windows 用户安装应用。
4. 从开始菜单启动 Windows Intelligence，再从任务栏通知区域打开设置。默认用 Copilot 键唤醒。请在 Windows 的 Copilot 键设置中选择 Windows Intelligence，或在应用的「唤醒快捷键」页录制并保存其他组合。

也可以手动安装。先将 WindowsIntelligence-Signing.cer 导入「本地计算机 → 受信任的人」，再双击 WindowsIntelligence.msix。

证书文件仅包含公钥。SHA256SUMS.txt 可用于核对下载文件。离线语音识别和朗读模型已包含在 MSIX 里，AI 服务的 API Key 需要在目标设备上单独配置。
