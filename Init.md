# 域环境远程运维工具开发 Skill

## 1. 概述
本 Skill 用于指导开发一款 Windows 域环境远程运维桌面程序，运行在 .NET 10 上，主界面使用 WPF 或 WinUI 3，字体统一采用“更纱黑体 Mono”（Sarasa Mono SC）。程序依赖微软 PsTools 套件、DameWare 远程工具等既有微软/第三方工具，完成对 AD/AAD 混合域内目标主机的凭据化远程运维, UI技术选择WPF + HandyControl + CommunityToolkit.Mvvm。

---

## 2. 运行环境与初始化
- **首次启动行为**：
  - 检测 `C:\ProgramData\RemoteAdmin\Tools` 目录是否存在 PsTools 压缩包解压后的文件（如 `PsExec.exe`、`PsService.exe` 等）。
  - 若缺失，从微软官方下载页（https://learn.microsoft.com/sysinternals/downloads/pstools）引导下载，或内置下载逻辑自动获取 PsTools 最新 ZIP 包并解压至该目录。
  - 若已存在，提供“检测更新/重新下载/卸载/选择本地路径/打开微软下载页”按钮。
  - 所有第三方工具（DameWare 安装路径、PsTools 路径、域公共目录）均可由用户编辑并持久化保存到配置文件。

- **配置文件**：使用 `appsettings.json` 或 `%AppData%\RemoteAdmin\settings.json`，存放工具路径、域公共目录、窗口状态等。

---

## 3. 凭据管理
### 3.1 存储与加密
- 凭据包含：用户名（格式可为 `DOMAIN\username` 或 `username@domain.com`）、密码。
- 使用 Windows 数据保护 API（DPAPI，`System.Security.Cryptography.ProtectedData`）加密密码，范围限定为当前用户（`CurrentUser`）。加密后的数据保存到本地文件 `%AppData%\RemoteAdmin\credentials.dat`。
- 凭据文件拷贝至其他电脑或用户账户下无法解密，满足安全性要求。
- 凭据支持添加、删除、清空全部。
- 主界面提供复选框勾选需要使用的凭据，运维操作将优先使用该凭据进行身份验证（优先级高于后续可能添加的其他凭据）。

### 3.2 凭据界面
- 弹出窗口或内嵌面板，列表显示已保存凭据描述（脱敏显示：`DOMAIN\user***`），提供“添加”“删除所选”“清空全部”按钮。
- 添加凭据时，输入用户名、密码，可选描述。

---

## 4. 远程执行基础
统一通过 **PsExec** 实现以管理员权限远程执行命令/程序：
PsExec.exe \<TargetHost> -u <username> -p <password> -accepteula -h -d /c <command>
- `-h` 以系统提权令牌运行（如果凭据具有管理员权限）。
- `-accepteula` 自动接受 EULA（先运行一次或配置注册表）。
- 默认工作在交互模式，但为 UI 集成，使用重定向输出方式。
- 命令输出实时回传到右侧实时日志区。
- 对于需要图形界面的程序（如远程启动 compmgmt.msc），必须使用 `-i` 参数允许交互（目标主机上以管理员权限启动界面，并显示在目标主机的桌面（默认 session））。使用 `PsExec -i 1 ...` 指定会话 ID（通常为目标主机当前活动的控制台会话）。

---

## 5. 主要功能实现细节
### 5.1 连接管理
- **目标主机输入**：左侧顶部文本框，输入主机名或 IP。
- **Ping 测试**：
  - 按钮“Ping”：执行 `ping <host> -n 1`，在日志区显示结果。
  - 按钮“持续 Ping”：启动一个后台 `-t` ping，实时输出到日志，可中止。
- **凭据管理**、**清除凭据**、**断开连接**（终止所有到该主机的 PsExec 等进程）。
- **DameWare 远程连接**：
  - 调用 `Dameware.exe`（路径从配置读取）并传递参数：
  -c: -h:<TargetHost> -u:<username> -p:<password>
  具体参数按 DameWare 命令行文档拼接，确保静默连接。

### 5.2 文件与磁盘
所有文件/磁盘操作均使用已选凭据远程访问（通过 `\\<host>\c$` 管理共享）。
- **一键打开目标主机 C 盘根目录**：`explorer \\<TargetHost>\c$`
- **一键打开目标主机公共桌面**：`explorer \\<TargetHost>\c$\Users\Public\Desktop`
- **一键打开域服务器公共目录**：使用配置的域共享路径，如 `explorer \\<DomainServer>\Public`（路径从高级设置获取）。
- **一键清理目标主机空间**：
- 弹出对话框，列出常见临时/缓存目录（勾选确认）：
- `C:\Windows\Temp`
- `C:\Users\<AllUsers>\AppData\Local\Temp`
- `C:\Windows\Prefetch`
- `C:\Windows\SoftwareDistribution\Download`
- 点击“清理”后，通过 PsExec 运行 `del /f /s /q` 或 `cleanmgr` 完成任务。注意路径需要使用 `\\?\C:\...` 解决长路径问题。
- **磁盘选择下拉框 D–Z**：选中盘符如 `E:`，打开 `\\<TargetHost>\E$`。
- **获取所有磁盘信息**：使用 PsExec 执行以下命令后解析结果：
wmic logicaldisk where "DriveType=3" get DeviceID, FreeSpace, Size /format:csv
或使用 PowerShell 命令更简单：
powershell "Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID, @{N='FreeGB';E={[math]::Round(_.FreeSpace/1GB,2)}}, @{N='SizeGB';E={[math]::Round(_.Size/1GB,2)}} | ConvertTo-Csv -NoTypeInformation"
解析后展示在子窗口或右侧日志区。

### 5.3 远程管理（完全凭据）
以下功能均需启动子窗口，展示目标主机信息并提供操作按钮，背后通过 PsExec 执行命令或调用系统工具。

#### 5.3.1 设备管理器功能
- 收集设备列表：`devcon find /all` 或 PowerShell `Get-PnpDevice | ...`；由于 `devcon` 不是系统自带，需通过 PsExec 将 devcon.exe 推送至目标主机或直接执行：
`psexec \\<host> -u <user> -p <pass> -accepteula powershell "Get-PnpDevice | Select-Object Status,Class,FriendlyName,InstanceId | ConvertTo-Csv"`
- 操作：卸载、禁用、启用、更新驱动。可通过 `devcon` 命令或 PowerShell `Disable-PnpDevice`、`Uninstall-PnpDriver` 实现。
- 驱动更新触发：启动旧版设备管理器或调用 Windows Update 扫描（实际复杂，可简化为调出目标主机的 `devmgmt.msc` 界面让本地用户操作？但需求要求与本机设备管理器一样的功能，尽量通过命令行模拟。可先实现禁用/启用/卸载）。

#### 5.3.2 服务管理器功能
- 获取服务列表：
`psexec \\<host> sc query type= service state= all` 或使用 PsService.exe（PsTools 组件）：
`PsService.exe \\<host> -u <user> -p <pass> query`
- 操作：启动、停止、暂停、重启、属性（查看服务配置）。对应 PsService 命令：`start/stop/pause/restart/config`。
- 在子窗口中展示服务名、显示名、状态，右键或按钮操作。

#### 5.3.3 打印机管理
- 获取打印机列表：PowerShell 命令
`Get-Printer | ConvertTo-Csv` 或 `cscript C:\Windows\System32\Printing_Admin_Scripts\zh-CN\prnmngr.vbs -l` （但 VBS 可能被禁用）。使用 PowerShell 更佳。
- 属性：`Get-Printer <name> | Format-List`，端口：`Get-PrinterPort`。
- 添加打印机：`Add-Printer -ConnectionName \\printserver\printer`（需知道路径，子界面提供输入）。
- 删除打印机：`Remove-Printer -Name <name>`。
- 设置默认打印机：`Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows" -Name "Device" ...` 或使用 `(Get-WmiObject ...).SetDefaultPrinter()`，需在用户会话下执行较复杂；若凭据管理员，可通过修改注册表并重启后台服务，或使用 `RUNDLL32 PRINTUI.DLL,PrintUIEntry`。最简单用 PsExec 带 `-i` 交互式启动 `control printers` 让目标用户手动操作，但不符合“与本机一致的功能”的自动化需求。我们采用 `PRINTUI.DLL` 命令，如：
`rundll32 printui.dll,PrintUIEntry /y /n "printer name"` （设置默认打印机）。
PsExec 执行该命令需指定会话 ID（获取控制台会话 `quser` 的第一行）。因此功能实现需先获取活动用户会话 ID。

#### 5.3.4 软件管理
- **获取已安装软件**：
- 扫描远程主机 64 位和 32 位注册表 Uninstall 键：
  - `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall`
  - `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall`
- 可使用 PsExec 调用 `reg query <key> /s` 并解析 DisplayName、UninstallString 等。
- 更稳健的方法是使用 PowerShell：
Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall* | Select-Object DisplayName, UninstallString
- **静默卸载**：对于有 `UninstallString` 的条目，添加 `/?` 或 `/quiet` 参数通过 PsExec 启动卸载程序（凭据管理员权限）。部分卸载程序需要额外参数，可提供自定义。
- **手动卸载**：使用 `-i` 参数通过 PsExec 在目标主机的活动会话中以管理员权限启动卸载程序（如 `MsiExec.exe /X{ProductCode}` 或 `UninstallString` 中的程序），由目标主机用户手动完成。
- **深度清理**：勾选后额外扫描注册表中的 Product GUID（`HKLM\Software\Microsoft\Windows\CurrentVersion\Installer\UserData\...` 等）以及 `HKLM\Software\Classes\Installer\Products`，并提供一键删除勾选的注册表键（危险操作，需确认）。

#### 5.3.5 环境变量编辑
- 获取系统/用户环境变量：
- 系统变量：`reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment"`
- 用户变量：`reg query "HKEY_USERS\<SID>\Environment"` 需获取目标主机用户 SID，较复杂。可简化为：PsExec 调用 PowerShell：
[System.Environment]::GetEnvironmentVariables('Machine')
[System.Environment]::GetEnvironmentVariables('User')
- 子界面展示所有变量（表格），并提供增删改。编辑后，通过 PowerShell 设置：
`[Environment]::SetEnvironmentVariable(<name>,<value>,[System.EnvironmentVariableTarget]::Machine)` 目标机器生效需重启资源管理器或重新登录，但变量值已更改。

#### 5.3.6 系统信息查询
- 执行 `systeminfo` 命令，通过 PsExec 获取完整输出并展示在子窗口。

### 5.4 发送交互程序（在目标主机以管理员权限启动）
各按钮功能本质是通过 PsExec 带 `-d -i <sessionID>` 启动相应程序，参数示例：
- 命令行终端：`cmd.exe` 或 `powershell.exe`
- 计算机管理：`compmgmt.msc`
- 打印机管理：`printmanagement.msc`
- 注册表编辑器：`regedit.exe`
- 环境变量编辑：`rundll32 shell32.dll,Control_RunDLL sysdm.cpl,,3` 或直接启动 `SystemPropertiesAdvanced.exe`。可使用 `rundll32.exe sysdm.cpl,EditEnvironmentVariables`（但该方式针对当前用户）。准确方式：启动 `systempropertiesadvanced.exe` 并切到环境变量页。

获取会话 ID 的方法：通过 PsExec 执行 `quser console` 或 PowerShell `(Get-Process -Name explorer -IncludeUserName | Select-Object -First 1).SessionId`。

### 5.5 网络工具
#### 5.5.1 刷新目标主机 DNS
- 执行 `psexec \\<host> ipconfig /flushdns`

#### 5.5.2 查看活动端口与进程管理
- 获取所有活动连接：`netstat -ano` 解析出 PID、本地/远程地址、状态。
- 支持查找（按程序名或端口），程序名通过 tasklist 或 PowerShell 获取：
`Get-Process -Id <PID> | Select-Object Name, Path`
- 结束进程树：PsKill（PsTools 组件）`PsKill -t \\<host> -u <user> -p <pass> <PID>` 或直接 `taskkill /pid <PID> /t /f`（通过 PsExec）。

### 5.6 命令执行终端
- **终端输入区**：多行文本框，支持键入任意命令。
- **回车或点击“执行”**：将命令通过 PsExec 执行，输出实时追加到上方日志区。
- **加载脚本**：通过按钮选择本地 .bat/.ps1 文件，将文件内容中的命令展示在输入区（如果是 .ps1，可包裹 `powershell -File <file>` 但文件需拷贝到目标机，可先通过 PsExec 创建临时文件再执行）。简化：将脚本内容作为命令发送（如 `powershell -Command "..."`）。
- **拖拽脚本文件**：拖拽到右侧任意区域，自动将文件路径或内容加载到终端输入区，不立即执行。提示信息显示在日志顶部。

### 5.7 高级设置
- 路径配置：PsTools 目录（可浏览本地路径或自动检测）、DameWare.exe 路径、域公共目录路径。
- 提供一个简单的文件选择按钮，修改后保存到设置文件。

---

## 6. UI 布局设计
- **整体窗口**：左侧导航/控件面板（固定宽度 ~280px），右侧主内容区域（自适应）。
- **左侧区域**（垂直滚动）：
- **连接管理**板块：目标主机输入 + 3 个按钮（Ping、持续 Ping、DameWare连接），凭据管理（按钮打开凭据窗口），清除凭据、断开连接。
- **文件与磁盘**板块：6 个按钮，每个触发对应功能（可能需要弹出子窗口）。
- **远程管理**板块：6 个按钮，每个打开子窗口。
- **发送交互程序**板块：5 个按钮。
- **网络工具**板块：2 个按钮。
- **高级设置**按钮。

- **右侧区域**：
- 顶部信息条（目标主机名，连接状态指示灯，C/D 盘空闲/总空间，当前时间）。
- 中间实时日志区域（`RichTextBox` 或只读多行文本框）：
- 日志分级：Info / Warn / Error，可通过下拉框筛选。
- 提供“清空日志”按钮。
- 支持复制、滚动自动跟随。
- 底部终端输入区 + 两个按钮（加载脚本、执行）：
- 拖拽脚本文件到右侧任意位置均会触发加载脚本操作。

- **字体**：全局使用“更纱黑体 Mono”字体，需通过 XAML 资源或代码设置 `FontFamily="Sarasa Mono SC"` 或直接引用字体文件（嵌入资源或系统安装）。

---

## 7. 运行流程与安全性
- **凭据使用**：任何需要远程鉴权的操作均从选中的凭据中获取用户名密码，传递给 PsExec 或 DameWare。确保在内存中尽快清除密码字符串，使用 `SecureString` 并在合适时机释放。
- **PsExec 安全**：自动接受 EULA（仅在首次运行时通过注册表设置或执行一次 `-accepteula`）；所有连接使用加密的网络认证（默认）。
- **日志记录**：所有命令执行均记录在日志区，但不记录明文密码（对密码参数脱敏处理）。
- **错误处理**：PsExec 返回非零退出码时，将标准错误输出捕获并显示为 Error 日志。

---

## 8. 开发约定
- 使用 .NET 10（主力框架 WPF；若使用 WinUI，需调整 XAML 资源引用）。
- 依赖管理：通过 Process 类调用 PsExec.exe 等工具，解析输出。
- 多线程：所有远程操作用 `async/await` 包装 `Task.Run` 避免阻塞 UI。
- 子窗口统一弹窗并只允许单实例。

---

## 9. 关键注意事项
- **域环境**：凭据格式需符合 AD 规范。为目标 B 账号需具有目标主机管理员权限。
- **AAD 混合**：支持 UPN 格式，实际调用时会自动由 Windows 认证处理。
- **PsExec 必需**：确保 PsExec 程序集位于配置的路径中，否则在首次运行时引导下载。
- **远程桌面/会话**：启动交互程序时必须正确指定 `-i` 会话 ID，否则界面不可见。
- **防病毒/防火墙**：PsExec 所用端口（445）和 RPC 需要开放；在企业环境中注意合规。

---
