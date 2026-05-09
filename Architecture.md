# RemoteOpsTool 架构设计方案

## 目录
1. [技术栈与依赖](#1-技术栈与依赖)
2. [项目目录结构](#2-项目目录结构)
3. [分层架构设计](#3-分层架构设计)
4. [依赖注入与启动流程](#4-依赖注入与启动流程)
5. [核心模块设计](#5-核心模块设计)
6. [数据流与线程模型](#6-数据流与线程模型)
7. [配置与持久化](#7-配置与持久化)
8. [凭据安全方案](#8-凭据安全方案)
9. [UI 布局方案](#9-ui-布局方案)
10. [发布与部署](#10-发布与部署)

---

## 1. 技术栈与依赖

| 层级 | 技术选型 | 说明 |
|------|---------|------|
| 运行时 | .NET 10 (net10.0-windows) | WPF 原生支持，单文件发布 |
| UI 框架 | WPF | 成熟稳定，控件丰富 |
| UI 组件库 | HandyControl 3.8+ | 开箱即用的 WPF 控件库 |
| MVVM 工具 | CommunityToolkit.Mvvm 8.4+ | 源码生成器，简化 MVVM |
| DI 容器 | Microsoft.Extensions.DependencyInjection | .NET 官方 DI |
| 配置管理 | Microsoft.Extensions.Configuration.Json | JSON 配置文件 |
| 日志 | 自定义 LogService + 界面展示 | 不引入额外日志框架 |
| 加密 | System.Security.Cryptography.ProtectedData | Windows DPAPI |
| 进程调用 | System.Diagnostics.Process | 标准库 |

### 外部工具依赖（非 NuGet，需用户环境具备）

| 工具 | 用途 | 默认路径 |
|------|------|---------|
| PsExec.exe | 远程命令执行引擎 | `C:\ProgramData\RemoteAdmin\Tools\` |
| PsService.exe | 服务管理 | 同上 |
| PsKill.exe | 进程终止 | 同上 |
| DameWare.exe | 远程桌面控制 | 用户安装路径（可配置） |
| devcon.exe | 设备管理（可选） | PsTools 目录或系统路径 |

---

## 2. 项目目录结构

```
RemoteOpsTool/
├── RemoteOpsTool.sln
├── Architecture.md                         # 本文件
├── Init.md                                 # 需求 Skill 文档
├── publish.bat                             # 发布脚本
│
├── src/
│   └── RemoteOpsTool/
│       ├── RemoteOpsTool.csproj
│       ├── App.xaml / App.xaml.cs
│       ├── GlobalUsings.cs                 # 全局 using
│       │
│       ├── Models/                         # 数据模型（POCO）
│       │   ├── AppSettings.cs              # 持久化配置
│       │   ├── CredentialInfo.cs           # 凭据信息
│       │   ├── DiskInfo.cs                 # 磁盘信息
│       │   ├── LogEntry.cs                 # 日志条目
│       │   ├── ServiceInfo.cs              # 服务信息
│       │   ├── DeviceInfo.cs               # 设备信息
│       │   ├── PrinterInfo.cs              # 打印机信息
│       │   ├── SoftwareInfo.cs             # 已安装软件
│       │   ├── EnvVariableInfo.cs          # 环境变量
│       │   ├── NetworkConnectionInfo.cs    # 网络连接
│       │   └── ProcessInfo.cs              # 进程信息
│       │
│       ├── ViewModels/                     # 视图模型
│       │   ├── MainViewModel.cs            # 主窗口 VM（聚合所有子 VM）
│       │   ├── StatusBarViewModel.cs       # 顶部状态栏
│       │   ├── LogViewModel.cs             # 日志区域
│       │   ├── ConnectionViewModel.cs      # 连接管理
│       │   ├── FileDiskViewModel.cs        # 文件与磁盘
│       │   ├── RemoteManagementViewModel.cs # 远程管理（聚合）
│       │   ├── InteractiveViewModel.cs     # 发送交互程序
│       │   ├── NetworkViewModel.cs         # 网络工具
│       │   ├── TerminalViewModel.cs        # 命令终端
│       │   ├── CredentialViewModel.cs      # 凭据管理弹窗
│       │   ├── SettingsViewModel.cs        # 高级设置弹窗
│       │   │
│       │   └── Dialogs/                    # 子功能窗口 VM
│       │       ├── DeviceManagerViewModel.cs
│       │       ├── ServiceManagerViewModel.cs
│       │       ├── PrinterManagerViewModel.cs
│       │       ├── SoftwareManagerViewModel.cs
│       │       ├── EnvVarViewModel.cs
│       │       ├── SystemInfoViewModel.cs
│       │       ├── DiskCleanupViewModel.cs
│       │       ├── NetworkPortsViewModel.cs
│       │       └── DiskInfoViewModel.cs
│       │
│       ├── Views/                          # 视图
│       │   ├── MainWindow.xaml / .xaml.cs
│       │   ├── Controls/                   # 可复用用户控件
│       │   │   ├── StatusBarControl.xaml
│       │   │   ├── LogViewerControl.xaml
│       │   │   ├── ConnectionControl.xaml
│       │   │   ├── FileDiskControl.xaml
│       │   │   ├── RemoteManagementControl.xaml
│       │   │   ├── InteractiveControl.xaml
│       │   │   ├── NetworkControl.xaml
│       │   │   └── TerminalControl.xaml
│       │   │
│       │   ├── Dialogs/                    # 弹窗/子窗口
│       │   │   ├── CredentialDialog.xaml
│       │   │   ├── SettingsDialog.xaml
│       │   │   ├── DeviceManagerWindow.xaml
│       │   │   ├── ServiceManagerWindow.xaml
│       │   │   ├── PrinterManagerWindow.xaml
│       │   │   ├── SoftwareManagerWindow.xaml
│       │   │   ├── EnvVarWindow.xaml
│       │   │   ├── SystemInfoWindow.xaml
│       │   │   ├── DiskCleanupWindow.xaml
│       │   │   ├── NetworkPortsWindow.xaml
│       │   │   └── DiskInfoWindow.xaml
│       │   │
│       │   └── Resources/
│       │       ├── Styles.xaml             # 全局样式
│       │       ├── Brushes.xaml            # 颜色资源
│       │       └── FontFamilys.xaml        # 字体资源
│       │
│       ├── Services/                       # 业务逻辑层
│       │   ├── Interfaces/                 # 服务接口
│       │   │   ├── ICredentialService.cs
│       │   │   ├── ISettingsService.cs
│       │   │   ├── ILogService.cs
│       │   │   ├── IPsExecService.cs
│       │   │   ├── IToolSetupService.cs
│       │   │   ├── IDameWareService.cs
│       │   │   ├── IFileDiskService.cs
│       │   │   ├── IDeviceService.cs
│       │   │   ├── IServiceManagerService.cs
│       │   │   ├── IPrinterService.cs
│       │   │   ├── ISoftwareService.cs
│       │   │   ├── IEnvVarService.cs
│       │   │   ├── ISystemInfoService.cs
│       │   │   └── INetworkService.cs
│       │   │
│       │   ├── CredentialService.cs        # DPAPI 凭据管理
│       │   ├── SettingsService.cs          # JSON 配置读写
│       │   ├── LogService.cs              # 内存日志收集
│       │   ├── PsExecService.cs           # PsExec 进程封装
│       │   ├── ToolSetupService.cs        # 工具检测/下载/解压
│       │   ├── DameWareService.cs         # DameWare 调用
│       │   ├── FileDiskService.cs         # 文件/磁盘操作
│       │   ├── DeviceService.cs           # 设备管理
│       │   ├── ServiceManagerService.cs   # 服务管理
│       │   ├── PrinterService.cs          # 打印机管理
│       │   ├── SoftwareService.cs         # 软件管理
│       │   ├── EnvVarService.cs           # 环境变量
│       │   ├── SystemInfoService.cs       # 系统信息
│       │   └── NetworkService.cs          # 网络工具
│       │
│       ├── Helpers/                        # 工具类
│       │   ├── ProcessHelper.cs           # Process 启动封装
│       │   ├── SecureStringHelper.cs      # 敏感字符串处理
│       │   ├── SessionHelper.cs           # 会话 ID 获取
│       │   ├── OutputParser.cs            # PowerShell/WMIC 输出解析
│       │   ├── NetworkPathHelper.cs       # 网络路径拼接
│       │   └── CredentialMasker.cs        # 日志脱敏
│       │
│       ├── Constants/
│       │   └── AppConstants.cs            # 常量定义（路径、注册表键等）
│       │
│       └── Converters/                    # WPF 值转换器
│           ├── LogLevelToColorConverter.cs
│           ├── BytesToGBConverter.cs
│           ├── BoolToVisibilityConverter.cs
│           └── ConnectionStatusToColorConverter.cs
│
└── tests/
    └── RemoteOpsTool.Tests/
        ├── RemoteOpsTool.Tests.csproj
        ├── Services/
        │   ├── CredentialServiceTests.cs
        │   ├── SettingsServiceTests.cs
        │   ├── PsExecServiceTests.cs
        │   └── OutputParserTests.cs
        └── Helpers/
            └── SessionHelperTests.cs
```

---

## 3. 分层架构设计

```
┌─────────────────────────────────────────────────────────┐
│                    Views (WPF XAML)                      │
│          MainWindow / Controls / Dialogs                 │
├─────────────────────────────────────────────────────────┤
│                ViewModels (CommunityToolkit.Mvvm)        │
│    MainViewModel 聚合子 VM，通过 DI 注入所有 Service      │
├─────────────────────────────────────────────────────────┤
│                  Services (Business Logic)               │
│    ICredentialService / IPsExecService / ...             │
│    所有远程操作最终都通过 IPsExecService 执行              │
├─────────────────────────────────────────────────────────┤
│               Helpers & Infrastructure                   │
│    ProcessHelper / SessionHelper / OutputParser / DPAPI  │
├─────────────────────────────────────────────────────────┤
│                   External Tools                         │
│    PsExec.exe / PsService.exe / DameWare.exe / ...       │
└─────────────────────────────────────────────────────────┘
```

### 层级职责

| 层 | 职责 | 规则 |
|---|------|------|
| **Views** | 纯 XAML 布局 + Code-behind 仅做 UI 相关操作 | 不直接调用 Service，通过 Binding/Command 与 VM 交互 |
| **ViewModels** | 持有 ObservableProperty/RelayCommand，协调 Service 调用 | 不引用 View，不处理 UI 控件细节 |
| **Services** | 执行业务逻辑，封装外部工具调用，数据持久化 | 接口化，可 Mock 可测试 |
| **Helpers** | 通用工具函数，无状态，纯函数风格 | 不依赖其他 Service |
| **Models** | 纯数据对象，实现 INotifyPropertyChanged（通过 CommunityToolkit ObservableObject 或 record） | 无业务逻辑 |

---

## 4. 依赖注入与启动流程

### 4.1 App.xaml.cs 启动入口

```csharp
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var services = new ServiceCollection();

        // --- Services (Singleton) ---
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IPsExecService, PsExecService>();
        services.AddSingleton<IToolSetupService, ToolSetupService>();
        services.AddSingleton<IDameWareService, DameWareService>();
        services.AddSingleton<IFileDiskService, FileDiskService>();
        services.AddSingleton<IDeviceService, DeviceService>();
        services.AddSingleton<IServiceManagerService, ServiceManagerService>();
        services.AddSingleton<IPrinterService, PrinterService>();
        services.AddSingleton<ISoftwareService, SoftwareService>();
        services.AddSingleton<IEnvVarService, EnvVarService>();
        services.AddSingleton<ISystemInfoService, SystemInfoService>();
        services.AddSingleton<INetworkService, NetworkService>();

        // --- ViewModels (Transient / Singleton as needed) ---
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddTransient<TerminalViewModel>();
        services.AddTransient<CredentialViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Sub-window ViewModels
        services.AddTransient<DeviceManagerViewModel>();
        services.AddTransient<ServiceManagerViewModel>();
        // ... etc

        // --- MainWindow ---
        services.AddSingleton<MainWindow>();

        Services = services.BuildServiceProvider();

        // 启动前检查工具
        var toolSetup = Services.GetRequiredService<IToolSetupService>();
        _ = toolSetup.EnsureToolsAsync();  // 首次启动检测/下载 PsTools

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }
}
```

### 4.2 启动序列

```
App.OnStartup()
  └─> DI Container 构建
        └─> IToolSetupService.EnsureToolsAsync()
              ├─ 检测 C:\ProgramData\RemoteAdmin\Tools\PsExec.exe
              ├─ 不存在 → 提示下载 / 自动下载
              └─ 存在 → 跳过
        └─> ISettingsService.Load()
              └─ 读取 %AppData%\RemoteAdmin\settings.json
        └─> ICredentialService.Load()
              └─ 读取 %AppData%\RemoteAdmin\credentials.dat
        └─> MainWindow 构造并显示
              └─ DataContext = MainViewModel
```

---

## 5. 核心模块设计

### 5.1 IPsExecService（所有远程操作的基础）

```csharp
public interface IPsExecService
{
    /// <summary>
    /// 基础远程执行 - 异步读取输出，支持取消
    /// </summary>
    Task<CommandResult> ExecuteAsync(
        string targetHost,
        string username,
        string password,
        string command,
        bool interactiveSession = false,  // 是否带 -i
        int sessionId = 1,                // 交互会话 ID
        CancellationToken ct = default);

    /// <summary>
    /// 实时输出版本 - 通过回调返回每一行
    /// </summary>
    Task ExecuteWithOutputAsync(
        string targetHost,
        string username,
        string password,
        string command,
        Action<string> onOutputLine,
        CancellationToken ct = default);

    /// <summary>
    /// 获取目标主机活动用户会话 ID
    /// </summary>
    Task<int> GetActiveSessionIdAsync(string targetHost, string username, string password);
}
```

**CommandResult 结构**：
```csharp
public record CommandResult(int ExitCode, string StdOut, string StdErr);
```

**实现要点**：
- 使用 `Process.Start()` 启动 `PsExec.exe`，参数按 Init.md 5.1 格式拼接
- 通过 `ProcessStartInfo.RedirectStandardOutput = true` 重定向输出
- `ExecuteWithOutputAsync` 在 `Task.Run` 中逐行读取 `Process.StandardOutput`
- 使用 `BeginErrorReadLine` + `ErrorDataReceived` 事件异步读取 stderr
- 命令字符串中的密码**不记录到日志**（由 LogService 脱敏处理）

### 5.2 ICredentialService（凭据管理）

```csharp
public interface ICredentialService
{
    ObservableCollection<CredentialInfo> Credentials { get; }
    List<CredentialInfo> SelectedCredentials { get; }   // 勾选的凭据

    Task LoadAsync();
    Task SaveAsync();

    void Add(CredentialInfo credential);
    void Remove(CredentialInfo credential);
    void ClearAll();

    // 解密获取明文密码（仅用于传递给 PsExec，不在界面展示）
    string DecryptPassword(CredentialInfo credential);
}
```

**加密存储格式**：
```
%AppData%\RemoteAdmin\credentials.dat
```
- JSON 数组，每条记录：`{ "UserName": "DOMAIN\\user", "EncryptedPassword": "base64...", "Description": "域账号" }`
- `EncryptedPassword = Convert.ToBase64String(ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser))`
- 明文密码解密后仅在 `PsExecService.ExecuteAsync()` 内部使用 `SecureStringHelper` 处理，用完立即释放

### 5.3 ISettingsService（配置管理）

```csharp
public interface ISettingsService
{
    AppSettings Settings { get; }

    Task LoadAsync();
    Task SaveAsync();
}
```

```csharp
public class AppSettings
{
    public string PsToolsPath { get; set; } = @"C:\ProgramData\RemoteAdmin\Tools";
    public string DameWarePath { get; set; } = string.Empty;
    public string DomainPublicPath { get; set; } = string.Empty;
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public WindowState WindowState { get; set; } = WindowState.Normal;
}
```

配置文件路径：`Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteAdmin", "settings.json")`

### 5.4 ILogService（日志系统）

```csharp
public interface ILogService
{
    ObservableCollection<LogEntry> Entries { get; }
    LogLevel FilterLevel { get; set; }   // 筛选级别

    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Log(LogLevel level, string message);
    void Clear();
}
```

```csharp
public record LogEntry(DateTime Timestamp, LogLevel Level, string Message);
public enum LogLevel { Info, Warn, Error }
```

**特点**：
- `ObservableCollection<LogEntry>` 直接绑定到 `ItemsControl` (使用 HandyControl 的高性能 `VirtualizingStackPanel`)
- 所有远程命令执行结果都通过 `ILogService` 输出到日志区
- 密码参数由 `CredentialMasker.MaskPasswordInCommand(string command)` 脱敏后再记录

### 5.5 MainViewModel（核心聚合 VM）

```csharp
public partial class MainViewModel : ObservableObject
{
    // --- 子 ViewModel ---
    public StatusBarViewModel StatusBar { get; }
    public LogViewModel Log { get; }
    public ConnectionViewModel Connection { get; }
    public FileDiskViewModel FileDisk { get; }
    public RemoteManagementViewModel RemoteManagement { get; }
    public InteractiveViewModel Interactive { get; }
    public NetworkViewModel Network { get; }
    public TerminalViewModel Terminal { get; }

    // --- 弹窗命令 ---
    [RelayCommand] private void OpenCredentials() { /* ... */ }
    [RelayCommand] private void OpenSettings() { /* ... */ }
    [RelayCommand] private void Disconnect() { /* 终止所有到目标主机的 PsExec 进程 */ }
}
```

### 5.6 功能模块服务一览

| 功能模块 | 对应 Service | 对应 ViewModel | 窗口类型 |
|---------|-------------|---------------|---------|
| 连接管理 | PsExecService + NetworkService | ConnectionViewModel | 左侧内嵌 |
| 文件与磁盘 | FileDiskService | FileDiskViewModel | 左侧内嵌 + DiskCleanupWindow |
| 设备管理器 | DeviceService | DeviceManagerViewModel | 独立子窗口 |
| 服务管理器 | ServiceManagerService | ServiceManagerViewModel | 独立子窗口 |
| 打印机管理 | PrinterService | PrinterManagerViewModel | 独立子窗口 |
| 软件管理 | SoftwareService | SoftwareManagerViewModel | 独立子窗口 |
| 环境变量 | EnvVarService | EnvVarViewModel | 独立子窗口 |
| 系统信息 | SystemInfoService | SystemInfoViewModel | 独立子窗口 |
| 网络工具 | NetworkService | NetworkViewModel | 左侧内嵌 + NetworkPortsWindow |
| 命令终端 | PsExecService | TerminalViewModel | 右侧内嵌 |

---

## 6. 数据流与线程模型

### 6.1 典型操作流程（以"获取目标主机磁盘信息"为例）

```
用户点击 [获取磁盘信息] 按钮
  │
  ▼
FileDiskViewModel.GetDiskInfoCommand (RelayCommand, async)
  │
  ├─ 从 CredentialService 获取已选凭据
  ├─ Calling ILogService.Info("正在获取磁盘信息...")
  │
  ▼
IFileDiskService.GetDiskInfoAsync(host, username, password)
  │
  ├─ 构造 PowerShell 命令（Init.md 5.2）
  ├─ 调用 IPsExecService.ExecuteAsync(...)
  │
  ▼
PsExecService 内部：
  │
  ├─ Process.Start("PsExec.exe", args)
  ├─ 异步读取 StdOut/StdErr
  ├─ 返回 CommandResult
  │
  ▼
FileDiskService 解析 CSV 输出 → List<DiskInfo>
  │
  ▼
FileDiskViewModel.DiskInfos = new ObservableCollection<DiskInfo>(result)
  ├─ WPF Binding 自动更新 UI
  └─ ILogService.Info("获取磁盘信息完成")
```

### 6.2 线程模型

```
┌───────────────┐     async/await      ┌──────────────────┐
│  UI Thread    │ ◄─────────────────── │  Background      │
│  (Dispatcher) │                      │  (ThreadPool)    │
│               │                      │                  │
│  ViewModel    │                      │  PsExecService   │
│  Command      │ ───── Task.Run ────► │  .ExecuteAsync() │
│  Binding      │                      │                  │
│  Observable   │ ◄── Dispatcher ──── │  LogService      │
│  Collection   │     .InvokeAsync     │  .Log()          │
└───────────────┘                      └──────────────────┘
```

- **UI 线程**：处理 WPF 绑定更新、命令触发
- **后台线程**：所有 `Process` 调用在 `Task.Run` 中执行
- **日志回写**：`LogService.Log()` 内部使用 `Application.Current.Dispatcher.InvokeAsync` 确保线程安全

### 6.3 撤销/重做 (Undo/Redo)

- 不实现全局的撤销/重做（不适用于远程运维操作）。
- 每个子窗口内对列表的增删改操作，使用 `ObservableCollection` + 本地 `Stack<IUndoable>` 实现简单回退。

---

## 7. 配置与持久化

### 7.1 文件位置

| 文件 | 路径 |
|------|------|
| 设置 | `%AppData%\RemoteAdmin\settings.json` |
| 凭据 | `%AppData%\RemoteAdmin\credentials.dat` |
| PsTools | `C:\ProgramData\RemoteAdmin\Tools\` (可配置) |

### 7.2 settings.json 示例

```json
{
  "psToolsPath": "C:\\ProgramData\\RemoteAdmin\\Tools",
  "dameWarePath": "C:\\Program Files\\DameWare\\DWRCC.exe",
  "domainPublicPath": "\\\\dc01\\Public",
  "windowLeft": 100,
  "windowTop": 100,
  "windowWidth": 1200,
  "windowHeight": 800,
  "windowState": "Normal"
}
```

### 7.3 credentials.dat 内部结构

```json
[
  {
    "userName": "DOMAIN\\admin",
    "encryptedPassword": "AQAAANCMnd8BFdER...",
    "description": "域管理员账号"
  },
  {
    "userName": "admin@domain.com",
    "encryptedPassword": "AQAAANCMnd8BFdER...",
    "description": "AAD 账号"
  }
]
```

---

## 8. 凭据安全方案

### 8.1 加密流程

```
明文密码 ──► Encoding.UTF8.GetBytes() ──► ProtectedData.Protect(DataProtectionScope.CurrentUser)
                                                  │
                                                  ▼
                                        Base64 编码后存入文件
```

### 8.2 解密流程

```
文件读取 ──► Base64 解码 ──► ProtectedData.Unprotect() ──► SecureStringHelper 包装
                                                               │
                                                               ▼
                                              传递给 ProcessStartInfo.Password
                                              (SecureString → 明文字符串转换仅在使用时)
```

### 8.3 内存保护

- 解密后的密码字符串使用 `SecureStringHelper` 包装：
  ```csharp
  internal static class SecureStringHelper
  {
      public static SecureString ToSecureString(string plainText);
      public static string ToPlainString(SecureString secureString); // 仅 PsExecService 内部使用
      public static void Dispose(SecureString secureString);
  }
  ```
- 使用后立即调用 `Array.Clear()` 覆盖内存或 `Dispose()`

### 8.4 日志脱敏

```csharp
internal static class CredentialMasker
{
    public static string MaskPasswordInCommand(string commandLine, string password)
    {
        if (string.IsNullOrEmpty(password)) return commandLine;
        return commandLine.Replace(password, "********");
    }
}
```

---

## 9. UI 布局方案

### 9.1 MainWindow 整体布局

```
┌──────────────────────────────────────────────────────────┐
│  [Target Host: ___________] [状态指示]  C: xxx GB  D: xxx GB  [时间]  │ ← StatusBarControl
├────────────┬─────────────────────────────────────────────┤
│            │                                               │
│ 连接管理    │  ┌─────────────────────────────────────────┐ │
│ ┌────────┐ │  │                                         │ │
│ │ 凭据   │ │  │        日志区域 (LogViewerControl)        │ │
│ │ Ping   │ │  │                                         │ │
│ │ 持续Ping│ │  │  [筛选: Info ▼] [清空日志] [复制]         │ │
│ │ DameWare│ │  │                                         │ │
│ │ 断开   │ │  ├─────────────────────────────────────────┤ │
│ └────────┘ │  │  > _                                    │ │
│            │  │        命令终端 (TerminalControl)          │ │
│ 文件与磁盘  │  │  [加载脚本] [执行]                         │ │
│ ┌────────┐ │  └─────────────────────────────────────────┘ │
│ │C盘     │ │                                               │
│ │公共桌面 │ │                                               │
│ │域公共   │ │                                               │
│ │清理空间 │ │                                               │
│ │磁盘选择 │ │                                               │
│ │磁盘信息 │ │                                               │
│ └────────┘ │                                               │
│            │                                               │
│ 远程管理    │                                               │
│ ┌────────┐ │                                               │
│ │设备管理 │ │                                               │
│ │服务管理 │ │                                               │
│ │打印机   │ │                                               │
│ │软件管理 │ │                                               │
│ │环境变量 │ │                                               │
│ │系统信息 │ │                                               │
│ └────────┘ │                                               │
│            │                                               │
│ 发送交互程序 │                                               │
│ ┌────────┐ │                                               │
│ │cmd     │ │                                               │
│ │计算机管理│ │                                               │
│ │打印机管理│ │                                               │
│ │注册表   │ │                                               │
│ │环境变量 │ │                                               │
│ └────────┘ │                                               │
│            │                                               │
│ 网络工具    │                                               │
│ ┌────────┐ │                                               │
│ │刷新DNS │ │                                               │
│ │端口进程 │ │                                               │
│ └────────┘ │                                               │
│            │                                               │
│ ┌────────┐ │                                               │
│ │高级设置 │ │                                               │
│ └────────┘ │                                               │
│            │                                               │
│ ← 280px → │ ←────────────── 自适应 ──────────────────→   │
└────────────┴─────────────────────────────────────────────┘
```

### 9.2 HandyControl 控件使用

| UI 元素 | HandyControl 控件 |
|---------|-------------------|
| 左侧按钮组 | `Button` + `SimplePanel` |
| 日志列表 | `ListBox` + `VirtualizingStackPanel` |
| 磁盘下拉框 | `ComboBox` |
| 终端输入 | `TextBox` (MultiLine) |
| 弹窗 | `Dialog` (模态) / `Window` (非模态子窗口) |
| 凭据弹窗 | `Dialog` 内嵌 `DataGrid` |
| 状态指示 | `CircleProgressBar` 或自定义 `Ellipse` + 颜色绑定 |
| 主题 | HandyControl 内置 Dark/Light 主题 |

### 9.3 字体全局设置

在 `App.xaml` 中设置：

```xml
<Application.Resources>
    <ResourceDictionary>
        <FontFamily x:Key="AppFont">pack://application:,,,/SarasaMonoSC-Regular.ttf#Sarasa Mono SC</FontFamily>
        <Style TargetType="Window">
            <Setter Property="FontFamily" Value="{StaticResource AppFont}" />
        </Style>
    </ResourceDictionary>
</Application.Resources>
```

---

## 10. 发布与部署

### 10.1 .csproj 配置

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <ApplicationIcon>Resources\app.ico</ApplicationIcon>
  </PropertyGroup>

  <PropertyGroup>
    <!-- 单文件发布 -->
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishReadyToRun>true</PublishReadyToRun>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="HandyControl" Version="3.*" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.*" />
  </ItemGroup>

</Project>
```

### 10.2 发布命令

```batch
:: publish.bat
dotnet publish src/RemoteOpsTool/RemoteOpsTool.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:PublishReadyToRun=true ^
    -o publish/
```

### 10.3 产物

`publish/RemoteOpsTool.exe` — 单个可执行文件，复制到任意 Windows 10+/Server 机器即可运行（仍需 .NET 10 运行时，但 SelfContained 模式已内嵌运行时）。

---

## 11. 关键设计决策总结

| 决策 | 选择 | 理由 |
|------|------|------|
| UI 框架 | WPF + HandyControl | WPF 在 .NET 10 上成熟稳定，HandyControl 提供开箱即用的现代控件 |
| MVVM 工具 | CommunityToolkit.Mvvm | 源码生成器减少样板代码，ObservableProperty/RelayCommand 一行搞定 |
| DI 容器 | Microsoft.Extensions.DI | .NET 官方容器，与 Configuration 集成良好 |
| 异步模式 | async/await + Task.Run | UI 线程不阻塞，Process 调用在后台线程执行 |
| 远程执行引擎 | PsExec（统一入口） | 所有功能最终都通过 PsExec 执行，避免多工具管理复杂性 |
| 配置格式 | JSON (appsettings) | 人可读可编辑，微软官方 Configuration 库支持 |
| 密码加密 | Windows DPAPI (CurrentUser) | 无需管理密钥，绑定当前用户，复制文件无法解密 |
| 发布方式 | 单文件自包含 EXE | 零依赖部署，直接复制运行 |
| 字体 | Sarasa Mono SC（内嵌） | 更纱黑体等宽字体，中英文显示优秀，嵌入到 exe 中避免依赖系统字体 |

---

## 12. 开发阶段建议

| 阶段 | 内容 | 预估 |
|------|------|------|
| **Phase 1** | 项目骨架搭建：csproj、DI、MainWindow 布局、主题/字体、Settings 读写 | 基础框架 |
| **Phase 2** | PsExecService + LogService + CredentialService 核心服务 | 核心引擎 |
| **Phase 3** | 连接管理 + 文件磁盘 + 终端模块（左侧 + 右侧 UI 贯通） | 核心功能 |
| **Phase 4** | 远程管理 6 个子功能（子窗口逐个实现） | 高级功能 |
| **Phase 5** | 网络工具 + 交互程序 + 高级设置 | 辅助功能 |
| **Phase 6** | ToolSetupService（工具下载/更新）、发布脚本、测试 | 交付准备 |
