# 官方小爱技能桥接

本服务只负责鉴权、任务交接和回复格式，不是另一个 AI 助手。Windows 端复用现有 Interaction、AiClient、PowerShellRunner、模型配置与执行界面。不刷机，不开放电脑控制端口，不读取音箱原生日志或麦克风。

## 已实现与尚未接通

已实现官方 V1 HMAC 校验、技能 app_id/用户/设备绑定、重复请求合并、一次领取的持久任务、结果回传、原生 to_speak 格式、Windows 主动连接、QQ 本地文件变化与系统通知入口。已有本机模拟协议和真实 PowerShell 测试。

当前没有附带真实技能 app_id/签名凭据或公开 HTTPS 部署，也尚无真实音箱对话验收结果。需要自行在开放平台配置并联调，不能把本机协议测试当作平台联调成功。

## 平台配置

1. 在用户本人的 Edge 中进入小爱开放平台，确认开发者主体与可用技能类型。登录、验证码和实名资料由本人完成，不编造资料。标准技能按官方文档配置调用名称，候选名称“电脑助手”，实际名称能否申请仍由平台决定。
2. 参考 intents.json 配置意图/槽位及调用语料。routing-prompt.txt 仅用于平台确实提供的提示词入口；标准技能不能靠它修改全局原生助手。
3. 回调地址为 `https://你的域名/xiaomi/skill`。在平台安全配置取得 KeyId 与 Base64 SecretKey，在服务器秘密环境中设置 relay.env.example 所列字段。平台请求中的加密 user_id/device_id 必须与用户实际配对信息一致，不能用昵称或米家数字账号猜测。
4. 生产服务必须在受信任 HTTPS 反向代理后运行，内部仅监听 loopback。代码只信任默认受信任本地代理传递的 HTTPS 协议；不禁用证书验证、不绕过浏览器证书警告。实际证书是否被平台接受需要联调。

签名实现与小米公开 POST 向量匹配。当前版本只接收无 query string 的 JSON POST。为确保命令正文被签名保护，必须存在并核对 Content-MD5；如果真实平台请求省略该头，保持拒绝，不自行补头伪造通过，需要平台确认可用的正文保护方式。空 Scope 按官方示例处理，其他签名版本/Scope 需明确兼容后再支持。

## 编译与运行

```powershell
.\.tooling\dotnet\dotnet.exe build src\XiaoAI.SkillRelay -c Release
# 先配置本机服务环境变量或托管平台 secret，再运行：
.\.tooling\dotnet\dotnet.exe run --project src\XiaoAI.SkillRelay -c Release --no-build
```

STATE_PATH 必须是服务账号私有的持久目录。任务中含用户要求和结果，按敏感数据保护，不放到公开网站目录，不提交 Git。服务最多保留 64 个任务，活动队列最多 16 个；过期记录在后续提交时清理，仅处理自己的哈希文件名。

生成一个不少于 32 字符的随机电脑配对密钥，在服务器 WINDOWS_TOKEN 与 Windows 设置的“小爱官方技能”中保存相同值。电脑端只存 Windows 凭据管理器，不写 settings.json；程序也支持通过标准输入导入 `--import-xiaoai-relay-credential`，不把密钥放到命令行。开发者签名密钥只留在服务端，音箱和 Windows 模型 API Key 不交给桥接服务。

在 Windows 设置启用已配对连接并填写 HTTPS 根地址；电脑自动操作仍受当前 PowerShell 开关控制。电脑正忙时不领取任务。关闭连接会取消正在进行的音箱任务，原有本地语音/快捷键/截图继续可用。

## 完成状态与播报

回调短暂等待约 1.8 秒；期间完成则直接把真实结果放在 to_speak，由原生小爱播报。较慢任务明确返回“正在处理”，用户可以在技能中询问“查看结果”；不把接受任务说成完成。

没有把 HTTP 请求一直挂起到 AI 完成，也没有虚构平台后台推送能力。官方文档列有 playbacknearlyfinished 音频事件，但当前没有验证其在 OH2P 上能否安全续接长任务，因此没有默认启用等待音频循环或自动后台播报。此项需要真实平台和设备测试；当前不能保证长任务完成后无需再次询问就自动播报。

任务领取即持久标为 leased，不因网络丢失或重启自动重新执行。重启时未完成任务标为 unknown，提醒核对电脑，不猜测成功。Windows 回传结果可重试，原始电脑操作不会因此重试。尚无持久客户端结果 outbox：回传最终失败时，电脑保留当前执行界面，服务端状态会提示无法确认。

## QQ 范围

QQ 文件：用户选择实际下载目录后，监听创建/修改/改名，待大小与写入时间稳定且没有打开的写入句柄才报告“当前稳定可读取”。不读取内容，不推断发件人，不保证网络下载完整，不监听 .db/.db-wal/.db-shm。目录链接不跟随。

QQ 消息：通过 Windows UserNotificationListener 读取显示名为 QQ 的系统通知，需要用户在 Windows 的权限提示中允许。QQ 不发 Windows 通知、折叠/隐藏通知或撤销授权时无法据此读取全部消息；显示名也不等于强认证的账号身份。当前未取得读取个人 QQNT 全部聊天历史的公开官方接口证据，不解密聊天库、不抓登录凭据、不注入 QQ。

事件仅在内存保留最多 100 条；不会因事件自动执行命令或向 QQ 发消息。只有启用“允许已配对的小爱读取检测结果”并在音箱明确查询时才返回最近检测数据。历史记录是否持久化仍由现有对话历史设置控制。

## 验证

```powershell
.\.tooling\dotnet\dotnet.exe run --project tests\XiaoAI.SkillRelay.Tests -c Release
.\artifacts\xiaoai-official\tests\WindowsIntelligence.Tests.exe --official-xiaoai
```

服务器测试包含官方签名向量、正文篡改、未绑定账号、HTTP 交接/结果格式、重复请求和重启不重跑。Windows 测试使用模拟服务与 AI、真实无害 PowerShell，并在自己的临时目录测试写入稳定性与数据库排除；没有读取真实 QQ 聊天内容，没有在实际小米云上调用。

依据：[小爱 HTTPS 协议](https://developers.xiaoai.mi.com/documents/Home?type=%2Fapi%2Fdoc%2Frender_markdown%2FSkillAccess%2Fskill%2Ffulu%2FHTTPS)、[签名机制](https://developers.xiaoai.mi.com/documents/Home?type=%2Fapi%2Fdoc%2Frender_markdown%2FSkillAccess%2Fskill%2Ffulu%2FSignature)、[标准技能显式调用](https://developers.xiaoai.mi.com/documents/Home?type=%2Fapi%2Fdoc%2Frender_markdown%2FSkillAccess%2Fskill%2FCustomSkillsMain)、[Windows 通知权限](https://learn.microsoft.com/windows/uwp/design/shell/tiles-and-notifications/notification-listener)、[QQ 官方机器人范围](https://bot.q.qq.com/wiki/develop/api-v2/)。
