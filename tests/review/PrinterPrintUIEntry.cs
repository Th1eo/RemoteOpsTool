// ============================================================================
// 远程管理 - 打印机管理：添加打印机向导 & 打印机属性 实现代码（含注释）
//
// 核心原理：调用微软原生 printui.dll 的 PrintUIEntry 接口，
// 在本机弹出目标主机的打印机管理界面，无需在目标主机运行任何代理程序。
// 复刻 printmanagement.msc 连接远程打印服务器的行为。
//
// 来源文件：src/RemoteOpsTool/ViewModels/Dialogs/PrinterManagerViewModel.cs
// ============================================================================

using System.Diagnostics;
using RemoteOpsTool.Helpers;

#region === 打印机属性（OpenPrinterProperties）===

// 调用方式：用户在打印机列表中右键 → "打印机属性"
// 目标：打开指定打印机的原生 Windows 打印机属性对话框
//
// 技术路线：
//   本地：rundll32 printui.dll,PrintUIEntry /p /n "打印机名"
//   远程：rundll32 printui.dll,PrintUIEntry /p /n "\\远程主机\打印机名"
//         + ProcessStartInfo 携带远程凭据（UserName / Domain / Password）
//
// PrintUIEntry 参考：https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/rundll32-printui
//   /p   = 显示打印机属性
//   /n   = 指定打印机名称（远程时使用 UNC 格式 \\host\printer）
//   /c   = 指定计算机名称（此处未使用，通过打印机 UNC 名称中的主机部分隐式定位远程机器）

private void OpenPrinterProperties()
{
    // 获取用户勾选的打印机（取第一个）
    var checkedItems = Printers.Where(p => p.IsChecked).ToList();
    if (checkedItems.Count == 0) return;
    var printer = checkedItems[0];
    var printerName = printer.Printer.Name;
    var host = _main.GetTargetHost();

    // 判断目标是否是本机
    // 空字符串、本机名、"localhost"、"127.0.0.1"、"." 均视为本机
    var isLocal = string.IsNullOrEmpty(host)
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        || host is "localhost" or "127.0.0.1" or ".";

    // 远程时构造 UNC 打印机路径：\\主机名\打印机名
    // 本机时直接使用打印机名称即可
    var targetName = isLocal ? printerName : $"\\\\{host}\\{printerName}";

    if (isLocal)
    {
        // 本机：直接调用 rundll32，UseShellExecute=true 以当前用户身份运行
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"printui.dll,PrintUIEntry /p /n \"{targetName}\"",
            UseShellExecute = true
        });
    }
    else
    {
        // 远程：通过 ProcessStartInfo 携带目标机器的凭据运行 rundll32
        // 注意：UseShellExecute 必须为 false 才能传递 UserName/Domain/Password
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var (runAsUser, runAsDomain) = ProcessHelper.SplitUserDomain(cred.UserName);
        var psi = new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"printui.dll,PrintUIEntry /p /n \"{targetName}\"",
            UseShellExecute = false,       // 必须为 false，否则无法传递身份凭据
            UserName = runAsUser,          // 远程用户名
            Domain = runAsDomain,          // 远程域（可为空）
            LoadUserProfile = true         // 加载用户注册表配置单元
        };
        if (!string.IsNullOrEmpty(password))
            psi.Password = ToSecureString(password);
        Process.Start(psi);                // 对话框显示在本机
    }
}

#endregion

#region === 添加打印机向导（OpenAddPrinterWizard）===

// 调用方式：用户点击"添加向导"按钮
// 目标：打开原生 Windows 添加打印机向导，在目标主机上添加打印机
//
// 技术路线：
//   本地：printui.exe /il
//   远程：printui.exe /il /c \\远程主机
//         + ProcessStartInfo 携带远程凭据
//
// PrintUIEntry 参考：
//   /il  = 启动添加打印机向导（Install printer wizard）
//   /c   = 指定目标计算机，将向导指向该机器的打印服务
//          Windows 会通过 RPC/DCOM 连接目标主机的 Spooler 服务
//
// 注意：
//   - /il 不支持 /n 参数预填打印机名，向导从发现阶段开始
//   - 这里使用 printui.exe 而非 rundll32 printui.dll,PrintUIEntry，
//     printui.exe 是 printui.dll 的独立包装，语义等价但更可靠
//   - 与 printmanagement.msc 连接远程服务器的行为完全一致

private void OpenAddPrinterWizard()
{
    var host = _main.GetTargetHost();

    var isLocal = string.IsNullOrEmpty(host)
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        || host is "localhost" or "127.0.0.1" or ".";

    if (isLocal)
    {
        // 本机：直接启动添加打印机向导
        Process.Start(new ProcessStartInfo
        {
            FileName = "printui.exe",
            Arguments = "/il",
            UseShellExecute = true
        });
    }
    else
    {
        // 远程：通过 /c \\hostname 将向导指向远程机器的 Spooler 服务
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var (runAsUser, runAsDomain) = ProcessHelper.SplitUserDomain(cred.UserName);
        var psi = new ProcessStartInfo
        {
            FileName = "printui.exe",
            Arguments = $"/il /c \\\\{host}",
            UseShellExecute = false,
            UserName = runAsUser,
            Domain = runAsDomain,
            LoadUserProfile = true
        };
        if (!string.IsNullOrEmpty(password))
            psi.Password = ToSecureString(password);
        Process.Start(psi);                // 向导对话框显示在本机
    }
}

#endregion

#region === 辅助方法 ===

// 将明文密码转换为 System.Security.SecureString
// ProcessStartInfo.Password 要求 SecureString 类型
private static SecureString ToSecureString(string pwd)
{
    var ss = new SecureString();
    foreach (var c in pwd) ss.AppendChar(c);
    return ss;
}

#endregion
