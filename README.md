# RemoteOpsTool

RemoteOpsTool 是一个面向受限域环境的 Windows WPF 远程运维工具。它不依赖常驻 Agent、中心服务或 WinRM，通过 WMI/DCOM、PsExec 和计划任务等通道完成远程查询、命令执行、脚本运行、服务/注册表/打印机/软件管理及文件磁盘操作。

当前源码版本：`1.4.28`。

## 主要能力

- 目标主机连通性、能力快照和传输路由学习。
- WMI/DCOM 优先的结构化查询与安全回退。
- 使用指定凭据 RunAs 启动 PsExec，并向目标主机显式传递凭据。
- 交互程序、脚本、终端命令和远程文件上传执行。
- 系统、进程、服务、注册表、软件、网络、打印机和设备管理。
- 磁盘信息、清理空间、环境变量和远程会话操作。

## 环境要求

- Windows 10/11 x64
- .NET SDK 10
- 推荐使用 PowerShell 7
- 对目标主机具备相应网络、凭据和远程管理权限

## 目录结构

```text
RemoteOpsTool/
├── .agents/                         # Codex/开发辅助配置
├── docs/                            # 架构、设计、测试和评估文档
│   └── internal/                    # 内部验证参考代码
├── src/RemoteOpsTool/               # WPF 应用主体
│   ├── Assets/Fonts/                # 实际引用的字体与许可证
│   ├── Services/                    # 业务、能力和传输路由
│   ├── ViewModels/                  # MVVM 状态与命令
│   └── Views/                       # XAML 窗口、控件和资源
├── tests/RemoteOpsTool.Tests/       # xUnit 自动化测试
├── tests/scripts/                   # 手工验证和诊断脚本
├── tools/                           # 运维辅助脚本
├── .gitattributes                   # 文本换行和二进制文件规则
├── .gitignore                       # 构建、发布、IDE、日志和秘密文件规则
├── RemoteOpsTool.slnx               # 解决方案
└── skills-lock.json                 # 开发辅助技能锁文件
```

## 构建和测试

```powershell
dotnet restore .\RemoteOpsTool.slnx

dotnet build .\RemoteOpsTool.slnx -c Debug --no-restore
dotnet test .\RemoteOpsTool.slnx -c Debug --no-restore

dotnet build .\RemoteOpsTool.slnx -c Release --no-restore
dotnet test .\RemoteOpsTool.slnx -c Release --no-restore
```

## 发布

```powershell
dotnet publish .\src\RemoteOpsTool\RemoteOpsTool.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\publish\_staging
```

`publish/` 中的 EXE、MSI、压缩包和其他发布产物不会提交到 Git。正式二进制应通过 GitHub Releases 或内部制品库发布。

## 文档

- [架构说明](docs/Architecture.md)
- [界面设计](docs/DESIGN.md)
- [功能测试文档](docs/功能测试文档.md)
- [性能基准测试](docs/性能基准测试.md)
- [远程执行命令路由优化评估报告](docs/远程执行命令路由优化评估报告.md)

## 安全约定

- 不提交密码、令牌、私钥、PFX/P12 证书、生产配置或凭据文件。
- 不提交 `publish/`、`bin/`、`obj/`、测试结果、日志或崩溃转储。
- PsExec 相关代码必须同时满足本地 RunAs 启动和向目标主机显式传递 `-u/-p` 凭据。
- 字体和第三方组件文件应保留其原始许可证文本。
