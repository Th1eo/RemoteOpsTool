# RemoteOpsTool Architecture

RemoteOpsTool 是一个面向受限域环境的 WPF 运维工具。它假设程序以普通域用户启动，但可以在工具内选择一组高权限运维凭据；目标主机通常不启用 WinRM、不启用远程注册表服务，也不部署长期 Agent 或中心服务器。

当前架构的核心原则是：

- 优先使用 Windows 原生远程管理通道，例如 WMI/DCOM、SMB 管理共享、RPC。
- 当原生通道不可用或能力不足时，使用 PsExec 临时服务执行命令。
- PsExec 进程本身使用所选运维凭据 RunAs 启动，以适配本机登录账号无域管理员权限的场景。
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
| `IPsExecService` | `PsExecService` | PsExec 参数构造、RunAs、流式输出 |
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

### 6.1 能力优先级

在用户描述的受限域环境下，程序主要按以下优先级工作：

1. 本机操作：目标是本机时直接调用本地 API 或本地进程。
2. WMI/DCOM：适合服务、设备、打印机、系统信息、磁盘、注册表 StdRegProv 等。
3. SMB 管理共享：适合复制脚本、访问 `ADMIN$`、公共桌面、磁盘路径。
4. PsExec 临时执行：适合 WMI 不足、目标必须在远端本机上下文执行的命令。
5. 本机 MMC/外部程序：如计算机管理、DameWare、打印机管理等交互入口。

WinRM 不作为核心依赖，因为目标环境通常不可用。

### 6.2 PsExecService

[PsExecService](src/RemoteOpsTool/Services/PsExecService.cs) 是所有 PsExec 调用的统一入口。

主要能力：

- 优先选择 `PsExec64.exe` 或 `PsExec.exe`。
- 使用所选凭据 RunAs 启动 PsExec 进程。
- 可选择是否在 PsExec 参数中显式带 `-u/-p`。
- 自动附加 `-accepteula`、`-nobanner`、`-n`、`-r`、`-h`、`-s`。
- 默认远端工作目录为 `C:\Windows\System32`。
- 支持普通执行、流式输出、交互会话执行。
- 日志中使用 `CredentialMasker` 隐藏密码。

典型远程命令形态：

```text
PsExec \\HOST -u DOMAIN\admin -p ******** -accepteula -nobanner -n 10 -r RemoteOpsTool_HOST_PID_0001 -h -s -w C:\Windows\System32 cmd /c <command>
```

当 `OmitPsExecExplicitCredentialsWhenRunAs` 为 true 时：

- PsExec 进程仍使用所选凭据 RunAs。
- 命令行中省略 `-u/-p`。
- 适合不希望 PsExec 命令参数出现凭据的场景。

### 6.3 WMI/DCOM 优先

多个服务采用 WMI/DCOM 优先、PsExec 兜底：

- `DeviceService`
- `FileDiskService`
- `ServiceManagerService`
- `PrinterService`
- `SoftwareService`
- `EnvVarService`
- `SystemInfoService`
- `NetworkService` 的部分能力

WMI 连接由 `RemoteWmiHelper.CreateScope()` 统一创建，使用传入的运维凭据连接 `\\host\root\cimv2` 或注册表 Provider。

### 6.4 SMB 连接

`NetworkShareCredentialHelper` 用于建立和清理由本工具创建的 SMB 连接，例如：

- `\\HOST\ADMIN$`
- `\\HOST\C$`
- `\\HOST\D$`
- `\\HOST\Users\Public\Desktop`

断开连接时，`MainViewModel.Disconnect()` 会调用 `NetworkShareCredentialHelper.DisconnectAll()` 清理本工具建立的连接。

## 7. 功能模块

| UI 功能 | ViewModel | Service | 主要通道 |
| --- | --- | --- | --- |
| 凭据管理 | `CredentialViewModel` | `CredentialService` | 本地 DPAPI/JSON |
| 连接和 Ping | `ConnectionViewModel` | `NetworkService` | ICMP |
| DameWare 远控 | `ConnectionViewModel` | `DameWareService` | 外部程序 |
| 文件/磁盘入口 | `FileDiskViewModel` | `FileDiskService` | SMB、WMI、PsExec |
| 清理空间 | `DiskCleanupViewModel` | `FileDiskService` | PsExec |
| 磁盘信息 | `DiskInfoViewModel` | `FileDiskService` | WMI 优先 |
| 设备管理 | `DeviceManagerViewModel` | `DeviceService` | WMI 优先、PsExec 兜底 |
| 服务管理 | `ServiceManagerViewModel` | `ServiceManagerService` | WMI 优先、PsExec 兜底 |
| 服务属性 | `ServicePropertiesViewModel` | `PsExecService` + WMI | WMI 优先，sc 兜底 |
| 打印机管理 | `PrinterManagerViewModel` | `PrinterService` | WMI 优先、PsExec 兜底 |
| 软件管理 | `SoftwareManagerViewModel` | `SoftwareService` | WMI/注册表/PsExec |
| 环境变量 | `EnvVarViewModel` | `EnvVarService` | StdRegProv、PsExec |
| 系统信息 | `SystemInfoViewModel` | `SystemInfoService` | WMI 优先、PsExec 兜底 |
| 注册表 | `RemoteRegistryViewModel` | `PsExecService` + WMI | StdRegProv、reg.exe |
| 进程管理 | `ProcessListViewModel` | `NetworkService` | WMI、tasklist、PsExec |
| 网络连接 | `NetworkPortsViewModel` | `NetworkService` | netstat、tasklist、WMI |
| 命令终端 | `TerminalViewModel` | `PsExecService` | PsExec 流式输出 |

### 7.1 缓存策略

缓存仅用于远程查询成本高、短时间内可接受快照展示的数据。进程、端口、会话、Ping、能力探测、磁盘剩余空间、文件共享访问和命令执行属于活动状态或操作结果，始终实时查询目标主机，不落持久缓存。

缓存键统一由 `CacheKeys` 生成，TTL 由 `CacheService` 统一维护。动态键家族可用 `InvalidateByPrefix()` 整组失效；未登记的新缓存键按 5 分钟保底 TTL 处理，避免遗漏策略后形成永久旧缓存。

| 数据 | 策略 | TTL | 必须失效/刷新 |
| --- | --- | --- | --- |
| 服务列表与状态 | 短快照缓存 | 15 秒 | 启动、停止、重启、服务属性窗口关闭、手动刷新 |
| 打印机清单、共享、默认状态 | 短快照缓存 | 1 分钟 | 删除、共享切换、默认切换、原生属性窗口/添加向导入口、手动刷新 |
| 注册表当前键值 | 极短快照缓存 | 30 秒 | 值增删改、键增删改影响当前路径、缓存到期后重新导航 |
| 环境变量 | 目标范围快照缓存 | 2 分钟 | 当前系统/用户变量增删改、手动刷新 |
| 设备列表 | 快照缓存 | 5 分钟 | 启用、禁用、卸载、驱动更新、手动刷新 |
| 系统信息 | 组合快照缓存 | 5 分钟 | 过期后重新打开窗口回查；动态磁盘和网络状态使用对应实时功能 |
| 软件清单 | 长快照缓存 | 15 分钟 | 静默/交互卸载、深度注册表清理、手动刷新；普通和深度键整组失效 |

| 实时数据 | 原因 |
| --- | --- |
| 进程列表、进程窗口、登录会话 | 秒级变化，界面支持主动/自动刷新 |
| 网络端口和活动连接 | 连接生命周期短，进程终止后必须立即反映 |
| Ping、连通性能力探测 | 表示当前网络与通道能力 |
| 磁盘容量、磁盘清理结果 | 空间变化与操作结果直接相关 |
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
| `OmitPsExecExplicitCredentialsWhenRunAs` | RunAs 后是否省略 PsExec `-u/-p` |
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
      │   └─ PsExecService 启动 PsExec
      ├─ 解析输出到 Model
      └─ 更新 ObservableCollection / ObservableProperty
```

注意点：

- UI 状态通过 `ObservableProperty` 通知。
- 长耗时操作使用 `async/await`，避免直接阻塞 UI 线程。
- 外部进程由 `ProcessHelper` 统一封装。
- 流式远程输出通过回调逐行写入日志。
- 部分窗口构造后立即后台加载，例如进程管理、系统信息、磁盘信息。
- 注册表窗口初始化在后台执行，避免窗口刚打开又关闭时锁住按钮。

## 12. 安全边界

### 12.1 本工具做了什么

- 使用用户选择的运维凭据访问目标主机。
- 通过 WMI/DCOM、SMB、PsExec 临时服务执行管理操作。
- 将密码本地 DPAPI 加密保存。
- 在日志中隐藏命令行密码。
- PsExec 临时服务名使用 `RemoteOpsTool_<host>_<pid>_<counter>` 模式，避免冲突。

### 12.2 本工具不做什么

- 不部署常驻 Agent。
- 不搭建中心服务器。
- 不依赖 WinRM。
- 不启用或要求目标主机远程注册表服务。
- 不在目标主机注入永久服务。
- 不做完整审计/回滚系统。

### 12.3 PsExec 提示说明

PsExec 输出中的 `Copying authentication key to HOST...` 是 PsExec 自身的握手/认证材料提示，不表示日志中泄露了明文密码。程序侧仍需注意：

- 不在日志中输出明文密码。
- 可启用“PsExec 已使用运维凭据 RunAs 时，省略命令 `-u/-p`”减少命令行凭据暴露面。
- PsExec 本身会在目标主机创建临时服务，这是其工作机制，不是长期 Agent。

## 13. 外部工具

| 工具 | 是否必需 | 用途 |
| --- | --- | --- |
| PsExec.exe / PsExec64.exe | 远程执行必需 | 远程命令、脚本、交互程序、兜底操作 |
| DameWare 远控程序 | 可选 | 远程桌面控制 |
| Windows 内置命令 | 必需 | `cmd`、`powershell`、`sc`、`reg`、`query`、`tasklist`、`netstat` 等 |

`ToolSetupService` 可以检测 PsExec 是否存在，并可下载 Sysinternals PsTools 到配置目录。

## 14. 发布与版本管理

### 14.1 当前版本

当前发布版本为 **1.4.0**。本版本属于次版本功能发布：磁盘清理窗口新增“删除目录本身”选项；未勾选时仅清空匹配目录的内容，勾选时删除匹配目录及其全部内容；文件目标始终直接删除。删除后继续执行实际验证，并禁止删除磁盘根目录或共享根目录。

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
<Version>1.4.0</Version>
<AssemblyVersion>1.4.0.0</AssemblyVersion>
<FileVersion>1.4.0.0</FileVersion>
<InformationalVersion>1.4.0</InformationalVersion>
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
$version = "1.4.0"
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
D:\path\to\RemoteOpsTool\publish\RemoteOpsTool 1.4.0.exe
```

旧版本发布文件可以保留用于回滚，但新版本不得继续使用 `v2`、`v3`、`v4` 等无法表达变更级别的命名方式。
## 15. 设计取舍

| 取舍 | 当前选择 | 原因 |
| --- | --- | --- |
| 远程管理通道 | WMI/DCOM 优先，PsExec 兜底 | 适配无 WinRM、无 Agent 的域环境 |
| 凭据使用 | PsExec 进程 RunAs 所选凭据 | 本机登录账号可能无管理员权限 |
| 注册表 | WMI StdRegProv + reg.exe | 不依赖 Remote Registry 服务 |
| 进程管理 | WMI/tasklist/PsExec 多路径 | 目标环境查询能力不稳定，需要多重兜底 |
| UI 架构 | 单主窗口 + 多功能子窗口 | 保持工具密度和运维效率 |
| 启动策略 | 首屏优先，工具检查后台化 | 降低启动等待感 |
| 发布方式 | 自包含单文件 | 方便复制到公司电脑直接运行 |

## 16. 后续维护建议

- 新增远程能力时优先放到 Service，不要直接写在 ViewModel 或 code-behind。
- 新的远程查询优先考虑 WMI/DCOM 或 SMB，只有必要时再走 PsExec。
- 所有 PsExec 命令都应经过 `PsExecService`，不要在功能模块里直接启动 PsExec。
- 所有密码和命令日志都必须经过脱敏。
- 新增表格窗口时复用全局 DataGrid 样式和 `DataGridRowClickHelper`。
- 复杂远程输出优先使用结构化格式，例如 CSV 或 JSON，少用纯字符串位置解析。
- 高风险操作应至少有确认框和日志输出。
