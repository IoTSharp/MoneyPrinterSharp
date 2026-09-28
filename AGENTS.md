# MoneyPrinter# 开发约定

- 产品展示名称为 `MoneyPrinter#`，简称 `MPS` 或 `MP#`；仓库名为 `MoneyPrinterSharp`，主仓库为 `https://github.com/IoTSharp/MoneyPrinterSharp`。
- 这是独立项目。修改只在本仓库进行，不将其他软件的源码、业务数据、生产配置或媒体复制进来。原 Gitee 仓库保留为迁移前历史。
- 实现使用 C# / .NET 10；所有方法和关键逻辑使用准确、简洁的中文注释。媒体处理调用 FFmpeg/FFprobe，不引入 Python 或 JavaScript 业务脚本。
- 在 Windows 使用 PowerShell 7，不使用 Windows PowerShell 5.1。工具发现只使用 PATH、已配置位置和小范围有界查询。
- 所有循环、轮询与批处理须有次数上限、总时间上限、取消机制；外部进程记录 PID、创建时间、命令行及父进程。只清理确认为本次任务创建的进程树和临时文件。
- 凭据留在 Windows Credential Manager，不在聊天、命令参数、任务记录、Git 或日志中回显。带签名的下载地址和供应商原始响应不得入库。
- 常规构建、测试、媒体回归使用本地样本，不提交付费生成请求。付费制作必须有当前任务的生成授权及预算；授权覆盖范围内不重复索要确认。超时不代表未扣费，应先查询已有任务。
- 保持现有技能 ID 与 `VideoProduction` 项目入口兼容；修改接口时同步维护技能说明与验证。
- 不使用 Graphify。
- 开始开发前阅读 `docs/development.md`；变更验证后按用户要求提交和推送，不强制改写远端历史。
