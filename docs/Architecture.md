# RemoteOpsTool Architecture

RemoteOpsTool 是一个面向受限域环境的 WPF 运维工具。它假设程序以普通域用户启动，但可以在工具内选择一组高权限运维凭据；目标主机通常不启用 WinRM、不启用远程注册表服务，也不部署长期 Agent 或中心服务器。

当前架构的核心原则是：

- 先识别目标是否为本机；本机操作绝不通过 PsExec 自连接。
- 本机后台命令使用本地进程/API；本机交互 GUI 使用当前桌面的 UAC 提权流程。
- `CapabilityService` 按 `host + username + password SHA-256 指纹` 缓存 `CapabilitySnapshot`；不存在统一 TTL，而是按探针和操作结果分别设置 TTL/冷却，同一键的并发探测会合并，手动刷新和失效可绕过缓存。
- 每个顶层操作通过 `IRemoteExecutionService.CreateSessionAsync()` 获取一次快照，并在该 session 内固定复用；查询类和普通命令默认使用 `WMI/DCOM → PsExec`；显式流式执行的脚本使用 `PsExec → WMI/DCOM`；有凭据且命令超过 700 字符时固定为 `WMI/DCOM → PsExec`，`InteractiveLaunch` 使用 `PsExec → WMI/DCOM → ScheduledTask`。
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
| 发布 | SingleFile + SelfContained | `win-x64` 单文件发布 |

NuGet 依赖见 [RemoteOpsTool.csproj](../src/RemoteOpsTool/RemoteOpsTool.csproj)。

## 2. 项目结构

```text
RemoteOpsTool/
├── README.md
├── RemoteOpsTool.slnx
├── .gitattributes
├── .gitignore
├── docs/
│   ├── Architecture.md
│   ├── DESIGN.md
│   ├── 功能测试文档.md
│   ├── 性能基准测试.md
│   └── 远程执行命令路由优化评估报告.md
├── src/RemoteOpsTool/
│   ├── App.xaml / App.xaml.cs
│   ├── Assets/
│   │   └── Fonts/
│   ├── RemoteOpsTool.csproj
│   ├── Constants/
│   │   └── AppConstants.cs
│   ├── Converters/
│   │   ├── BoolToVisibilityConverter.cs
│   │   ├── ConnectionStatusToColorConverter.cs
│   │   └── LogLevelToColorConverter.cs
│   ├── Helpers/
│   │   ├── ComputerManagementHelper.cs
│   │   ├── CredentialMasker.cs
│   │   ├── DataGridRowClickHelper.cs
│   │   ├── HostHelper.cs
│   │   ├── NativeProcessHelper.cs
│   │   ├── NetworkPathHelper.cs
│   │   ├── NetworkShareCredentialHelper.cs
│   │   ├── ProcessHelper.cs
│   │   ├── RegistryHelper.cs
│   │   ├── RemoteErrorClassifier.cs
│   │   ├── RemoteExecutionOutputNormalizer.cs
│   │   ├── RemoteRegistryBatchReader.cs
│   │   ├── RemoteWmiConnectionPool.cs
│   │   ├── RemoteWmiHelper.cs
│   │   ├── ServiceCommandHelper.cs
│   │   ├── SessionHelper.cs
│   │   ├── SvgImageHelper.cs
│   │   └── WindowHelper.cs
│   ├── Models/
│   │   ├── AppSettings.cs
│   │   ├── CommandResult.cs
│   │   ├── CommandShell.cs
│   │   ├── CredentialInfo.cs
│   │   ├── DeviceInfo.cs
│   │   ├── DiskCleanupResult.cs
│   │   ├── DiskInfo.cs
│   │   ├── EnvVariableInfo.cs
│   │   ├── LogEntry.cs
│   │   ├── NetworkConnectionInfo.cs
│   │   ├── PrinterInfo.cs
│   │   ├── ProcessDetailInfo.cs
│   │   ├── ProcessInfo.cs
│   │   ├── RegistryTreeNode.cs
│   │   ├── RemoteCapabilityInfo.cs
│   │   ├── ServiceConfigSnapshot.cs
│   │   ├── ServiceInfo.cs
│   │   ├── ServicePropertiesCacheData.cs
│   │   ├── SoftwareInfo.cs
│   │   ├── SystemInfoData.cs
│   │   └── UserSessionInfo.cs
│   ├── Services/
│   │   ├── Capability/
│   │   │   ├── CapabilityCachePolicy.cs
│   │   │   ├── CapabilityMatrix.cs
│   │   │   ├── CapabilityProbeCatalog.cs
│   │   │   ├── CapabilityProbeProfile.cs
│   │   │   ├── CapabilityService.cs
│   │   │   ├── CapabilitySnapshot.cs
│   │   │   ├── OperationCapability.cs
│   │   │   ├── RouteLearningStore.cs
│   │   │   └── TransportAvailability.cs
│   │   ├── Interfaces/
│   │   │   ├── ICacheService.cs
│   │   │   ├── ICapabilityService.cs
│   │   │   ├── ICredentialService.cs
│   │   │   ├── IDameWareService.cs
│   │   │   ├── IDeviceService.cs
│   │   │   ├── IEnvVarService.cs
│   │   │   ├── IFileDiskService.cs
│   │   │   ├── ILogService.cs
│   │   │   ├── INetworkService.cs
│   │   │   ├── IPrinterService.cs
│   │   │   ├── IPsExecService.cs
│   │   │   ├── IServiceManagerService.cs
│   │   │   ├── ISettingsService.cs
│   │   │   ├── ISoftwareService.cs
│   │   │   ├── ISystemInfoService.cs
│   │   │   ├── ITaskSchedulerService.cs
│   │   │   ├── IToolSetupService.cs
│   │   │   └── ITransportProbeService.cs
│   │   ├── Transports/
│   │   │   ├── ICommandTransport.cs
│   │   │   ├── IRemoteCommandExecutor.cs
│   │   │   ├── RemoteCommand.cs
│   │   │   ├── RemoteCommandShape.cs
│   │   │   ├── RemoteExecutionService.cs
│   │   │   ├── RemoteOperationKind.cs
│   │   │   ├── RemoteTransportKind.cs
│   │   │   ├── TransportFailureClassifier.cs
│   │   │   ├── TransportProbeService.cs
│   │   │   └── TransportResult.cs
│   │   ├── CacheKeys.cs
│   │   ├── CacheService.cs
│   │   ├── CredentialService.cs
│   │   ├── DameWareService.cs
│   │   ├── DeviceService.cs
│   │   ├── EnvVarService.cs
│   │   ├── FileDiskService.cs
│   │   ├── LogService.cs
│   │   ├── NetworkService.cs
│   │   ├── PrinterService.cs
│   │   ├── PsExecService.cs
│   │   ├── ServiceManagerService.cs
│   │   ├── SettingsService.cs
│   │   ├── SoftwareService.cs
│   │   ├── SystemInfoService.cs
│   │   ├── TaskSchedulerService.cs
│   │   └── ToolSetupService.cs
│   ├── ViewModels/
│   │   ├── ConnectionViewModel.cs
│   │   ├── CredentialViewModel.cs
│   │   ├── FileDiskViewModel.cs
│   │   ├── InteractiveViewModel.cs
│   │   ├── LogViewModel.cs
│   │   ├── MainViewModel.cs
│   │   ├── NetworkViewModel.cs
│   │   ├── RemoteManagementViewModel.cs
│   │   ├── SettingsViewModel.cs
│   │   ├── StatusBarViewModel.cs
│   │   ├── TerminalViewModel.cs
│   │   └── Dialogs/
│   │       ├── DeviceManagerViewModel.cs
│   │       ├── DiskCleanupViewModel.cs
│   │       ├── DiskInfoViewModel.cs
│   │       ├── EnvVarViewModel.cs
│   │       ├── NetworkPortsViewModel.cs
│   │       ├── PrinterManagerViewModel.cs
│   │       ├── ProcessListViewModel.cs
│   │       ├── RemoteRegistryViewModel.cs
│   │       ├── ServiceManagerViewModel.cs
│   │       ├── ServicePropertiesViewModel.cs
│   │       ├── SoftwareManagerViewModel.cs
│   │       └── SystemInfoViewModel.cs
│   └── Views/
│       ├── MainWindow.xaml / MainWindow.xaml.cs
│       ├── Dialogs/
│       │   ├── CredentialDialog.*
│       │   ├── DeviceManagerWindow.*
│       │   ├── DiskCleanupWindow.*
│       │   ├── DiskInfoWindow.*
│       │   ├── EnvVarWindow.*
│       │   ├── NetworkPortsWindow.*
│       │   ├── PrinterManagerWindow.*
│       │   ├── RemoteRegistryWindow.*
│       │   ├── ServiceManagerWindow.*
│       │   ├── ServicePropertiesDialog.*
│       │   ├── ServicePropertiesWindow.*
│       │   ├── SettingsDialog.*
│       │   ├── SoftwareManagerWindow.*
│       │   └── SystemInfoWindow.*
│       └── Resources/
│           ├── Styles.xaml
│           ├── Tools.ico
│           └── Tools.svg
├── tests/RemoteOpsTool.Tests/
│   ├── CapabilityMatrixTests.cs
│   ├── CapabilityServiceTests.cs
│   ├── CacheServiceTests.cs
│   ├── CleanupPathNormalizationTests.cs
│   ├── ExecutionRoutingOptimizationTests.cs
│   ├── PsExecCommandPlanningTests.cs
│   ├── RemoteExecutionOutputNormalizerTests.cs
│   ├── RemoteExecutionServiceTests.cs
│   ├── RemoteRegistryBatchReaderTests.cs
│   ├── RemoteWmiConnectionPoolTests.cs
│   ├── ServiceCommandHelperTests.cs
│   ├── SystemInfoServiceTests.cs
│   ├── TransportFailureClassifierTests.cs
│   └── ...其他服务与辅助类测试
├── tests/scripts/
└── tools/
    └── Report-RouteLearning.ps1
```

项目以“单一 WPF 主窗口 + 按需创建子窗口”组织。远程功能按“ViewModel 协调、Service 业务实现、Transports 统一路由、Capability 主机能力状态、Helpers 通用基础设施”分层。测试项目覆盖能力矩阵、路由、缓存、WMI 连接池、注册表批量读取、PsExec 参数规划、输出归一化和清理脚本等关键行为。
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

入口在 [App.xaml.cs](../src/RemoteOpsTool/App.xaml.cs)。

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

所有核心服务由 `App.xaml.cs` 在启动时注册为 Singleton：

| 接口/类型 | 实现 | 职责 |
| --- | --- | --- |
| `ISettingsService` | `SettingsService` | JSON 配置读写 |
| `ICredentialService` | `CredentialService` | 凭据保存、选择、DPAPI 加解密 |
| `ILogService` | `LogService` | 内存日志、筛选、文件日志 |
| `ICacheService` | `CacheService` | 主机维度查询快照缓存、TTL 和前缀失效 |
| `ITransportProbeService` | `TransportProbeService` | 探测 ICMP、TCP 445/135/5985、`ADMIN$`、WMI、会话、PsExec、计划任务 RPC |
| `ICapabilityService` | `CapabilityService` | 按主机/凭据维护探针结果、操作级健康状态和 session 快照 |
| `IRouteLearningStore` | `RouteLearningStore` | 持久化主机、凭据指纹、operation、命令形态和 transport 的路由统计 |
| `IRemoteCommandExecutor` | `PsExecService` | 严格单通道 raw executor；不探测、不重试、不 fallback |
| `IRemoteExecutionService` | `RemoteExecutionService` | 创建 session、按 profile 懒升级能力、排序回退链、安全回退和记录结果 |
| `IPsExecService` | `PsExecService` | PsExec 参数构造、RunAs、流式输出、WMI/交互/计划任务 raw 执行及旧调用兼容入口 |
| `ITaskSchedulerService` | `TaskSchedulerService` | 原生 `schtasks`/Task Scheduler RPC 交互兜底 |
| `IToolSetupService` | `ToolSetupService` | PsTools 检测、下载、签名检查 |
| `IDameWareService` | `DameWareService` | DameWare 远控启动 |
| `IFileDiskService` | `FileDiskService` | 管理共享、磁盘信息、空间清理 |
| `IDeviceService` | `DeviceService` | 设备列表、启用、禁用、卸载、驱动版本索引 |
| `IServiceManagerService` | `ServiceManagerService` | 服务列表、配置、启停、重启和状态轮询 |
| `IPrinterService` | `PrinterService` | 打印机列表和操作 |
| `ISoftwareService` | `SoftwareService` | 软件清单、卸载、注册表清理 |
| `IEnvVarService` | `EnvVarService` | 系统/用户环境变量 |
| `ISystemInfoService` | `SystemInfoService` | 系统硬件、网络、补丁信息的并行 WMI 查询 |
| `INetworkService` | `NetworkService` | Ping、端口、进程、会话、活动连接 |

`MainViewModel` 和 `MainWindow` 也是 Singleton。各功能窗口的 ViewModel 通常在打开窗口时手动创建，并注入所需 Service。应用退出时统一清理日志、清空 WMI 连接池并 flush 路由学习文件。
## 6. 远程执行策略

远程执行不再依赖每次完整探测或固定回退顺序，而是采用以下流水线：

```text
业务 Service
  └─ IRemoteExecutionService.CreateSessionAsync(profile)
      ├─ CapabilityService：按 host + 用户名 + 密码 SHA-256 指纹取得快照
      │   └─ CapabilityProbeCatalog：只执行该操作真正需要的探针
      ├─ RouteLearningStore：按 operation + 命令形态 + transport 学习首选顺序
      └─ RemoteExecutionSession
          ├─ CapabilityMatrix：构建完整安全回退链
          ├─ 失败冷却排序：刚失败的通道移到链尾但仍保留回退资格
          ├─ IRemoteCommandExecutor：严格单通道执行
          └─ 只有 TransportFailure 才继续下一通道
```

### 6.1 最小能力探测 Profile

`CapabilityProbeProfile` 避免所有操作都执行完整网络、PsExec 和计划任务探测：

| Profile | 预探测内容 | 典型用途 |
| --- | --- | --- |
| `Full` | Ping、SMB 445、RPC 135、WinRM 5985、`ADMIN$`、WMI/DCOM、会话查询、PsExec、计划任务 RPC | 连接测试、用户手动刷新、完整诊断 |
| `InventoryWmiOnly` | WMI/DCOM | 设备、服务、打印机、系统信息等结构化查询 |
| `RegistryWmiOnly` | WMI/DCOM | 注册表读取和写入 |
| `Command` | `ADMIN$`、WMI/DCOM、PsExec | 已知需要命令回退或显式脚本执行 |
| `CommandWmiFirst` | WMI/DCOM | 普通命令的冷启动；PsExec 在必要时懒升级探测 |
| `SessionQuery` | WMI/DCOM、会话查询 | 登录会话查询 |
| `InteractiveLaunch` | 不预探测 | 交互 GUI 启动；运行时按 PsExec → WMI/DCOM → ScheduledTask 自行回退 |

`CapabilityProbeCatalog` 会合并同一快照中的缺失探针，并选择能够覆盖缺失项的最小 Profile。普通命令默认使用 `CommandWmiFirst`；显式 `PreferPsExec` 的流式脚本会先确保 `Command` 能力存在；如果 WMI 查询失败导致路由链为空，`RemoteExecutionSession` 只升级一次能力快照，而不是在业务层反复探测。

### 6.2 主机级能力缓存键

`CapabilityService` 的缓存键由三部分组成：

```text
规范化主机名 + 规范化用户名 + SHA-256(密码)
```

特点：

- 密码只参与内存指纹计算，不写入快照、日志或路由学习文件。
- 不同凭据即使访问同一主机也不会共享能力状态。
- 同一键的并发 `ProbeAsync()` 会合并，避免多个窗口同时 WMI/PsExec 探测。
- `RefreshAsync()` 强制重探，`Invalidate()` 立即移除；连接测试和手动刷新可绕过旧快照。
- 一个顶层操作只通过 `IRemoteExecutionService.CreateSessionAsync()` 取得一次 `CapabilitySnapshot`，分片上传、删除复核、多步骤清理都复用该 session。
- `ExecuteOnceAsync()` 是便捷入口；仍需强一致步骤序列的调用方必须显式创建 session。

### 6.3 探针级 TTL 与失败冷却

不存在“整个 CapabilitySnapshot 统一 5 分钟”的规则。探针缓存和 operation/transport 健康状态分别维护：

| 探针/结果 | 成功 TTL | 失败 TTL |
| --- | ---: | ---: |
| Ping、TCP 445/135/5985 | 30 秒 | 15 秒 |
| 登录会话查询 | 12 秒 | 8 秒 |
| `ADMIN$` 管理共享 | 30 秒 | 30 秒（瞬时失败保底） |
| PsExec 临时执行 | 60 秒 | 按失败分类：权限/认证 10 分钟；禁用/未找到/远端命令 30 分钟；其他 30 秒 |
| WMI/DCOM | 5 分钟 | 按失败分类：权限/认证 10 分钟；禁用/未找到/远端命令 30 分钟；其他 30 秒 |
| 计划任务 RPC | 5 分钟 | 按失败分类：权限/认证 10 分钟；禁用/未找到/远端命令 30 分钟；其他 30 秒 |
| 缺失探针记录 | 30 秒 | 30 秒 |

`CapabilityFailureKind` 区分 `Timeout`、`Network`、`Authentication`、`PermissionDenied`、`Disabled`、`NotFound`、`Protocol`、`RemoteCommand` 和 `Unknown`。健康状态按 `(Operation, Transport)` 隔离，例如 `Command + PsExec` 失败不会错误抑制 `InteractiveLaunch + PsExec`。

失败通道进入冷却时只是被移到回退链尾，不会被永久移除。若整个链都在冷却中，`RemoteExecutionService` 仍按安全矩阵顺序重试一次，避免一次瞬时故障导致功能直接不可用。

### 6.4 路由矩阵和决策顺序

`RemoteOperationKind` 当前包括 `Command`、`InteractiveLaunch`、`Inventory`、`RegistryRead`、`RegistryWrite`。`CapabilityMatrix` 的底线顺序如下：

| Operation | 默认安全顺序 | 说明 |
| --- | --- | --- |
| `Command` | `WMI/DCOM → PsExec` | 普通短命令避免每次启动 `PSEXESVC` |
| `InteractiveLaunch` | `PsExec → WMI/DCOM → ScheduledTask` | 需要真实桌面会话的 GUI/管理入口 |
| `Inventory` | `WMI/DCOM → PsExec` | 设备、服务、打印机、系统信息等结构化查询 |
| `RegistryRead` | `WMI/DCOM → PsExec` | 先用 `StdRegProv`，WMI 不可用时才走命令注册表路径 |
| `RegistryWrite` | `WMI/DCOM → PsExec` | 写入结果需要严格幂等控制 |

普通命令的运行期排序规则：

1. 默认使用 `CommandWmiFirst`，只预热 WMI，不为了普通命令额外启动 PsExec 探测。
2. `RemoteCommand.PreferPsExec = true` 的显式流式脚本优先 PsExec；如果 PsExec 尚未探测，会懒升级到 `Command` Profile。
3. 命令行长度超过 `700` 字符且存在凭据时，强制 WMI 优先，避免 `CreateProcessWithLogonW`/RunAs 启动器的命令长度限制；PsExec 仍保留为传输失败后的回退。
4. 路由学习出的首选 transport 只重排现有安全链，不裁剪、不越过 `CapabilityMatrix`。
5. 历史学习出的 PsExec 首选项不得覆盖普通命令的 WMI 默认策略；只有显式 `PreferPsExec` 的脚本允许 PsExec 优先。
6. 已进入失败冷却的通道排在链尾，但保持回退资格。
7. 每个 transport 在一次 `ExecuteAsync()` 中最多尝试一次。

命令形态由 `RemoteCommandShape` 分类，用于路由学习隔离：

| 形态 | 判定 |
| --- | --- |
| `Script` | `PreferPsExec=true` 的脚本/流式执行 |
| `InteractiveLaunch` | 交互会话或 `InteractiveLaunch` operation |
| `LongCommand` | 非脚本命令长度 `>= 1024` 字符 |
| `ShortCommand` | 其他普通短命令 |

分类只保存枚举和统计，不保存命令文本、脚本正文或密码。

### 6.5 回退安全规则

1. 只有 `TransportResult.IsTransportFailure == true` 才能进入下一 transport。
2. 远端正进程已经启动并返回非零退出码时，属于 `CommandFailure`；立即返回，绝不换通道重放，避免清理、卸载、注册表写入等副作用执行两次。
3. 所有可用通道都传输失败时，返回组合错误并保留每个通道的原始错误摘要。
4. 某个 operation 成功后，session 优先复用该通道，并写入主机级学习状态。
5. 传输失败时更新对应 `(Operation, Transport)` 的 `Health`/`FailureKind` 和冷却时间。
6. 交互启动固定使用 `PsExec → WMI/DCOM → ScheduledTask`；即使普通命令的 PsExec 探测失败，也不会误删交互 PsExec 的回退资格。
7. PsExec 支持实时逐行回调和最终结果；WMI/DCOM 只能在命令完成后批量回放 stdout/stderr。

### 6.6 路由学习与评分

`RouteLearningStore` 将路由效果持久化到：

```text
%AppData%\RemoteAdmin\route-learning.json
```

统计维度：

```text
主机 + 凭据 SHA-256 指纹 + RemoteOperationKind + RemoteCommandShape + RemoteTransportKind
```

记录字段包括：

- 成功/失败次数。
- 远端命令已启动但返回非零的次数。
- 回退次数。
- 平均耗时、EWMA、P50、P95。
- 平均首输出时间。
- 输出字节数。
- 最近耗时样本。
- 最后成功/失败时间与失败类型。

实现约束：

- 最多保留 4096 条记录，按最后尝试时间淘汰最旧记录。
- 写盘采用 1 秒 debounce；失败后 5 秒重试。
- 超过 30 天没有尝试的记录不参与首选评分。
- 只保存凭据指纹，不保存密码、命令文本、脚本正文或目标输出。
- 评分综合成功率、EWMA/P95 延迟、最近失败、硬失败、远端命令非零率和成功样本量。
- 学习结果只影响首选排序，不能绕过 operation 的安全矩阵和凭据规则。

可通过以下脚本生成控制台报表或 CSV：

```powershell
tools\Report-RouteLearning.ps1
```

冷启动/热会话、回退率和 P50/P95 的统一测试矩阵见本目录 `性能基准测试.md`。

### 6.7 PsExecService

[PsExecService](../src/RemoteOpsTool/Services/PsExecService.cs) 同时实现 `IRemoteCommandExecutor` 和 `IPsExecService`：

- `IRemoteCommandExecutor` 的 `ExecutePsExecOnlyAsync`、`ExecuteWmiOnlyAsync`、`ExecuteInteractivePsExecOnlyAsync`、`ExecuteInteractiveWmiOnlyAsync`、`ExecuteInteractiveScheduledTaskOnlyAsync` 是 raw-only 方法，供 `RemoteExecutionService` 使用。
- `IPsExecService` 保留本机执行和旧调用兼容入口；新的远程功能不得绕过 `RemoteExecutionSession` 进行通道选择。

凭据规则：

- 只要提供运维凭据，所有后台、流式和交互 PsExec 都必须同时满足：
  - 用所选凭据 RunAs 启动本地 PsExec 进程。
  - 命令行显式传入 `-u <user> -p <password>`。
- 两个条件缺一不可；不存在“RunAs 后省略凭据”或“显式凭据但不 RunAs”的路径。
- 日志中的密码由 `CredentialMasker` 隐藏。

参数和安全行为：

- 优先使用 `PsExec64.exe`，否则使用 `PsExec.exe`。
- 后台/流式命令可使用隔离服务名 `RemoteOpsTool_<host>_<pid>_<counter>`；当自定义 `-r` 被 SCM/EDR 拒绝时，只恢复为默认 `PSEXESVC`，绝不删除 `-u/-p`。
- 交互 GUI 固定使用默认 `PSEXESVC`，参数包含 `-h -n <timeout> -w <working-directory> -i <session> -d`；不使用 `-r`，不使用 `-s`。
- `compmgmt.msc`、`printmanagement.msc`、`appwiz.cpl` 等入口先规范化为 `mmc.exe ...` 或 `control.exe ...` 再启动。
- 管理入口按钮（计算机管理、打印机属性、添加打印机向导）不经过 PsExec 在目标机拉起窗口，而是在管理端本机用所选凭据以“仅网络凭据登录”（`CreateProcessWithLogonW` + `LOGON_NETCREDENTIALS_ONLY`，等价于 `runas /netonly`）启动原生 GUI，远端认证走所选凭据、窗口留在本机桌面。
- “打印管理”入口同样在管理端本机启动 `mmc.exe`，但会读取本机 `printmanagement.msc` 中打印管理 snap-in（CLSID `{D06342BD-9057-4673-B43A-0E9BBBE99F11}`）的 `BinaryStorage` 配置流，复制并注入目标服务器后输出到 `%LOCALAPPDATA%\RemoteOpsTool\Consoles\printmanagement-<host>-<host-hash>-<guid>.msc`；控制台不依赖不存在的 `/server:` 开关。每次点击都生成独立控制台文件和新的 `ConsoleFileID`，避免 MMC 复用已打开窗口中手工删除过服务器的旧文档；未被 MMC 占用的旧副本会尽力清理，仍被占用的副本不会阻塞新控制台启动。若源控制台或配置流不可用，则回退原始控制台并提示手动“添加/删除服务器”。
- 打印机属性使用 `rundll32 printui.dll,PrintUIEntry /p /n <目标机本地打印机名> /c\\<host>`；添加打印机向导也统一使用 `rundll32 printui.dll,PrintUIEntry /il /c\\<host>`，由 `/c` 把安装请求送进目标主机的打印后台处理程序。不能直接使用 `printui.exe /il`：其可执行文件带提权清单，`CreateProcessWithLogonW + LOGON_NETCREDENTIALS_ONLY` 会在进程创建阶段返回 Win32 740（`The requested operation requires elevation.`）；`rundll32.exe` 可加载同一 `PrintUIEntry` 实现且不会在启动阶段触发该提权限制，仍不用 `PsExec -i <session>` 在目标机桌面拉起 GUI。
- 添加向导在管理端本机运行，`/il` 的部分分支（例如“添加本地打印机”）会忽略 `/c` 把队列装到管理端；因此启动前后各取一次目标主机与管理端本机的打印机快照，后台按退避间隔（10s → 60s，最长 5 分钟）比对：目标主机新增即视为成功并刷新缓存，只有本机新增则明确告警并提示删除误装队列。
- `cmd.exe`、`powershell.exe`、`pwsh.exe` 按直接程序处理；普通 GUI 程序强制 `Direct + WrapCmd=false`。
- 只有明确的 PowerShell 脚本语句才选择 PowerShell host，避免 UI shell 选择污染 GUI 启动形态。
- `MaxSafeRunAsCommandLength = 700`；超过该长度且存在凭据的后台命令由路由层强制 WMI 优先。

典型交互形态：

```text
PsExec \\HOST -u DOMAIN\admin -p ******** -accepteula -nobanner -h -n 10 -w C:\Windows\System32 -i 4 -d cmd.exe
```

### 6.8 WMI/DCOM

WMI/DCOM 是结构化查询、常规短命令、注册表读写和 PsExec 安全回退的首选通道：

- 所有连接由 `RemoteWmiHelper.CreateScope()` 统一创建，使用传入的运维凭据连接 `\\host\root\cimv2` 或对应 Provider。
- `RemoteWmiConnectionPool` 按 `host + credential + namespace` 复用 `ManagementScope`；每个键最多 4 个并发 scope。
- scope 不跨并发调用共享；空闲 scope 可复用。
- 连接失败冷却 3 秒，查询失败冷却 1 秒，避免在远程服务故障时产生请求风暴。
- `RemoteRegistryBatchReader` 通过最多 4 条 lane 批量读取 `StdRegProv` 值，每条 lane 复用一个 scope 和一个 ManagementClass。
- `SystemInfoService` 将独立 WMI 查询分成最多 4 批并行执行，只选择需要的属性，减少每台主机的查询轮次。
- WMI 命令通道使用一次性注册表任务启动和收集结果，Job 根路径为：

```text
SOFTWARE\RemoteOpsTool\WmiJobs
```

- WMI 命令行上限为 30000 字符，等待上限为 15 分钟。
- WMI 启动的远端进程已经返回非零退出码时属于命令失败，不得因此切到 PsExec 重放。
- WMI/DCOM 不可用或传输失败时才会进入安全回退，不允许功能 Service 私自重试。

### 6.9 SMB 连接

`NetworkShareCredentialHelper` 用于建立和清理由本工具创建的 SMB 连接，例如：

```text
\\HOST\ADMIN$
\\HOST\C$
\\HOST\D$
\\HOST\Users\Public\Desktop
```

断开连接时，`MainViewModel.Disconnect()` 调用 `NetworkShareCredentialHelper.DisconnectAll()` 清理本工具建立的连接。SMB 失败属于分层传输结果，可以回退到命令上传，但上传的每一个分片必须复用同一个顶层 session 和快照。本机操作绝不通过 PsExec 自连接。

### 6.10 输出归一化

`RemoteExecutionOutputNormalizer` 同时用于流式回调和最终 `CommandResult`：

- 过滤 PsExec 的 `Connecting to...`、`Starting PSEXESVC...`、`Copying authentication key...` 等协议噪音。
- 解码 PowerShell `#< CLIXML` 输出，保留可读消息。
- 规范化 CRLF、空行和进度流。
- 回退时按匹配次数去重，重复日志不会被重复展示。
- 输出的 `StdOut`、`StdErr` 归一化规则一致，日志区和最终结果不会出现两套文本。
## 7. 功能模块

| UI 功能 | ViewModel | Service | 当前主要策略 |
| --- | --- | --- | --- |
| 凭据管理 | `CredentialViewModel` | `CredentialService` | 本地 DPAPI/JSON；缓存键按主机与凭据隔离 |
| 连接和 Ping | `ConnectionViewModel` | `NetworkService` | ICMP 实时探测；连通性成功后按 `Full` 能力画像初始化或刷新主机能力快照 |
| DameWare 远控 | `ConnectionViewModel` | `DameWareService` | 本机启动外部远控程序，不通过 PsExec 自连接 |
| 文件/磁盘入口 | `FileDiskViewModel` | `FileDiskService` | 本机文件操作为本地 API；远程优先 SMB；命令型操作统一经 `RemoteExecutionService` |
| 清理空间 | `DiskCleanupViewModel` | `FileDiskService` | 本机直接执行或 UAC；远程长凭据脚本 WMI/DCOM → PsExec；显式流式脚本 PsExec → WMI/DCOM；普通短命令 WMI/DCOM → PsExec；删除后复核 |
| 磁盘信息 | `DiskInfoViewModel` | `FileDiskService` | 直接 WMI `Win32_LogicalDisk`，8 秒超时，60 秒短缓存；失败后才按统一能力画像回退 |
| 设备管理 | `DeviceManagerViewModel` | `DeviceService` | `InventoryWmiOnly`：并行 WMI 查询 + `DriverVersionIndex`；WMI 传输失败时才回退 PsExec |
| 服务管理 | `ServiceManagerViewModel` | `ServiceManagerService` | WMI scope/session 复用；启停、重启和状态轮询优先 WMI，命令型兜底统一走 session |
| 服务属性 | `ServicePropertiesViewModel` | `ServiceManagerService` / `RemoteExecutionService` | WMI 优先；配置项使用 2 分钟短缓存和加载进度反馈；修改后按主机 + 凭据失效并刷新状态 |
| 打印机管理 | `PrinterManagerViewModel` | `PrinterService` | WMI 结构化查询和操作优先，PsExec 仅作为传输失败回退；原生属性页/添加向导在管理端本机以仅网络凭据（netonly）打开并连接目标打印后台处理程序，向导结束后按前后打印机快照判定安装落在目标机还是误装到管理端 |
| 软件管理 | `SoftwareManagerViewModel` | `SoftwareService` | `RemoteRegistryBatchReader` 批量注册表读取；普通/深度模式隔离缓存；WMI 失败后 PsExec 兜底 |
| 环境变量 | `EnvVarViewModel` | `EnvVarService` | `StdRegProv` 批量读取系统/用户范围，2 分钟缓存；写入后按范围失效 |
| 系统信息 | `SystemInfoViewModel` | `SystemInfoService` | 最多 4 组并行 WMI 查询，仅选择所需属性；5 分钟组合快照，传输失败才回退 |
| 注册表 | `RemoteRegistryViewModel` | `RemoteRegistryViewModel` + `RemoteRegistryBatchReader`（WMI 优先，`PsExecService` 受限兜底） | `StdRegProv` 批量读取；支持 loading/refresh 进度；变更后按子树 `InvalidateByPrefix()` 失效 |
| 进程管理 | `ProcessListViewModel` | `NetworkService` | WMI 优先并额外读取 `ExecutablePath`/`CommandLine`/`ParentProcessId`；终止操作复用进程树快照，失败后 tasklist/PsExec 作为受限回退；重启固定为“校验 → 终止单个进程 → 按原启动信息重建”，不杀进程树 |
| 网络连接 | `NetworkPortsViewModel` | `NetworkService` | `netstat -ano` 获取端口/连接；进程名补充优先 WMI，失败后使用独立短超时 `tasklist` |
| 命令终端 | `TerminalViewModel` | `RemoteExecutionService` | 普通远程命令 WMI/DCOM → PsExec；显式上传脚本 `PreferPsExec` 时 PsExec → WMI/DCOM；超过 700 字符且带凭据的负载 WMI 优先；GUI 自动转 `InteractiveLaunch`，按 PsExec → WMI/DCOM → ScheduledTask |

> 远程执行边界：除本机操作和纯 SMB 文件传输外，远程命令、注册表写入、脚本上传分片和交互启动都必须通过 `RemoteExecutionService` 建立 session；`PsExecService` 只作为 raw executor 执行已经选定的单通道，不自行探测、重试或回退。

### 7.1 缓存与会话复用策略

通用缓存只保存短时间可接受的远端快照，不缓存实时状态或操作结果。缓存根目录为：

```text
%LocalAppData%\RemoteOpsTool\Cache
```

缓存键统一由 `CacheKeys` 生成，TTL 由 `CacheService` 维护。键必须包含目标主机；涉及凭据差异的数据还必须包含凭据指纹，避免不同账号或权限范围之间串用结果：

- 服务和服务属性缓存按 `host + credential` 隔离。
- 软件缓存按 `host + credential + normal/deep` 隔离。
- 注册表动态键家族通过 `InvalidateByPrefix()` 整组或按子树失效。
- 未登记键使用 5 分钟保底 TTL，防止遗漏策略导致永久旧数据。
- 主窗口磁盘容量使用 single-flight，避免 60 秒刷新周期重叠；失败后按 15/30/60/120 秒退避，切换主机时取消旧查询。

| 数据 | 缓存策略 | TTL | 写入后失效/刷新 |
| --- | --- | ---: | --- |
| 服务列表与状态 | 短快照 | 15 秒 | 启动、停止、重启、属性窗口关闭、手动刷新 |
| 服务属性配置 | 配置快照 | 2 分钟 | 配置修改、服务切换、手动刷新 |
| 打印机清单、共享、默认状态 | 短快照 | 1 分钟 | 删除、共享/默认切换、原生属性或添加向导、手动刷新 |
| 注册表当前键值 | 极短快照 | 30 秒 | 值/键增删改后按当前路径或子树失效 |
| 环境变量 | 范围快照 | 2 分钟 | 系统/用户变量增删改、手动刷新 |
| 设备列表 | 快照 | 5 分钟 | 启用、禁用、卸载、驱动更新、手动刷新 |
| 系统信息 | 组合快照 | 5 分钟 | 重新打开窗口时允许过期回查；动态网络状态使用实时功能 |
| 软件清单 | 长快照 | 15 分钟 | 静默/交互卸载、深度注册表清理、手动刷新；普通/深度键整组失效 |
| 磁盘容量 | 极短快照 | 60 秒 | 清理后失效；主窗口 single-flight + 失败退避 |
| 未登记缓存键 | 保底策略 | 5 分钟 | 到期重新查询；业务应显式登记新键 |

以下数据始终实时获取，不进入通用 `CacheService`：

| 实时数据 | 原因 |
| --- | --- |
| 进程列表、进程窗口、登录会话 | 秒级变化，需要立即反映目标主机当前状态 |
| 网络端口和活动连接 | 连接生命周期短，进程终止或连接关闭后必须及时更新 |
| Ping、能力探测 | 表示当前网络和通道可用性，必须按探针级 TTL/冷却单独维护 |
| 磁盘清理结果 | 属于操作结果，并与删除前后空间复核直接相关 |
| 命令、脚本上传、共享路径访问 | 属于操作执行，不是可复用查询数据 |

能力探测不存入通用缓存，而由 `CapabilityService` 使用 `host + username + 密码指纹` 维护探针级缓存。同一顶层操作只获取一次能力快照并在 session 内复用；传输成功/失败会更新 operation-specific 状态并写入持久化路由学习记录。
## 8. UI 架构

### 8.1 主窗口

[MainWindow](../src/RemoteOpsTool/Views/MainWindow.xaml) 是单窗口工作台布局：

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

当前 UI 主题集中在 [Styles.xaml](../src/RemoteOpsTool/Views/Resources/Styles.xaml)：

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

[AppSettings](../src/RemoteOpsTool/Models/AppSettings.cs) 当前字段：

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

[LogService](../src/RemoteOpsTool/Services/LogService.cs) 提供：

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
      ├─ 设置 IsLoading / IsExecuting，必要时显示局部进度条
      ├─ 调用 Service
      │   ├─ WMI/DCOM 查询（连接池、批量读取或并行批次）
      │   ├─ ProcessHelper 启动本地进程
      │   ├─ RemoteExecutionService 建立 session，读取能力快照并排序候选通道
      │   └─ PsExecService（raw executor）、WMI/DCOM 或 ScheduledTask 执行
      ├─ 解析、合并并归一化输出
      └─ 更新 ObservableCollection / ObservableProperty
```

并发与线程边界：

- UI 状态通过 `ObservableProperty` 通知；长耗时路径使用 `async/await`，不在 UI 线程同步等待。
- 外部进程由 `ProcessHelper` 统一封装。PsExec 支持逐行流式回调和取消；WMI/DCOM 在远端命令完成后回放归一化的 stdout/stderr。
- 输出回调通过锁定与去重控制并发写入，回退或重复片段不会造成日志区交错或重复展示。
- `RemoteWmiConnectionPool` 按 `host + credential + namespace` 复用 scope，每个键最多 4 个并发 scope；并发调用不共享同一个 scope，空闲 scope 可复用。
- WMI 连接失败冷却 3 秒，查询失败冷却 1 秒，避免服务异常时形成请求风暴。
- `RemoteRegistryBatchReader` 使用最多 4 条 lane，每条 lane 复用一个 scope 和一个 `ManagementClass`，用于软件、环境变量和注册表的批量读取。
- `SystemInfoService` 将独立查询拆成最多 4 批并行 WMI 请求，只返回界面需要的属性。
- 服务启停、重启和配置修改后不依赖固定 1500 ms 假设；通过状态轮询等待目标状态或超时，并按 `host + credential` 失效服务缓存。
- 部分窗口构造后立即后台加载，例如进程管理、系统信息、磁盘信息；注册表窗口的加载、刷新和子树展开均提供进度反馈，关闭窗口时取消旧请求。
- 同一主机的路由能力快照在顶层 session 内复用；功能 Service 不自行完整探测，也不并发更新同一个 operation/transport 健康状态。
- 退出时先刷新日志，再清空 WMI 连接池并异步 flush 路由学习记录，避免后台任务在进程退出过程中丢失状态。
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
- WMI/DCOM 是普通后台命令、查询、注册表读写和结构化操作的首选通道；PsExec 保留为显式脚本流式执行、交互启动和带凭据安全回退。无 WMI/DCOM 或传输失败时，后台命令仍可回退到 PsExec。
- WMI/DCOM 与计划任务 RPC 回退依赖目标主机的 WMI/DCOM、RPC、Task Scheduler 服务、防火墙、权限和应用控制策略；回退失败时必须保留原始通道错误和回退错误，便于定位。
### 12.4 PsExec 提示说明

PsExec 输出中的 `Copying authentication key to HOST...` 是 PsExec 自身的握手/认证材料提示，不表示日志中泄露了明文密码。程序侧仍需注意：

- 不在日志中输出明文密码。
- 不对后台、流式或交互路径省略命令 `-u/-p`：PsExec 必须由所选凭据 RunAs 启动，同时显式传入目标凭据；日志中的密码始终由 `CredentialMasker` 隐藏。
- PsExec 本身会在目标主机创建临时服务，这是其工作机制，不是长期 Agent。

## 13. 外部工具

| 工具 | 是否必需 | 用途 |
| --- | --- | --- |
| PsExec.exe / PsExec64.exe | 可选执行与回退通道 | 显式脚本流式执行和交互程序；始终为有凭据调用启用 RunAs + `-u/-p`；普通后台命令仅在 WMI/DCOM 传输失败时使用 |
| schtasks.exe | 系统内置 | 原生任务计划 RPC 通道，用于 GUI 交互启动的第三兜底 |
| DameWare 远控程序 | 可选 | 远程桌面控制 |
| Windows 内置命令 | 必需 | `cmd`、`powershell`、`sc`、`reg`、`query`、`tasklist`、`netstat` 等 |

`ToolSetupService` 可以检测 PsExec 是否存在，并可下载 Sysinternals PsTools 到配置目录。

## 14. 发布与版本管理

### 14.1 当前版本

当前发布版本为 **1.5.0**。本版本把“进程管理”从网络工具迁移到远程管理分组（网络工具仅保留刷新 DNS 与刷新 IP），并为进程管理新增“重启进程”：先读取目标进程的可执行文件路径与原始命令行，再执行“终止单个进程 → 按原启动方式重建”。重启对关键系统进程（`lsass`、`wininit`、`services`、`csrss`、`smss`、`winlogon`、`svchost` 等）以及由 Windows 服务承载的进程直接拒绝，并引导到服务管理；交互式进程必须回到原会话启动，绝不落到管理端当前会话；非交互进程使用 `Win32_Process.Create` 重建。重启日志只记录进程名与 PID，不记录完整命令行，交互启动通道在静默调用时不再输出命令行。1.4.30 的周期性凭据落盘与重复 `Credentials saved.` 日志修复以及 1.4.29 的凭据与日志安全强化、凭据健康诊断、失效凭据选择提示和保存反馈保持不变。

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

1. `../src/RemoteOpsTool/RemoteOpsTool.csproj` 中的 `<Version>`、`<AssemblyVersion>`、`<FileVersion>` 和 `<InformationalVersion>`。
2. `../src/RemoteOpsTool/Views/MainWindow.xaml` 中显示给用户的版本文本。
3. 发布目录中的最终文件名。
4. 本架构文件的“当前版本”及变更说明。

当前 `.csproj` 使用的版本字段示例：

```xml
<Version>1.5.0</Version>
<AssemblyVersion>1.5.0.0</AssemblyVersion>
<FileVersion>1.5.0.0</FileVersion>
<InformationalVersion>1.5.0</InformationalVersion>
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
$version = "1.5.0"
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
D:\path\to\RemoteOpsTool\publish\RemoteOpsTool 1.5.0.exe
```

旧版本发布文件可以保留用于回滚，但新版本不得继续使用 `v2`、`v3`、`v4` 等无法表达变更级别的命名方式。
## 15. 设计取舍

| 取舍 | 当前选择 | 原因 |
| --- | --- | --- |
| 查询/结构化管理 | WMI/DCOM 优先 | 适配磁盘、设备、服务、会话等结构化操作 |
| 远程命令执行 | `RemoteExecutionService` 统一策略：普通命令与查询使用 `WMI/DCOM → PsExec`；显式流式脚本使用 `PsExec → WMI/DCOM`；超长凭据负载固定 WMI 优先；只有传输失败才 fallback | 用主机级能力缓存和路由学习减少完整探测，普通短命令避免反复启动 PSEXESVC，PsExec 保留流式输出与安全回退 |
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
