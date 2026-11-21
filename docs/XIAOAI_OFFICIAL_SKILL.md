# OH2P 无需刷机：小爱官方技能上下文

研究日期：2026-11-21。按用户最新要求停止固件写入，走官方技能。现已实现服务端协议、Windows 主动连接、既有工具执行和 QQ 检测，并通过本机模拟联调；尚未创建/发布真实技能或连接实际音箱。代码和配置步骤见 [官方技能桥接](../integrations/xiaoai/README.md)。

## 官方接口实际提供什么

小爱开放平台的 HTTPS 技能协议提供 `query`、`session.session_id`、`session.application.app_id`、`request.request_id`、`request.timestamp` 和可选 `context.device_id`。`session_attributes` 可在技能会话中返回上下文。文档描述的是请求命中技能后，平台把识别结果/意图/槽位发给开发者服务，并不是向 Windows 开放音箱所有原生对话或麦克风。

官方来源：[HTTPS 请求与回复协议](https://developers.xiaoai.mi.com/documents/Home?type=%2Fapi%2Fdoc%2Frender_markdown%2FSkillAccess%2Fskill%2Ffulu%2FHTTPS)、[自定义技能](https://developers.xiaoai.mi.com/documents/Home?type=%2Fapi%2Fdoc%2Frender_markdown%2FSkillAccess%2Fskill%2FCustomSkillsMain)、[签名机制](https://developers.xiaoai.mi.com/documents/Home?type=%2Fapi%2Fdoc%2Frender_markdown%2FSkillAccess%2Fskill%2Ffulu%2FSignature)。

因此可以研究保留原来的“小爱同学”，通过技能调用话术进入“电脑助手”，在技能上下文里连续下达电脑指令。普通天气、音乐、米家控制继续留在原生能力范围。具体调用话术和 OH2P/超级小爱支持范围需要开放平台和真机测试，当前不能承诺不加技能入口就自动取得所有电脑相关原生对话。

若用户所说的“上下文”实际指米家云端语音历史：那是另一条路径。当前没有取得面向本项目、经过验证的公开官方历史读取授权接口；不能把社区轮询接口冒称官方技能，也不能用轮询记录证明原生执行前接管或没有双重回复。本轮不恢复该轮询器。

## 集成路径

1. 平台把命中技能的请求发送到 HTTPS 服务；服务验证平台签名、技能 app_id、时效、request_id、防重放和账号/电脑配对。不从未经校验的 session/context 字段直接取得电脑控制权限。
2. Windows 端通过主动出站的加密、认证连接取得已配对任务，不公开任意 PowerShell HTTP 端口。现有 AI 配置、Agent、工具权限和操作界面继续复用，执行过程显示在电脑上。
3. 单次任务由官方技能会话负责回复；短请求的实际结果可通过 `response.to_speak` 返回，让原生小爱播报，不占用音箱麦克风、不替换语音进程。慢任务必须先确认平台真实响应时限与续接能力，不能超时后盲目重试有副作用的电脑命令。
4. 不把云回调当作原生 ASR 补丁的交接回执。两种入口的认证/所有权规则必须分别验证；现有补丁原型的 A/B/C 开关不能直接被置为通过。

服务端 HMAC、任务交接和回复、Windows 配对连接代码已实现，但服务尚未上线。用户已经在 Edge 登录；窗口工具当前仍无法读取页面，因此尚未创建开发者技能和凭据或部署 HTTPS 服务。平台签名实测、OH2P 能力和真机对话/播报仍待完成。无需为这条路线提供刷机模式、活动槽位或 SSH 密码。

## 验收要求

- 命中“电脑助手”的指令在电脑执行一次；每一步的实际命令与结果在 Windows 可见。
- 非技能请求仍由原生小爱处理，退出技能后正常音乐/米家控制可用。
- 账号未配对、伪造签名、过期/重复请求、电脑离线均不能执行新任务。
- 请求/会话/配对隔离，断网重连不重放已经执行的操作。
- 只播报实际结果，长任务不虚报完成；原生播放器恢复与会话退出需实测。

这些设备验收目前均未完成。此方案不需要固件补丁，也不代表已经实现无入口话术的全局自动双 AI 路由。
