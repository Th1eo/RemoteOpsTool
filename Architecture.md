# RemoteOpsTool Architecture

RemoteOpsTool 是一个面向受限域环境的 WPF 运维工具。它假设程序以普通域用户启动，但可以在工具内选择一组高权限运维凭据；目标主机通常不启用 WinRM、不启用远程注册表服务，也不部署长期 Agent 或中心服务器。

当前架构的核心原则是：

- 先识别目标是否为本机；本机操作绝不通过 PsExec 自连接。
- 本机后台命令使用本地进程/API；本机交互 GUI 使用当前桌面的 UAC 提权流程。
- `CapabilityService` 按 `host + username + password 指纹` 缓存 `CapabilitySnapshot`，默认 TTL 5 分钟；同一键的并发探测会合并，手动刷新和失效可绕过缓存。
- 每个顶层操作通过 `IRemoteExecutionService.CreateSessionAsync()` 获取一次快照，并在该 session 内固定复用；查询类操作使用 `WMI/DCOM → PsExec`，普通命令默认使用 `PsExec → WMI/DCOM`，有凭据且命令超过 700 字符时切换为 `WMI/DCOM → PsExec`，`InteractiveLaunch` 使用 `PsExec → WMI/DCOM → ScheduledTask`。
- 只有 `TransportResult.IsTransportFailure == true` 才允许回退；远端命令已经启动后返回非零退出码属于命令失败，绝不通过另一通道重放。
- 只要提供运维凭据，所有 PsExec 路径都必须同时满足“用所选凭据 RunAs 启动本地 PsExec”和“命令行显式传入 `-u/-p`”；交互 PsExec 固定使用默认 `PSEXESVC`、`-h -n -w -i <session> -d`，不使用 `-r`、不使用 `-s`。
- 所有远程能力保持无常驻 Agent、无中心服务、无永久服务注入。
- UI 使用 WPF + MVVM，业务逻辑集中在 Services，窗口只负责显示和少量 UI 事件。

## 1. 技术栈

| 类别 | 选型 | 说明 |
| --- | --- | --- |
| 运行时 | .NET 10 / `net10.0-windows` | Windows 桌面应用，WPF 原生支持 |
| UI | WPF | 主窗口、弹窗、DataGrid、TreeView、RichTextBox |
| MVVM | CommunityToolkit.Mvvm | `ObservableProperty`、`RelayCommand` |
| DI | Microsoft.Extensions.DependencyInjection | 在 `App.xaml.cs` 注册单例服务和主窗口 |
| WMI | System.Management | WMI/DCOM 查询和操作 |
| 图标 | Svg.Skia + ICO | 主窗口 SVG/ICO 资源 |
| 配置 | JSON 文件 | `%AppData%\RemoteAdmin\settings.json` |
| 凭据加密 | Windows DPAPI CurrentUser | `%AppData%\RemoteAdmin\credentials.dat` |
| 发布 | SingleFile + SelfContained + ReadyToRun | `win-x64` 单文件发布 |

NuGet 依赖见 [RemoteOpsTool.csproj](src/RemoteOpsTool/RemoteOpsTool.csproj)。

## 2. 项目结构

```text
src/RemoteOpsTool/
├── App.xaml / App.xaml.cs
├── RemoteOpsTool.csproj
├── Constants/
│   └── AppConstants.cs
├── Converters/
│   ├── BoolToVisibilityConverter.cs
│   ├── ConnectionStatusToColorConverter.cs
│   └── LogLevelToColorConverter.cs
├── Helpers/
│   ├── CredentialMasker.cs
│   ├── DataGridRowClickHelper.cs
│   ├── HostHelper.cs
│   ├── NativeProcessHelper.cs
│   ├── NetworkPathHelper.cs
│   ├── NetworkShareCredentialHelper.cs
│   ├── ProcessHelper.cs
│   ├── RegistryHelper.cs
│   ├── RemoteErrorClassifier.cs
│   ├── RemoteWmiHelper.cs
│   ├── SessionHelper.cs
│   ├── SvgImageHelper.cs
│   └── WindowHelper.cs
├── Models/
│   ├── AppSettings.cs
│   ├── CommandResult.cs
│   ├── CredentialInfo.cs
│   ├── DeviceInfo.cs
│   ├── DiskInfo.cs
│   ├── EnvVariableInfo.cs
│   ├── LogEntry.cs
│   ├── NetworkConnectionInfo.cs
│   ├── PrinterInfo.cs
│   ├── ProcessDetailInfo.cs
│   ├── ProcessInfo.cs
│   ├── RegistryTreeNode.cs
│   ├── RemoteCapabilityInfo.cs
│   ├── ServiceInfo.cs
│   ├── SoftwareInfo.cs
│   ├── SystemInfoData.cs
│   └── UserSessionInfo.cs
├── Services/
│   ├── Capability/
│   │   ├── CapabilityMatrix.cs
│   │   ├── CapabilityService.cs
│   │   ├── CapabilitySnapshot.cs
│   │   ├── OperationCapability.cs
│   │   └── RouteLearningStore.cs
│   ├── Transports/
│   │   ├── IRemoteCommandExecutor.cs
│   │   ├── RemoteCommand.cs
│   │   ├── RemoteExecutionService.cs
│   │   ├── RemoteOperationKind.cs
│   │   ├── RemoteTransportKind.cs
│   │   ├── TransportFailureClassifier.cs
│   │   ├── TransportProbeService.cs
│   │   └── TransportResult.cs
│   ├── Interfaces/
│   ├── CredentialService.cs
│   ├── DameWareService.cs
│   ├── DeviceService.cs
│   ├── EnvVarService.cs
│   ├── FileDiskService.cs
│   ├── LogService.cs
│   ├── NetworkService.cs
│   ├── PrinterService.cs
│   ├── PsExecService.cs
│   ├── ServiceManagerService.cs
│   ├── SettingsService.cs
│   ├── SoftwareService.cs
│   ├── SystemInfoService.cs
│   └── ToolSetupService.cs
├── ViewModels/
│   ├── MainViewModel.cs
│   ├── ConnectionViewModel.cs
│   ├── CredentialViewModel.cs
│   ├── FileDiskViewModel.cs
│   ├── InteractiveViewModel.cs
│   ├── LogViewModel.cs
│   ├── NetworkViewModel.cs
│   ├── RemoteManagementViewModel.cs
│   ├── SettingsViewModel.cs
│   ├── StatusBarViewModel.cs
│   ├── TerminalViewModel.cs
│   └── Dialogs/
└── Views/
    ├── MainWindow.xaml / MainWindow.xaml.cs
    ├── Dialogs/
    └── Resources/
        ├── Styles.xaml
        ├── Tools.ico
        └── Tools.svg
```

测试项目位于 `tests/RemoteOpsTool.Tests/`，当前主要用于覆盖辅助解析和基础服务行为。

## 3. 分层设计

```text
Views
  WPF XAML、窗口布局、右键菜单、拖拽、少量纯 UI 事件

ViewModels
  状态、命令、选择项、筛选、加载进度；协调 Services

Services
  远程查询/操作、配置读写、凭据管理、日志、外部工具封装

Helpers
  Process/WMI/SMB/路径/会话/脱敏/窗口状态等通用基础设施

Models
  纯数据对象或简单 ObservableObject 行模型
```

关键约束：

- View 不直接做远程操作。
- ViewModel 不直接拼复杂外部工具流程，复杂流程放到 Service。
- Service 不依赖具体窗口。
- 远程命令日志必须走脱敏。
- 远程操作默认异步执行，避免阻塞 UI 线程。

## 4. 启动流程

入口在 [App.xaml.cs](src/RemoteOpsTool/App.xaml.cs)。

```text
App.OnStartup
  ├─ 注册 DI 服务
  ├─ 并行读取 Settings 和 Credentials
  ├─ 根据 DebugMode 启用文件日志
  ├─ 创建并显示 MainWindow
  └─ 后台检查 PsExec 工具是否存在
```

启动优化点：

- `SettingsService.LoadAsync()` 与 `CredentialService.LoadAsync()` 并行执行。
- PsExec 工具检测放到主窗口显示之后后台执行，避免首屏被工具检查或下载阻塞。
- 主窗口 `Loaded` 后才启动时间刷新、磁盘状态刷新等 UI 辅助任务。

## 5. 依赖注入

所有核心服务在启动时注册为 Singleton：

| 接口 | 实现 | 职责 |
| --- | --- | --- |
| `ISettingsService` | `SettingsService` | JSON 配置读写 |
| `ICredentialService` | `CredentialService` | 凭据保存、选择、DPAPI 加解密 |
| `ILogService` | `LogService` | 内存日志、筛选、文件日志 |
| `ITransportProbeService` | `TransportProbeService` | 无状态探测 ICMP、端口、SMB、WMI、`query user`、PsExec、计划任务 RPC |
| `ICapabilityService` | `CapabilityService` | 缓存并按主机/凭据复用 `CapabilitySnapshot`，TTL 内合并探测并记录操作级学习路由 |
| `IRemoteCommandExecutor` | `PsExecService` | 严格单通道 raw executor；不探测、不重试、不 fallback |
| `IRemoteExecutionService` | `RemoteExecutionService` | 按顶层操作创建 session、选择回退链、复用固定快照 |
| `IPsExecService` | `PsExecService` | PsExec 参数构造、RunAs、流式输出、本机与旧调用兼容入口 |
| `ITaskSchedulerService` | `TaskSchedulerService` | 原生 schtasks /s 任务计划 RPC 交互兜底 |
| `IToolSetupService` | `ToolSetupService` | PsTools 检测、下载、签名检查 |
| `IDameWareService` | `DameWareService` | DameWare 远控启动 |
| `IFileDiskService` | `FileDiskService` | 管理共享、磁盘信息、清理 |
| `IDeviceService` | `DeviceService` | 设备列表、启用、禁用、卸载 |
| `IServiceManagerService` | `ServiceManagerService` | 服务列表和服务启停 |
| `IPrinterService` | `PrinterService` | 打印机列表和操作 |
| `ISoftwareService` | `SoftwareService` | 软件列表、卸载、注册表清理 |
| `IEnvVarService` | `EnvVarService` | 系统/用户环境变量 |
| `ISystemInfoService` | `SystemInfoService` | 系统硬件、网络、补丁信息 |
| `INetworkService` | `NetworkService` | Ping、端口、进程、会话、连接 |

`MainViewModel` 和 `MainWindow` 也是 Singleton。各功能窗口的 ViewModel 通常在打开窗口时手动创建，并注入所需 Service。

## 6. 远程执行策略

### 6.1 主机级能力缓存与传输路由学习

`TransportProbeService` 负责无状态探测 ICMP、TCP 445/135/5985、`ADMIN$`、WMI/DCOM、`query user`、PsExec `whoami` 和 `schtasks /Query`，不通过 `RemoteExecutionService` 或 `PsExecService` 的策略入口递归调用。Ping 和各端口检查并发执行，管理类探测受控并发上限为 3；`ADMIN$` 检查完成后才启动 PsExec 探测，避免 SMB 凭据会话竞争。探测结果按固定顺序返回，避免并发完成时序影响能力矩阵。

`CapabilityService` 将探测结果和传输学习状态封装为 `CapabilitySnapshot`：

- 缓存键为规范化主机、用户名和密码 SHA-256 指纹；默认 TTL 5 分钟，同一键的并发 `ProbeAsync()` 由 `SemaphoreSlim` 合并，`RefreshAsync()` 强制重探，`Invalidate()` 立即移除。
- 快照内的能力状态按 `(Operation, Transport)` 隔离，记录 `Health`、`FailureKind`、`CheckedAt` 和 `ExpiresAt`；例如 `Command + PsExec` 失败不会误伤 `InteractiveLaunch + PsExec`。
- 能力状态 TTL 按失败分类动态设置：成功 5 分钟，瞬时/网络/超时失败 30 秒，权限或认证失败 10 分钟，禁用、未找到、远端命令失败等硬失败 30 分钟。
- 路由学习记录按规范化主机、凭据 SHA-256 指纹、operation 和 transport 统计成功/失败次数、最近成败时间和平均耗时；记录持久化到 `%AppData%\RemoteAdmin\route-learning.json`，最多 4096 条，只保存凭据指纹，不保存密码或命令文本。
- 快照包含可用/不可用通道、原始探测结果和学习到的首选路由；`RecordTransportSuccess()` 会写回成功通道，传输失败会更新对应 operation 的 `Health`/`FailureKind` 状态。
- 每个顶层操作只通过 `IRemoteExecutionService.CreateSessionAsync()` 获取一次快照；清理空间的多步骤、分片上传和删除复核都复用同一个 session 快照。
- `RemoteExecutionService.ExecuteOnceAsync()` 是单次操作便捷入口，仍会按缓存规则取得快照；需要强一致步骤序列的调用方必须显式创建 session。

### 6.2 通道策略与回退规则

`IRemoteCommandExecutor` 是严格单通道 raw executor。`PsExecService` 实现该接口后，只执行调用方指定的 `PsExec`、`WMI/DCOM` 或计划任务通道，不自行探测、不自行重试、不自行 fallback。探测、重试策略、通道排序和结果分类全部由 `RemoteExecutionService`/`RemoteExecutionSession` 负责。

| 操作类型 | 回退顺序 | 说明 |
| --- | --- | --- |
| `Command` | `PsExec → WMI/DCOM`（超长有凭据负载为 `WMI/DCOM → PsExec`） | 统一凭据执行通道，WMI 作为高效查询和安全回退 |
| `InteractiveLaunch` | `PsExec → WMI/DCOM → ScheduledTask` | 需要真实桌面会话的 GUI/管理入口 |
| `Inventory` | `WMI/DCOM → PsExec` | 设备、服务、打印机、系统信息等结构化查询 |
| `RegistryRead` | `WMI/DCOM → PsExec` | StdRegProv 失败后再尝试远程命令注册表读取 |
| `RegistryWrite` | `WMI/DCOM → PsExec` | 写入失败不得因命令非零而重放 |

回退规则：

1. 只依据 `TransportResult.IsTransportFailure` 决定是否进入下一通道。
2. 远端命令正常启动并返回非零退出码时，结果是 `CommandFailure`，立即返回给上层，绝不换通道重放，避免清理、卸载、注册表写入等副作用被重复执行。
3. 所有已声明可用通道都传输失败时，返回组合错误，并保留每个通道的原始错误摘要。
4. 某个 operation 成功后，session 会立即优先复用该通道，同时通过 `CapabilitySnapshot.RecordTransportSuccess()` 写入主机级学习路由；失败时按 operation-specific TTL 记录冷却并移除 session 首选，但保留其回退资格。
5. 路由学习结果只影响首选排序，不裁剪回退链，也不得绕过 `CapabilityMatrix` 的安全边界。
6. PsExec 支持实时输出；WMI/DCOM 只能在命令完成后批量回放 stdout/stderr。

### 6.3 PsExecService

[PsExecService](src/RemoteOpsTool/Services/PsExecService.cs) 同时提供两种边界：

- `IRemoteCommandExecutor` 的 `ExecutePsExecOnlyAsync`、`ExecuteWmiOnlyAsync`、`ExecuteInteractivePsExecOnlyAsync`、`ExecuteInteractiveWmiOnlyAsync`、`ExecuteInteractiveScheduledTaskOnlyAsync` 是 raw-only 方法，供 `RemoteExecutionService` 使用。
- `IPsExecService` 的本机执行和旧调用方法保留兼容性，但新的远程功能不得绕过 session 直接进行通道选择。

主要行为：

- 优先选择 `PsExec64.exe` 或 `PsExec.exe`，日志中的密码由 `CredentialMasker` 隐藏。
- 只要提供凭据，所有后台、流式、交互 PsExec 都同时显式携带 `-u/-p`，并由所选凭据 RunAs 启动本地 PsExec；两者缺一不可。
- 后台/流式自定义 `-r` 被 SCM/EDR 拒绝时，只把服务名恢复为默认 `PSEXESVC`，恢复链不得删除 `-u/-p`，也不得改用无凭据 RunAs。
- 交互 GUI 使用 `-h -n <timeout> -w <working-directory> -i <session> -d`，仅执行一次；有凭据时仍由所选凭据 RunAs 启动并保留显式 `-u/-p`，不进入通用服务名恢复链。交互启动固定按 `PsExec → WMI/DCOM → ScheduledTask` 路由，只有前一个通道属于传输失败时才进入下一个通道。
- `compmgmt.msc`、`printmanagement.msc`、`appwiz.cpl` 等先规范化为 `mmc.exe ...` / `control.exe ...`，再以直接命令形态启动。
- `cmd.exe`、`powershell.exe`、`pwsh.exe` 入口按直接程序处理，避免二次套壳；普通 `regedit.exe`、`notepad.exe` 等 GUI 程序也强制 `Direct + WrapCmd=false`。
- 只有明确的 PowerShell 脚本语句（例如 `Get-Process`、包含 `$`/管道/脚本体）才使用 PowerShell host；UI 当前选择的 shell 不会污染 GUI 启动形态。
- 后台/流式命令可使用隔离 `-r RemoteOpsTool_<host>_<pid>_<counter>`；有凭据且负载超过 700 字符时由路由层优先使用 WMI/DCOM，PsExec 仅作为回退。

典型交互形态：

```text
PsExec \\HOST -u DOMAIN\admin -p ******** -accepteula -nobanner -h -n 10 -w C:\Windows\System32 -i 4 -d cmd.exe
```

所有携带凭据的 PsExec 调用都保留显式 `-u/-p`；不再提供“RunAs 后省略凭据”的模式，并对日志统一脱敏。

### 6.4 WMI/DCOM

结构化查询、注册表 Provider、磁盘信息、服务/设备/打印机枚举优先使用 WMI/DCOM。WMI 连接由 `RemoteWmiHelper.CreateScope()` 统一创建，使用传入的运维凭据连接 `\\host\root\cimv2` 或注册表 Provider。磁盘查询直接读取 `Win32_LogicalDisk`，单次 8 秒超时，成功后进入 20 秒短缓存；WMI 不可用时才通过统一 `Inventory` session 回退，避免每次查询都启动 PsExec 或 PowerShell 引导进程。

WMI 命令通道通过一次性注册表任务启动并收集结果，适合较长 PowerShell 负载；它仍然必须通过 `RemoteExecutionSession` 进入 `CapabilityMatrix` 选择的阶段，不得在功能 Service 内私自重试。

### 6.5 SMB 连接

`NetworkShareCredentialHelper` 用于建立和清理由本工具创建的 SMB 连接，例如：

- `\\HOST\ADMIN$`
- `\\HOST\C$`
- `\\HOST\D$`
- `\\HOST\Users\Public\Desktop`

断开连接时，`MainViewModel.Disconnect()` 会调用 `NetworkShareCredentialHelper.DisconnectAll()` 清理本工具建立的连接。SMB 失败属于分层传输结果，调用方可以回退到命令上传，但上传的每一个分片必须复用同一个顶层 session 和快照。

## 7. 功能模块

| UI 功能 | ViewModel | Service | 主要通道 |
| --- | --- | --- | --- |
| 凭据管理 | `CredentialViewModel` | `CredentialService` | 本地 DPAPI/JSON |
| 连接和 Ping | `ConnectionViewModel` | `NetworkService` | ICMP |
| DameWare 远控 | `ConnectionViewModel` | `DameWareService` | 外部程序 |
| 文件/磁盘入口 | `FileDiskViewModel` | `FileDiskService` | SMB、WMI、PsExec |
| 清理空间 | `DiskCleanupViewModel` | `FileDiskService` | 本机直接/UAC；远程超长批量脚本（有凭据）走 WMI/DCOM → PsExec 兜底，普通短命令走 PsExec → WMI/DCOM，删除后复核 |
| 磁盘信息 | `DiskInfoViewModel` | `FileDiskService` | WMI 优先 |
| 设备管理 | `DeviceManagerViewModel` | `DeviceService` | WMI 优先、PsExec 兜底 |
| 服务管理 | `ServiceManagerViewModel` | `ServiceManagerService` | WMI 优先、PsExec 兜底 |
| 服务属性 | `ServicePropertiesViewModel` | `RemoteExecutionService`（`ServiceManagerService` 业务逻辑） | WMI 优先，sc/PsExec 经统一 session 兜底 |
| 打印机管理 | `PrinterManagerViewModel` | `PrinterService` | WMI 优先、PsExec 兜底 |
| 软件管理 | `SoftwareManagerViewModel` | `SoftwareService` | WMI/注册表/PsExec |
| 环境变量 | `EnvVarViewModel` | `EnvVarService` | StdRegProv、PsExec |
| 系统信息 | `SystemInfoViewModel` | `SystemInfoService` | WMI 优先、PsExec 兜底 |
| 注册表 | `RemoteRegistryViewModel` | `PsExecService` + WMI | StdRegProv、reg.exe |
| 进程管理 | `ProcessListViewModel` | `NetworkService` | WMI、tasklist、PsExec |
| 网络连接 | `NetworkPortsViewModel` | `NetworkService` | netstat、tasklist、WMI |
| 命令终端 | `TerminalViewModel` | `RemoteExecutionService`（raw executor：`PsExecService`） | 本机直接；远程普通命令默认 PsExec → WMI/DCOM，超长有凭据负载为 WMI/DCOM → PsExec；管理 GUI 自动转 `InteractiveLaunch`，按 PsExec → WMI/DCOM → ScheduledTask 回退 |

> 远程执行边界：表中除本机操作和纯 SMB 文件传输外，所有远程命令、注册表写入、脚本上传分片和交互启动都必须通过 `RemoteExecutionService`；`PsExecService` 只作为 raw executor 执行已经选定的单通道。

### 7.1 缓存策略

缓存仅用于远程查询成本高、短时间内可接受快照展示的数据。进程、端口、会话、Ping、文件共享访问和命令执行属于活动状态或操作结果，始终实时查询目标主机，不落持久缓存。磁盘剩余空间为降低状态栏刷新成本，允许 20 秒短缓存；主窗口默认每 30 秒刷新一次，使用 single-flight 防止重叠查询，失败按 15/30/60/120 秒指数退避，切换主机时取消旧查询。能力探测不进入通用 `CacheService`，而由 `CapabilityService` 按 `host + username + password指纹` 保存 5 分钟主机级快照；同一顶层操作只取得一次快照并在 session 内复用，传输成功/失败会更新该快照的 operation-specific 状态和持久化路由学习记录。

缓存键统一由 `CacheKeys` 生成，TTL 由 `CacheService` 统一维护。动态键家族可用 `InvalidateByPrefix()` 整组失效；未登记的新缓存键按 5 分钟保底 TTL 处理，避免遗漏策略后形成永久旧缓存。

| 数据 | 策略 | TTL | 必须失效/刷新 |
| --- | --- | --- | --- |
| 服务列表与状态 | 短快照缓存 | 15 秒 | 启动、停止、重启、服务属性窗口关闭、手动刷新 |
| 打印机清单、共享、默认状态 | 短快照缓存 | 1 分钟 | 删除、共享切换、默认切换、原生属性窗口/添加向导入口、手动刷新 |
| 注册表当前键值 | 极短快照缓存 | 30 秒 | 值增删改、键增删改影响当前路径、缓存到期后重新导航 |
| 环境变量 | 目标范围快照缓存 | 2 分钟 | 当前系统/用户变量增删改、手动刷新 |
| 设备列表 | 快照缓存 | 5 分钟 | 启用、禁用、卸载、驱动更新、手动刷新 |
| 系统信息 | 组合快照缓存 | 5 分钟 | 过期后重新打开窗口回查；动态网络状态使用对应实时功能 |
| 软件清单 | 长快照缓存 | 15 分钟 | 静默/交互卸载、深度注册表清理、手动刷新；普通和深度键整组失效 |
| 磁盘容量 | 极短快照缓存 | 20 秒 | 清理空间后失效；主窗口刷新采用 single-flight 和失败退避 |

| 实时数据 | 原因 |
| --- | --- |
| 进程列表、进程窗口、登录会话 | 秒级变化，界面支持主动/自动刷新 |
| 网络端口和活动连接 | 连接生命周期短，进程终止后必须立即反映 |
| Ping、连通性能力探测 | 表示当前网络与通道能力 |
| 磁盘清理结果 | 清理操作结果与空间变化直接相关 |
| 远程命令、脚本上传、共享路径访问 | 属于操作执行，不是可复用查询数据 |

## 8. UI 架构

### 8.1 主窗口

[MainWindow](src/RemoteOpsTool/Views/MainWindow.xaml) 是单窗口工作台布局：

- 顶部状态栏：目标主机、连接状态、磁盘容量、本地时间。
- 左侧功能区：连接、文件磁盘、远程管理、交互程序、网络工具、高级设置。
- 右侧上方：实时日志，支持筛选、复制、清空、右键菜单。
- 右侧下方：远程命令终端，支持多行命令、脚本加载、脚本拖拽、交互执行。

主窗口 code-behind 处理纯 UI 行为：

- 日志 RichTextBox 追加和重建。
- 命令输入框 Enter 执行。
- 脚本拖拽。
- 状态灯动画。
- 目标主机输入后的延迟连通性检测。

### 8.2 子窗口

子窗口集中在 `Views/Dialogs/`：

- DataGrid 类窗口：设备、服务、打印机、软件、环境变量、磁盘、进程/端口。
- Tree/List 类窗口：远程注册表。
- 表单类窗口：设置、凭据、服务属性、输入确认。

当前 UI 主题集中在 [Styles.xaml](src/RemoteOpsTool/Views/Resources/Styles.xaml)：

- 深色运维工作台风格。
- 统一 DataGrid 表头高度、行高、分割线、选中亮蓝色。
- 统一右键菜单样式。
- 常用按钮样式按用途区分：主操作、成功、警告、错误等。

### 8.3 DataGrid 交互

`DataGridRowClickHelper` 统一处理多表格行点击行为：

- 左键点击行时切换 `IsChecked`。
- 右键点击行时选中该行，并写入 ViewModel 的 `RightClickedRow` 或 `RightClickedSessionRow`。
- 右键菜单命令优先作用于右键行，其次作用于勾选行。

## 9. 配置和持久化

### 9.1 路径

| 类型 | 路径 |
| --- | --- |
| 应用数据目录 | `%AppData%\RemoteAdmin` |
| 设置文件 | `%AppData%\RemoteAdmin\settings.json` |
| 凭据文件 | `%AppData%\RemoteAdmin\credentials.dat` |
| 路由学习文件 | `%AppData%\RemoteAdmin\route-learning.json` |
| 默认 PsTools 目录 | `C:\ProgramData\RemoteAdmin\Tools` |

### 9.2 AppSettings

[AppSettings](src/RemoteOpsTool/Models/AppSettings.cs) 当前字段：

| 字段 | 说明 |
| --- | --- |
| `PsToolsPath` | PsExec/PsExec64 所在目录 |
| `DameWarePath` | DameWare 远控程序路径 |
| `DomainPublicPath` | 域公共目录路径 |
| `CustomCleanupDirectories` | 清理空间自定义目录 |
| `DebugMode` | 是否输出 Debug 和文件日志 |
| `PreferPsExec64` | 是否优先使用 PsExec64 |

| `PsExecConnectTimeoutSeconds` | PsExec `-n` 连接超时 |
| `PsExecRemoteWorkingDirectory` | PsExec `-w` 远端工作目录 |
| `PsExecServiceNamePrefix` | PsExec 临时服务名前缀 |
| `WindowLeft/Top/Width/Height/StateValue` | 主窗口位置和大小 |

### 9.3 凭据

`CredentialService` 使用 JSON 文件保存凭据元数据，密码使用 Windows DPAPI `DataProtectionScope.CurrentUser` 加密。

特点：

- 凭据文件复制到其他 Windows 用户或其他主机后无法直接解密。
- 解密仅在执行远程操作时发生。
- 日志输出使用 `CredentialMasker` 对密码脱敏。
- 当前只允许一个“已选运维凭据”作为默认操作凭据。

## 10. 日志系统

[LogService](src/RemoteOpsTool/Services/LogService.cs) 提供：

- 内存日志集合。
- `Info`、`Warn`、`Error`、`Debug` 级别。
- 日志过滤。
- 清空和重建事件。
- DebugMode 下文件日志。
- `IsExecuting` 状态，用于主窗口执行进度条。

主窗口的日志区不是简单 TextBox，而是 RichTextBox 逐段追加，按日志级别显示不同颜色。

## 11. 线程和异步模型

常见异步路径：

```text
用户点击按钮
  └─ RelayCommand async 方法
      ├─ 设置 IsLoading / IsExecuting
      ├─ 调用 Service
      │   ├─ WMI/DCOM 查询
      │   ├─ ProcessHelper 启动本地进程
      │   ├─ RemoteExecutionService 依据固定 CapabilitySnapshot 选择通道
      │   └─ PsExecService（raw executor）或 WMI/DCOM 执行
      ├─ 解析输出到 Model
      └─ 更新 ObservableCollection / ObservableProperty
```

注意点：

- UI 状态通过 `ObservableProperty` 通知。
- 长耗时操作使用 `async/await`，避免直接阻塞 UI 线程。
- 外部进程由 `ProcessHelper` 统一封装。
- PsExec 通道支持实时逐行输出；WMI/DCOM 回退在命令完成后批量回放 stdout/stderr。
- 部分窗口构造后立即后台加载，例如进程管理、系统信息、磁盘信息。
- 注册表窗口初始化在后台执行，避免窗口刚打开又关闭时锁住按钮。

## 12. 安全边界

### 12.1 本工具做了什么

- 使用用户选择的运维凭据访问目标主机。
- 本机通过本地进程/API/UAC 执行；远程通过 WMI/DCOM、SMB、PsExec 临时服务执行管理操作。
- 将密码本地 DPAPI 加密保存。
- 在日志中隐藏命令行密码。
- 后台/流式 PsExec 临时服务名默认使用 `RemoteOpsTool_<host>_<pid>_<counter>` 模式以避免并发冲突；自定义服务名被目标 SCM/EDR 拒绝时，只在保留 RunAs、显式 `-u/-p` 的前提下切换到默认 `PSEXESVC`。交互 GUI 使用默认 `PSEXESVC` 和单次直接启动，不使用 `-r`、不使用 `-s`。

### 12.2 本工具不做什么

- 不部署常驻 Agent。
- 不搭建中心服务器。
- 不依赖 WinRM。
- 不启用或要求目标主机远程注册表服务。
- 不在目标主机注入永久服务。
- 不做完整审计/回滚系统。

### 12.3 本机 UAC 与远程回退边界

- 保存的密码不能安全、可靠地静默注入当前桌面的 UAC 安全桌面；本机计算机管理、程序和功能等 GUI 仍通过当前桌面 UAC 启动。
- PsExec 是带凭据远程命令的统一执行通道候选；WMI/DCOM 保留为高效查询通道和安全回退。无 PsExec、PSEXESVC 被阻止或 ADMIN$/SCM 不可用时，后台命令和部分交互程序仍可回退到 WMI/DCOM。
- WMI/DCOM 与计划任务 RPC 回退依赖目标主机的 WMI/DCOM、RPC、Task Scheduler 服务、防火墙、权限和应用控制策略；回退失败时必须保留原始通道错误和回退错误，便于定位。
### 12.4 PsExec 提示说明

PsExec 输出中的 `Copying authentication key to HOST...` 是 PsExec 自身的握手/认证材料提示，不表示日志中泄露了明文密码。程序侧仍需注意：

- 不在日志中输出明文密码。
- 不对后台、流式或交互路径省略命令 `-u/-p`：PsExec 必须由所选凭据 RunAs 启动，同时显式传入目标凭据；日志中的密码始终由 `CredentialMasker` 隐藏。
- PsExec 本身会在目标主机创建临时服务，这是其工作机制，不是长期 Agent。

## 13. 外部工具

| 工具 | 是否必需 | 用途 |
| --- | --- | --- |
| PsExec.exe / PsExec64.exe | 可选统一执行通道 | 远程命令、脚本、交互程序；始终为有凭据调用启用 RunAs + `-u/-p`，不可用时回退 WMI/DCOM，再回退计划任务 RPC |
| schtasks.exe | 系统内置 | 原生任务计划 RPC 通道，用于 GUI 交互启动的第三兜底 |
| DameWare 远控程序 | 可选 | 远程桌面控制 |
| Windows 内置命令 | 必需 | `cmd`、`powershell`、`sc`、`reg`、`query`、`tasklist`、`netstat` 等 |

`ToolSetupService` 可以检测 PsExec 是否存在，并可下载 Sysinternals PsTools 到配置目录。

## 14. 发布与版本管理

### 14.1 当前版本

当前发布版本为 **1.4.16**。本版本修复拖拽脚本立即执行时沿用能力缓存中的 WMI/DCOM 路由，导致脚本虽已启动但无实时输出、进度条长期等待的问题：立即执行脚本在 PsExec 可用时显式首选 PsExec，仍仅在其发生传输失败时回退 WMI/DCOM。WMI 引导子进程同时关闭标准输入，并在子进程退出后对标准输出和标准错误读取设置 5 秒上限，避免批处理等待输入或输出管道句柄未释放造成挂起。远程执行继续使用 1.4.14 引入的按主机和凭据建立、默认 5 分钟 TTL 的能力快照与传输路由学习；普通命令默认 `PsExec → WMI/DCOM`，查询类使用 `WMI/DCOM → PsExec`，超长有凭据命令使用 `WMI/DCOM → PsExec`，交互 GUI 使用 `PsExec → WMI/DCOM → ScheduledTask`。有凭据 PsExec 始终由所选凭据 RunAs 启动并显式传入 `-u/-p`；只有传输失败才允许切换通道，远端非零退出绝不重放。

项目版本号必须使用语义化版本格式：

```text
MAJOR.MINOR.PATCH
```

对应规则如下：

| 版本段 | 适用场景 | 示例 |
| --- | --- | --- |
| `MAJOR` | 重大架构升级、重大功能变更或不兼容变更 | `1.0.0` → `2.0.0` |
| `MINOR` | 新增向后兼容的功能或较完整的功能模块 | `1.0.3` → `1.1.0` |
| `PATCH` | Bug 修复、稳定性修复、兼容性修复及小范围优化 | `1.0.3` → `1.0.4` |

正式发布优先使用三段式版本号（例如 `1.0.3`）。两段式版本号（例如 `1.0`）仅用于产品宣传、里程碑或兼容旧文档；构建元数据和发布文件仍应使用三段式版本号。

### 14.2 版本号维护位置

版本升级时必须同步检查以下位置：

1. `src/RemoteOpsTool/RemoteOpsTool.csproj` 中的 `<Version>`、`<AssemblyVersion>`、`<FileVersion>` 和 `<InformationalVersion>`。
2. `src/RemoteOpsTool/Views/MainWindow.xaml` 中显示给用户的版本文本。
3. 发布目录中的最终文件名。
4. 本架构文件的“当前版本”及变更说明。

当前 `.csproj` 使用的版本字段示例：

```xml
<Version>1.4.16</Version>
<AssemblyVersion>1.4.16.0</AssemblyVersion>
<FileVersion>1.4.16.0</FileVersion>
<InformationalVersion>1.4.16</InformationalVersion>
<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
```

Windows 文件属性中的 `FileVersion` 保留四段式是正常要求；产品版本和发布文件名使用三段式语义版本。

### 14.3 发布配置

发布配置在 `.csproj`：

```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
```

发布要求：

- 使用 `Release` 配置。
- 目标运行时为 `win-x64`。
- 使用 `SelfContained`，目标电脑无需预先安装 .NET 运行时。
- 使用 `PublishSingleFile`，最终交付单个独立 EXE。
- 发布前必须完成编译验证；发布后必须检查文件属性中的 `ProductVersion` 和 `FileVersion`。

### 14.4 发布命令与产物命名

建议先发布到临时目录，确认只有单个 EXE 后，再移动到正式发布目录并追加语义版本号：

```powershell
$version = "1.4.16"
$temp = "D:\path\to\RemoteOpsTool\publish\_publish_$($version.Replace('.', '_'))"

dotnet publish src\RemoteOpsTool\RemoteOpsTool.csproj `
   -c Release `
   -r win-x64 `
   --self-contained true `
   --no-restore `
   -p:PublishSingleFile=true `
   -p:EnableCompressionInSingleFile=true `
   -p:IncludeNativeLibrariesForSelfExtract=true `
   -o $temp

Move-Item "$temp\RemoteOpsTool.exe" `
  "D:\path\to\RemoteOpsTool\publish\RemoteOpsTool $version.exe"
Remove-Item $temp -Recurse -Force
```

发布文件命名规范：

```text
RemoteOpsTool <MAJOR>.<MINOR>.<PATCH>.exe
```

当前正式产物：

```text
D:\path\to\RemoteOpsTool\publish\RemoteOpsTool 1.4.16.exe
```

旧版本发布文件可以保留用于回滚，但新版本不得继续使用 `v2`、`v3`、`v4` 等无法表达变更级别的命名方式。
## 15. 设计取舍

| 取舍 | 当前选择 | 原因 |
| --- | --- | --- |
| 查询/结构化管理 | WMI/DCOM 优先 | 适配磁盘、设备、服务、会话等结构化操作 |
| 远程命令执行 | `RemoteExecutionService` 统一策略：`PsExec → WMI/DCOM`；查询和超长凭据负载使用 WMI 优先；只有传输失败才 fallback | 用主机级能力缓存和路由学习减少完整探测，PsExec 统一凭据执行，WMI 保留高效查询与安全回退 |
| 远程 GUI | `RemoteExecutionService` 统一策略：`PsExec direct → WMI/DCOM → ScheduledTask`；交互 PsExec 显式 `-u/-p`、默认 `PSEXESVC`、`-h -n -w -i -d`，不使用 `-r/-s` | 直接启动目标用户桌面 GUI，避免 PowerShell 双层包装和空黑窗 |
| 本机操作 | 本地进程/API/UAC，不使用 PsExec | 避免本机自连接和 PSEXESVC 握手错误 |
| 凭据使用 | PsExec 进程 RunAs 所选凭据 | 本机登录账号可能无管理员权限 |
| 注册表 | WMI StdRegProv + reg.exe | 不依赖 Remote Registry 服务 |
| 进程管理 | WMI/tasklist/PsExec 多路径 | 目标环境查询能力不稳定，需要多重兜底 |
| UI 架构 | 单主窗口 + 多功能子窗口 | 保持工具密度和运维效率 |
| 启动策略 | 首屏优先，工具检查后台化 | 降低启动等待感 |
| 发布方式 | 自包含单文件 | 方便复制到公司电脑直接运行 |

## 16. 后续维护建议

- 新增远程能力时优先放到 Service，不要直接写在 ViewModel 或 code-behind。
- 新的远程查询优先考虑 WMI/DCOM 或 SMB，只有必要时再走 PsExec。
- 所有远程命令、查询、写入和交互启动都必须先通过 `RemoteExecutionService` 建立 session；业务模块不得直接选择 PsExec 或 WMI 通道，也不得自行重新探测能力。
- 只有 `PsExecService` 作为 `IRemoteCommandExecutor` raw executor 时才允许执行单通道 PsExec；raw executor 不负责探测、重试或 fallback。
- 所有密码和命令日志都必须经过脱敏。
- 新增表格窗口时复用全局 DataGrid 样式和 `DataGridRowClickHelper`。
- 复杂远程输出优先使用结构化格式，例如 CSV 或 JSON，少用纯字符串位置解析。
- 高风险操作应至少有确认框和日志输出。
