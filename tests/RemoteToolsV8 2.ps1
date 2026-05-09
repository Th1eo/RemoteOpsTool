# 计算机远程管理工具 - 编译脚本（v8.2 编译修复与环境管理版）
# 1. [外边距对称] 窗体宽度精调至 1172px，补偿边框占用，实现左右两侧外边距绝对 10px 完美居中。
# 2. [底边齐平] 底层重绘公式精确同步，无论常态还是最大化，左右底部齐平 0 误差。
# 3. [异常细分] 深度拦截错误码，通过 ICMP 存活探测区分“目标电脑不在线”与“未发现X盘分区”。
# 4. [环境管理] 新增“环境变量”管理模块，支持远程无感查看/修改/删除系统变量及当前登录用户的用户变量。
# 5. [Bug修复] 还原了 v8.1 整合时意外丢失的 LoadCredential、ManageRemotePrograms 等函数，彻底修复 CS0103 编译报错。
# 6. [版本迭代] 遵循递增与格式双重规则，系统主版本号更新为 v8.2。
# 7. [机制优化] 修复了远程状态下环境管理模块无法识别当前登录用户的 Bug，改用 explorer 进程属主探测。
# 8. [网络优化] 路由追踪功能强制解析并使用 IPv4 地址，并将最大追踪跃点数提升至 30 以显示完整路径。
# 9. [环境补全] 打开工具时自动检测 PsExec64 环境，若无则从微软官方静默下载。
# 10.[下载修复] 修复 TLS 握手 send 报错，跳过证书验证，并将下载目录修改为软件同目录的 RemoteToolsEnv 文件夹。
# 11.[注册表树] 新增“注册表值”管理功能，内置可视化 TreeView 树状结构，支持逐级展开浏览远程注册表，左树右值。
# 12.[界面重构] 修复修改/新增注册表时的控件遮挡Bug，去除多余内边距与网格线，实现原生级无缝布局。
# 13.[完善体验] 软件管理/环境变量支持最大化，注册表地址栏支持手动输入与回车自动导航。
# 14.[右键管理] 注册表左侧树状列表新增原生级“新建项”、“删除项”、“重命名项”的右键菜单及深度递归操作。
# 15.[权限修复] 直接调用 GetOwnerSid 获取进程属主 SID，彻底解决 AzureAD/微软账户 造成的“获取SID失败”。
# 16.[UI纯净版] 彻底同步 SID 获取机制至环境变量模块，移除隐藏列，使用纯净两列布局，加入动态拉伸。
# 17.[UI优化] 优化了软件管理界面的最大化体验，限制了“程序名称”宽度，彻底消除了右侧多余的空白假列。
# 18.[打印机管理] 快捷功能区新增第三行“Printer管理”，支持增删改查远程打印机，支持脚本拖放与双击原生属性面板。
# 19.[系统信息] 快捷功能区新增“Computer信息”，支持一键读取与展示远程电脑硬件、网络与操作系统的全维度数据。
# 20.[布局微调] 将“Computer信息”移动至“本机信息”右侧，并精准修复了快捷工具底部多余的空白间距，恢复完美贴合。
# 21.[显卡信息] 新增显卡WMI过滤查询，精准拦截DameWare/扩展坞等虚拟显卡。
# 22.[资产导出] “Computer信息”模块新增“复制选中”与“导出列表”功能，支持右键快捷复制与CSV资产导出。
# 23.[对齐修复] 修正了窗体和日志区多余的48px拉伸，确保左右底框在723px水平线绝对 0 误差对齐。
# 24.[色彩重排] 重新设计了快捷工具区的按钮颜色矩阵，确保任何上下左右相邻的按钮绝不撞色。
# 25.[环境修复] 修复传入 UserName 和 Password 执行 PsExec 时因为底层工作目录权限不足导致的“The directory name is invalid”异常。

param(
    [switch]$NoIcon,
    [switch]$Debug,
    [string]$OutputName = "RemoteTools.exe"
)

# ==================== 配置区域 ====================
$config = @{
    OutputExe = $OutputName
    TargetFramework = "v4.0.30319"
    Optimization = $true
    WarningLevel = 4
}

# ==================== 辅助函数 ====================
function Write-ColorOutput {
    param([string]$Message, [string]$Type = "Info")
    $colors = @{ Success="Green"; Error="Red"; Warning="Yellow"; Info="Cyan"; Debug="Gray" }
    $prefix = @{ Success="✅"; Error="❌"; Warning="⚠️"; Info="📌"; Debug="🔍" }[$Type]
    Write-Host "$prefix $Message" -ForegroundColor $colors[$Type]
}

function Test-Compiler {
    $compilerPaths = @(
        "$env:windir\Microsoft.NET\Framework64\$($config.TargetFramework)\csc.exe",
        "$env:windir\Microsoft.NET\Framework\$($config.TargetFramework)\csc.exe"
    )
    foreach ($path in $compilerPaths) {
        if (Test-Path $path) { Write-ColorOutput "找到编译器: $path" "Success"; return $path }
    }
    Write-ColorOutput "未找到 C# 编译器，请安装 .NET Framework SDK" "Error"
    return $null
}

function Find-PsExec {
    Write-ColorOutput "正在从系统环境变量中查找 PsExec64.exe..." "Info"
    $envPaths = $env:PATH -split [System.IO.Path]::PathSeparator | Where-Object { $_.Trim() -ne "" }
    foreach ($path in $envPaths) {
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        $fullPath = Join-Path $path "PsExec64.exe"
        if (Test-Path $fullPath) { Write-ColorOutput "找到 PsExec64: $fullPath (来自环境变量)" "Success"; return $fullPath }
        $fullPath32 = Join-Path $path "PsExec.exe"
        if (Test-Path $fullPath32) { Write-ColorOutput "找到 PsExec: $fullPath32 (来自环境变量)" "Success"; return $fullPath32 }
    }
    $commonPaths = @("$env:ProgramFiles\PSTools\PsExec64.exe", "$env:ProgramFiles(x86)\PSTools\PsExec64.exe", ".\PsExec64.exe")
    foreach ($path in $commonPaths) {
        if (Test-Path $path) { Write-ColorOutput "找到 PsExec: $path (常见位置)" "Success"; return $path }
    }
    Write-ColorOutput "未找到 PsExec64.exe，运行后将尝试自动下载..." "Warning"
    return ""
}

# ==================== 主程序 ====================
function Start-Compilation {
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "   计算机远程管理工具 - 编译脚本 v8.2" -ForegroundColor Cyan
    Write-Host "========================================`n" -ForegroundColor Cyan
    
    $csc = Test-Compiler
    if (-not $csc) { exit 1 }
    
    $tempIconPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "classic_winforms_app.ico")
    $hasIcon = $false
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dummyForm = New-Object System.Windows.Forms.Form
        $fs = New-Object System.IO.FileStream($tempIconPath, [System.IO.FileMode]::Create)
        $dummyForm.Icon.Save($fs)
        $fs.Close()
        $dummyForm.Dispose()
        $config.IconPath = $tempIconPath
        $hasIcon = $true
        Write-ColorOutput "已成功提取系统原生态图标并准备注入" "Success"
    } catch {
        Write-ColorOutput "提取图标失败，将使用默认空图标" "Warning"
    }
    
    $psExecPath = Find-PsExec
    Write-ColorOutput "正在生成底层 C# 代码..." "Info"
    
    $csharpCode = @"
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteDiskAccess
{
    #region 现代主题定义
    public static class ModernTheme
    {
        public static Color Primary = Color.FromArgb(0, 120, 215);     
        public static Color PrimaryLight = Color.FromArgb(60, 150, 230);
        public static Color Success = Color.FromArgb(39, 174, 96);     
        public static Color Warning = Color.FromArgb(243, 156, 18);    
        public static Color Danger = Color.FromArgb(220, 53, 69);      
        public static Color Info = Color.FromArgb(23, 162, 184);       
        public static Color Purple = Color.FromArgb(111, 66, 193);     
        public static Color Slate = Color.FromArgb(85, 95, 105);       
        
        public static Color BgPrimary = Color.FromArgb(30, 30, 30);    
        public static Color BgSecondary = Color.FromArgb(42, 42, 45);  
        public static Color BgCard = Color.FromArgb(38, 38, 40);       
        public static Color BgHover = Color.FromArgb(60, 60, 64);
        
        public static Color TextPrimary = Color.FromArgb(240, 240, 240);
        public static Color TextSecondary = Color.FromArgb(200, 200, 200);
        public static Color TextMuted = Color.FromArgb(140, 140, 140);
        public static Color Border = Color.FromArgb(62, 62, 66);       
        
        public static int CornerRadius = 8;       
        public static int CornerRadiusSmall = 4;  

        public static GraphicsPath GetRoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(rect); return path; }
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
    #endregion

    #region 自定义控件
    public class ModernButton : Button
    {
        private Color _normalColor;
        private Color _hoverColor;
        private Color _pressColor;
        private int _cornerRadius = ModernTheme.CornerRadiusSmall;
        private bool _isHovered = false;
        private bool _isPressed = false;
        
        public Color ButtonColor 
        { 
            get { return _normalColor; }
            set 
            { 
                _normalColor = value;
                _hoverColor = ControlPaint.Light(value, 0.15f);
                _pressColor = ControlPaint.Dark(value, 0.1f);
                Invalidate();
            }
        }
        
        public int CornerRadius 
        { 
            get { return _cornerRadius; }
            set { _cornerRadius = value; Invalidate(); }
        }
        
        public ModernButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | 
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ButtonColor = ModernTheme.Primary;
            ForeColor = ModernTheme.TextPrimary;
        }
        
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; 
            
            g.Clear(Parent != null ? Parent.BackColor : ModernTheme.BgCard);
            
            Color bgColor = _normalColor;
            if (_isPressed) bgColor = _pressColor;
            else if (_isHovered) bgColor = _hoverColor;
            
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = ModernTheme.GetRoundedPath(rect, _cornerRadius))
            using (SolidBrush brush = new SolidBrush(bgColor))
            { 
                g.FillPath(brush, path); 
            }
            
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), ForeColor, 
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
        }
        
        protected override void OnMouseEnter(EventArgs e) { _isHovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _isHovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _isPressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _isPressed = false; Invalidate(); base.OnMouseUp(e); }
    }
    
    public class ModernTextBox : UserControl
    {
        private TextBox _textBox;
        private Button _clearButton;
        private Panel _container;
        private Color _borderColor = ModernTheme.Border;
        private Color _borderColorFocused = ModernTheme.PrimaryLight;
        private bool _isFocused = false;
        
        public override string Text { get { return _textBox.Text; } set { _textBox.Text = value; } }
        public char PasswordChar { get { return _textBox.PasswordChar; } set { _textBox.PasswordChar = value; } }
        public new event EventHandler TextChanged;
        
        public ModernTextBox() { InitializeComponents(); }
        
        private void InitializeComponents()
        {
            Size = new Size(250, 36);
            _container = new Panel { Dock = DockStyle.Fill, BackColor = ModernTheme.BgSecondary, Padding = new Padding(8, 0, 0, 0) };
            _textBox = new TextBox { Location = new Point(10, (Height - 24) / 2), Size = new Size(Width - 45, 24), 
                BorderStyle = BorderStyle.None, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, Font = new Font("Segoe UI", 12f) };
            _clearButton = new Button { Text = "✕", Font = new Font("Segoe UI", 9f, FontStyle.Bold), FlatStyle = FlatStyle.Flat, 
                BackColor = Color.Transparent, ForeColor = ModernTheme.TextMuted, Size = new Size(24, 24), 
                Location = new Point(Width - 30, (Height - 24) / 2), Cursor = Cursors.Hand, Visible = false };
            _clearButton.FlatAppearance.BorderSize = 0;
            _clearButton.MouseEnter += (s, e) => _clearButton.ForeColor = ModernTheme.Danger;
            _clearButton.MouseLeave += (s, e) => _clearButton.ForeColor = ModernTheme.TextMuted;
            _clearButton.Click += (s, e) => { _textBox.Clear(); _clearButton.Visible = false; if (TextChanged != null) TextChanged(this, EventArgs.Empty); };
            _textBox.TextChanged += (s, e) => { _clearButton.Visible = !string.IsNullOrEmpty(_textBox.Text); if (TextChanged != null) TextChanged(this, EventArgs.Empty); };
            _textBox.Enter += (s, e) => { _isFocused = true; Invalidate(); };
            _textBox.Leave += (s, e) => { _isFocused = false; Invalidate(); };
            _container.Controls.Add(_textBox);
            _container.Controls.Add(_clearButton);
            Controls.Add(_container);
            _container.Paint += Container_Paint;
            _container.Resize += (s, e) => { _textBox.Size = new Size(_container.Width - 45, 24); 
                _textBox.Location = new Point(10, (_container.Height - 24) / 2); 
                _clearButton.Location = new Point(_container.Width - 30, (_container.Height - 24) / 2); };
        }
        
        private void Container_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color borderColor = _isFocused ? _borderColorFocused : _borderColor;
            Rectangle rect = new Rectangle(0, 0, _container.Width - 1, _container.Height - 1);
            using (GraphicsPath path = ModernTheme.GetRoundedPath(rect, ModernTheme.CornerRadiusSmall))
            using (Pen pen = new Pen(borderColor, 1)) 
            { 
                g.DrawPath(pen, path); 
            }
        }
    }
    
    public class ModernCard : Panel
    {
        private string _title = "";
        public string CardTitle { get { return _title; } set { _title = value; Invalidate(); } }
        
        public ModernCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | 
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = ModernTheme.BgCard;
            Padding = new Padding(15, 45, 15, 15);
        }
        
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : ModernTheme.BgPrimary);

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = ModernTheme.GetRoundedPath(rect, ModernTheme.CornerRadius))
            {
                using (SolidBrush brush = new SolidBrush(BackColor)) { g.FillPath(brush, path); }
                using (Pen pen = new Pen(ModernTheme.Border, 1)) { g.DrawPath(pen, path); }
            }
            
            if (!string.IsNullOrEmpty(_title))
            {
                using (SolidBrush brush = new SolidBrush(ModernTheme.TextPrimary))
                using (Font font = new Font("Segoe UI", 11.5f, FontStyle.Bold))
                { g.DrawString(_title, font, brush, 15, 12); }
                using (Pen pen = new Pen(ModernTheme.Border, 1)) { g.DrawLine(pen, 15, 40, Width - 15, 40); }
            }
        }
    }
    
    public class ModernConsole : RichTextBox
    {
        public ModernConsole()
        {
            BackColor = Color.FromArgb(16, 16, 16); 
            ForeColor = ModernTheme.Success;
            Font = new Font("Consolas", 9.5f);
            ReadOnly = true;
            WordWrap = true;
            BorderStyle = BorderStyle.None;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }
        
        public void AppendColoredText(string text, Color color)
        {
            if (InvokeRequired) { Invoke(new Action(() => AppendColoredText(text, color))); return; }
            SelectionStart = TextLength;
            SelectionLength = 0;
            SelectionColor = color;
            AppendText(text + Environment.NewLine);
            ScrollToCaret();
        }
    }
    
    public class ModernProgressBar : ProgressBar
    {
        private Color _progressColor = ModernTheme.Primary;
        public Color ProgressColor { get { return _progressColor; } set { _progressColor = value; Invalidate(); } }
        
        public ModernProgressBar() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : ModernTheme.BgSecondary);

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = ModernTheme.GetRoundedPath(rect, ModernTheme.CornerRadiusSmall))
            {
                using (SolidBrush brush = new SolidBrush(ModernTheme.BgSecondary)) { g.FillPath(brush, path); }
            }
            
            if (Value > 0)
            {
                int progressWidth = (int)((Width - 1) * ((double)Value / Maximum));
                if (progressWidth > 0)
                {
                    Rectangle progRect = new Rectangle(0, 0, progressWidth, Height - 1);
                    using (GraphicsPath progPath = ModernTheme.GetRoundedPath(progRect, ModernTheme.CornerRadiusSmall))
                    using (SolidBrush brush = new SolidBrush(_progressColor)) 
                    { 
                        g.FillPath(brush, progPath); 
                    }
                }
            }
        }
    }
    #endregion

    public class MainWindow : Form
    {
        #region 解决无边框窗体任务栏点击最小化问题
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_MINIMIZEBOX = 0x00020000;
                CreateParams cp = base.CreateParams;
                cp.Style |= WS_MINIMIZEBOX;
                return cp;
            }
        }
        #endregion

        #region Win32 API
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class NETRESOURCE { public int dwScope = 0; public int dwType = 1; public int dwDisplayType = 0; public int dwUsage = 0; 
            public string lpLocalName = null; public string lpRemoteName = null; public string lpComment = null; public string lpProvider = null; }

        [DllImport("mpr.dll", CharSet = CharSet.Auto)] private static extern int WNetAddConnection2(NETRESOURCE netResource, string password, string userName, int flags);
        [DllImport("mpr.dll", CharSet = CharSet.Auto)] private static extern int WNetCancelConnection2(string name, int flags, bool force);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] 
        private static extern bool CopyFileEx(string lpExistingFileName, string lpNewFileName, CopyProgressRoutine lpProgressRoutine, 
            IntPtr lpData, ref bool pbCancel, int dwCopyFlags);
        private delegate CopyProgressResult CopyProgressRoutine(long TotalFileSize, long TotalBytesTransferred, long StreamSize, 
            long StreamBytesTransferred, uint dwStreamNumber, CopyProgressCallbackReason dwCallbackReason, IntPtr hSourceFile, 
            IntPtr hDestinationFile, IntPtr lpData);
        private enum CopyProgressResult : uint { PROGRESS_CONTINUE = 0, PROGRESS_CANCEL = 1, PROGRESS_STOP = 2, PROGRESS_QUIET = 3 }
        private enum CopyProgressCallbackReason : uint { CALLBACK_CHUNK_FINISHED = 0x00000000, CALLBACK_STREAM_SWITCH = 0x00000001 }
        #endregion

        #region 字段
        private ModernTextBox _txtTarget;
        private ModernConsole _resultBox;
        private ModernButton _btnC, _btnD, _btnDesktop, _btnDiskSpace, _btnChangeCred, _btnDisconnectAll;
        private ModernButton _btnContinuousPing; 
        private ModernButton _btnPsExecCmd, _btnPsExecProgram, _btnPsExecCompmgmt, _btnPsExecPrint;
        private ModernButton _btnClear;
        private ModernButton _btnDWRCC;
        private ModernButton _btnUninstall;
        private Label _lblPsExecInfo;
        private readonly string _credFile;
        private readonly List<string> _connectedShares = new List<string>();
        private bool _continuePing;
        private bool _isConnecting;
        private System.Windows.Forms.Timer _timer;
        private Label _statusLabel;
        private string _psExecPath = "";
        private NetworkCredential _currentCred;
        private string _currentPassword = "";
        private readonly object _lockObj = new object();
        private readonly Dictionary<int, string> _errorMessages;
        private Label _timeLabel;
        private Label _versionLabel;
        private byte[] _entropy;
        private Panel _titleBar;
        private bool _isDragging = false;
        private Point _dragStart;
        private ModernProgressBar _uploadProgress;
        private Label _lblProgressPercent;
        private ModernButton _btnCancelUpload;
        private bool _cancelUpload = false;
        private int _currentFileIndex = 0;
        private int _totalFiles = 0;
        private string _currentUploadingFile = "";
        
        private bool _isFakeMaximized = false;
        private Rectangle _normalBounds;
        #endregion

        public MainWindow()
        {
            _credFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteToolCred.dat");
            _entropy = Encoding.UTF8.GetBytes("RemoteDiskTool_SALT_2024");
            _errorMessages = new Dictionary<int, string> { {86, "密码错误"}, {1326, "用户名或密码错误"}, {53, "找不到网络路径"}, 
                {58, "指定的服务器无法执行请求的操作"}, {67, "网络名不存在"}, {1219, "已用其他用户名连接"}, {1385, "没有管理员权限"} };
            LoadCredential();
            FindPsExecFromPath();
            
            try { 
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); 
            } catch { }
            
            InitializeComponents();
            
            // 确保窗口完全显示、句柄创建完毕后，再触发异步下载，解决跨线程时序崩溃问题
            this.Shown += (s, e) => CheckAndDownloadPsExecAsync();
            
            _txtTarget.Text = ""; 
            
            this.FormClosing += MainWindow_FormClosing;
        }

        #region 本地化解析与错误拦截核心引擎
        private string GetTargetComputer()
        {
            string text = _txtTarget.Text.Trim();
            if (string.IsNullOrEmpty(text)) text = "%COMPUTERNAME%";
            return Environment.ExpandEnvironmentVariables(text);
        }

        private bool IsLocalMachine(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            string t = target.Trim().ToLower();
            if (t == "localhost" || t == "127.0.0.1" || t == "::1" || t == ".") return true;
            if (t == Environment.MachineName.ToLower()) return true;
            if (t == "%computername%".ToLower()) return true;
            
            try {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList) {
                    if (ip.ToString() == t) return true;
                }
            } catch { }
            
            return false;
        }

        private bool IsComputerOnline(string host)
        {
            if (IsLocalMachine(host)) return true;
            try {
                using (Ping ping = new Ping()) {
                    PingReply reply = ping.Send(host, 1000);
                    return reply.Status == IPStatus.Success;
                }
            } catch { return false; }
        }

        private string GetConnectionErrorStr(int errorCode, string pc, string targetPath)
        {
            if (errorCode == 53 || errorCode == 58 || errorCode == 67 || errorCode == 1203 || errorCode == 121)
            {
                if (!IsComputerOnline(pc))
                {
                    return "✗ 连接失败: 目标电脑已离线";
                }
                
                if (!string.IsNullOrEmpty(targetPath))
                {
                    int dollarIdx = targetPath.IndexOf('$');
                    if (dollarIdx >= 1)
                    {
                        char drive = targetPath[dollarIdx - 1];
                        if (char.IsLetter(drive))
                        {
                            return "✗ 连接失败: 未发现" + char.ToUpper(drive) + "盘分区";
                        }
                    }
                }
            }
            
            string msg = _errorMessages.ContainsKey(errorCode) ? _errorMessages[errorCode] : "错误码 " + errorCode;
            return "✗ 连接失败: " + msg;
        }
        #endregion

        #region WMI 通用与本地化引擎
        private ManagementScope GetWmiScope(string computerName, NetworkCredential cred, string wmiNamespace) {
            if (IsLocalMachine(computerName)) {
                return new ManagementScope(string.Format(@"\\.\{0}", wmiNamespace));
            } else {
                ConnectionOptions options = new ConnectionOptions();
                options.Username = cred.UserName;
                options.Password = cred.Password;
                if (!string.IsNullOrEmpty(cred.Domain)) { options.Authority = "ntlmdomain:" + cred.Domain; }
                options.Authentication = AuthenticationLevel.PacketPrivacy;
                options.Impersonation = ImpersonationLevel.Impersonate;
                return new ManagementScope(string.Format(@"\\{0}\{1}", computerName, wmiNamespace), options);
            }
        }

        private string GetCurrentUserSid(string computerName, NetworkCredential cred) {
            try {
                ManagementScope cimv2Scope = GetWmiScope(computerName, cred, @"root\cimv2");
                cimv2Scope.Connect();
                
                using (var searcher = new ManagementObjectSearcher(cimv2Scope, new ObjectQuery("SELECT * FROM Win32_Process WHERE Name='explorer.exe'"))) {
                    foreach (ManagementObject m in searcher.Get()) {
                        try {
                            ManagementBaseObject sidOut = m.InvokeMethod("GetOwnerSid", null, null);
                            if (sidOut != null && sidOut["Sid"] != null) {
                                return sidOut["Sid"].ToString();
                            }
                        } catch {}
                    }
                }

                string user = "";
                string domain = "";

                string loggedInUser = "";
                using (var searcher = new ManagementObjectSearcher(cimv2Scope, new ObjectQuery("SELECT UserName FROM Win32_ComputerSystem"))) {
                    foreach (ManagementObject m in searcher.Get()) {
                        if (m["UserName"] != null) {
                            loggedInUser = m["UserName"].ToString();
                            break;
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(loggedInUser)) {
                    string[] parts = loggedInUser.Split('\\');
                    if(parts.Length == 2) {
                        domain = parts[0];
                        user = parts[1];
                    } else {
                        user = loggedInUser;
                    }
                }

                if (!string.IsNullOrEmpty(user)) {
                    string query = string.IsNullOrEmpty(domain) ? 
                        string.Format("SELECT SID FROM Win32_UserAccount WHERE Name='{0}'", user) : 
                        string.Format("SELECT SID FROM Win32_UserAccount WHERE Domain='{0}' AND Name='{1}'", domain, user);
                    using (var searcher = new ManagementObjectSearcher(cimv2Scope, new ObjectQuery(query))) {
                        foreach (ManagementObject m in searcher.Get()) {
                            if (m["SID"] != null) return m["SID"].ToString();
                        }
                    }
                }
            } catch { }
            return null;
        }

        private void SetLabelSafe(Label lbl, string text) {
            if(lbl.InvokeRequired) lbl.Invoke((Action)(() => lbl.Text = text));
            else lbl.Text = text;
        }

        private Dictionary<string, string> ReadRegValues(ManagementClass regClass, uint hDefKey, string sSubKeyName) {
            Dictionary<string, string> dict = new Dictionary<string, string>();
            ManagementBaseObject inParams = regClass.GetMethodParameters("EnumValues");
            inParams["hDefKey"] = hDefKey;
            inParams["sSubKeyName"] = sSubKeyName;
            ManagementBaseObject outParams = regClass.InvokeMethod("EnumValues", inParams, null);
            
            if(outParams["sNames"] != null) {
                string[] names = outParams["sNames"] as string[];
                int[] types = outParams["Types"] as int[];
                for(int i=0; i<names.Length; i++) {
                    string val = "";
                    if(types[i] == 1 || types[i] == 2) { 
                        string method = types[i] == 2 ? "GetExpandedStringValue" : "GetStringValue";
                        ManagementBaseObject inP2 = regClass.GetMethodParameters(method);
                        inP2["hDefKey"] = hDefKey;
                        inP2["sSubKeyName"] = sSubKeyName;
                        inP2["sValueName"] = names[i];
                        ManagementBaseObject outP2 = regClass.InvokeMethod(method, inP2, null);
                        if (outP2["sValue"] != null) val = outP2["sValue"].ToString();
                    }
                    dict[names[i]] = val;
                }
            }
            return dict;
        }
        #endregion

        #region 加密解密方法
        private string EncryptString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            try { byte[] plainBytes = Encoding.UTF8.GetBytes(plainText); 
                byte[] encryptedBytes = ProtectedData.Protect(plainBytes, _entropy, DataProtectionScope.CurrentUser); 
                return Convert.ToBase64String(encryptedBytes); }
            catch { return ""; }
        }

        private string DecryptString(string encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText)) return "";
            try { byte[] encryptedBytes = Convert.FromBase64String(encryptedText); 
                byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, _entropy, DataProtectionScope.CurrentUser); 
                return Encoding.UTF8.GetString(plainBytes); }
            catch { return ""; }
        }

        private void SaveCredential(string username, string password) 
        { 
            try { string data = EncryptString(username) + "|" + EncryptString(password); File.WriteAllText(_credFile, data); } 
            catch (Exception ex) { AppendResult("保存凭证失败: " + ex.Message, ModernTheme.Danger); } 
        }

        private bool LoadCredentialFromFile(out string username, out string password)
        {
            username = null; password = null;
            try { if (File.Exists(_credFile)) { string[] parts = File.ReadAllText(_credFile).Split('|'); 
                if (parts.Length == 2) { username = DecryptString(parts[0]); password = DecryptString(parts[1]); return true; } } }
            catch { }
            return false;
        }
        
        private void LoadCredential() { 
            string username, password; 
            if (LoadCredentialFromFile(out username, out password) && !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)) { 
                string domain = "", user = username; 
                if (user.Contains("\\")) { string[] parts = user.Split('\\'); domain = parts[0]; user = parts[1]; } 
                _currentCred = new NetworkCredential(user, password, domain); _currentPassword = password; 
            } 
        }

        private void FindPsExecFromPath()
        {
            string localAppDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RemoteToolsEnv", "PsExec64.exe");
            if (File.Exists(localAppDataPath)) { _psExecPath = localAppDataPath; return; }

            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string path in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    string fullPath = Path.Combine(path.Trim(), "PsExec64.exe");
                    if (File.Exists(fullPath)) { _psExecPath = fullPath; break; }
                    fullPath = Path.Combine(path.Trim(), "PsExec.exe");
                    if (File.Exists(fullPath)) { _psExecPath = fullPath; break; }
                }
            }
            if (string.IsNullOrEmpty(_psExecPath))
            {
                string[] commonPaths = { 
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PSTools", "PsExec64.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PSTools", "PsExec64.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PSTools", "PsExec.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PSTools", "PsExec.exe"),
                    Path.Combine(Environment.SystemDirectory, "PsExec64.exe"), Path.Combine(Environment.SystemDirectory, "PsExec.exe"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PsExec64.exe"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PsExec.exe") };
                foreach (string path in commonPaths) { if (File.Exists(path)) { _psExecPath = path; break; } }
            }
        }

        private void CheckAndDownloadPsExecAsync()
        {
            if (!string.IsNullOrEmpty(_psExecPath) && File.Exists(_psExecPath)) return;
            
            Task.Run(() => {
                try {
                    Action uiStart = () => {
                        if (this.IsDisposed || !this.IsHandleCreated) return;
                        if (_lblPsExecInfo != null) {
                            _lblPsExecInfo.Text = "模式: ProcessStartInfo 凭证运行 | 状态: 🔄 正在下载 PsExec64...";
                            _lblPsExecInfo.ForeColor = ModernTheme.Warning;
                        }
                        SetStatus("正在下载 PsExec64...", true);
                    };
                    if (this.IsHandleCreated && !this.IsDisposed) this.Invoke(uiStart);
                    
                    // 修复底层握手错误与跳过证书验证
                    ServicePointManager.Expect100Continue = false;
                    ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | (SecurityProtocolType)192 | (SecurityProtocolType)48;
                    
                    // 修改路径为软件同目录的 RemoteToolsEnv 文件夹
                    string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RemoteToolsEnv");
                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                    string targetFile = Path.Combine(targetDir, "PsExec64.exe");
                    
                    using (WebClient wc = new WebClient()) {
                        wc.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                        wc.DownloadFile("https://live.sysinternals.com/PsExec64.exe", targetFile);
                    }
                    
                    if (File.Exists(targetFile)) {
                        _psExecPath = targetFile;
                        Action uiSuccess = () => {
                            if (this.IsDisposed || !this.IsHandleCreated) return;
                            if (_lblPsExecInfo != null) {
                                _lblPsExecInfo.Text = "模式: ProcessStartInfo 凭证运行 | 状态: ✓ 环境已就绪";
                                _lblPsExecInfo.ForeColor = ModernTheme.Success;
                            }
                            if (_btnPsExecCmd != null) _btnPsExecCmd.Enabled = true;
                            if (_btnPsExecProgram != null) _btnPsExecProgram.Enabled = true;
                            if (_btnPsExecCompmgmt != null) _btnPsExecCompmgmt.Enabled = true;
                            if (_btnPsExecPrint != null) _btnPsExecPrint.Enabled = true;
                            SetStatus("PsExec64 下载完成", false);
                        };
                        if (this.IsHandleCreated && !this.IsDisposed) this.Invoke(uiSuccess);
                    }
                } catch (Exception ex) {
                    Action uiFail = () => {
                        if (this.IsDisposed || !this.IsHandleCreated) return;
                        if (_lblPsExecInfo != null) {
                            _lblPsExecInfo.Text = "模式: ProcessStartInfo 凭证运行 | 状态: ✗ 自动下载失败";
                            _lblPsExecInfo.ForeColor = ModernTheme.Danger;
                        }
                        SetStatus("PsExec64 下载失败", false);
                        AppendResult("PsExec64 下载失败: " + ex.Message, ModernTheme.Danger);
                    };
                    if (this.IsHandleCreated && !this.IsDisposed) this.Invoke(uiFail);
                }
            });
        }
        #endregion

        private void InitializeComponents()
        {
            this.Text = "计算机远程管理工具";
            this.Size = new Size(1172, 818);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.None;
            this.BackColor = ModernTheme.BgPrimary;
            this.Padding = new Padding(1);
            
            this.MaximizedBounds = Screen.PrimaryScreen.WorkingArea;
            this.Paint += (s, e) => { using (Pen pen = new Pen(ModernTheme.Border, 1)) { e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); } };
            CreateTitleBar();
            
            Panel contentPanel = new Panel { Dock = DockStyle.Fill, BackColor = ModernTheme.BgPrimary, Padding = new Padding(0), AutoScroll = false };
            this.Controls.Add(contentPanel);
            
            CreateStatusBar(contentPanel);
            contentPanel.BringToFront(); 
            
            CreateUI(contentPanel);
            
            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += UpdateTimeDisplay;
            _timer.Start();
        }

        private void CreateTitleBar()
        {
            _titleBar = new Panel { Dock = DockStyle.Top, Height = 45, BackColor = ModernTheme.BgSecondary };
            
            _titleBar.MouseDown += (s, e) => { 
                if (e.Button == MouseButtons.Left) { 
                    if (_isFakeMaximized) {
                        _isFakeMaximized = false;
                        this.Bounds = _normalBounds;
                        _dragStart = new Point(e.X, e.Y);
                    } else {
                        _dragStart = e.Location; 
                    }
                    _isDragging = true; 
                } 
            };
            _titleBar.MouseMove += (s, e) => { if (_isDragging) { Point p = PointToScreen(e.Location); Location = new Point(p.X - _dragStart.X, p.Y - _dragStart.Y); } };
            _titleBar.MouseUp += (s, e) => _isDragging = false;
            
            PictureBox icon = new PictureBox 
            { 
                Size = new Size(24, 24), Location = new Point(15, 10), BackColor = Color.Transparent, SizeMode = PictureBoxSizeMode.Zoom
            };
            try { 
                icon.Image = this.Icon.ToBitmap(); 
            } catch { }
            _titleBar.Controls.Add(icon);
            
            Panel iconSeparator = new Panel { Location = new Point(45, 8), Size = new Size(1, 29), BackColor = ModernTheme.Border };
            _titleBar.Controls.Add(iconSeparator);
            
            Label titleLabel = new Label 
            { 
                Text = "计算机远程管理工具", Font = new Font("Segoe UI", 10f, FontStyle.Regular), 
                ForeColor = ModernTheme.TextPrimary, Location = new Point(55, 11), AutoSize = true 
            };
            _titleBar.Controls.Add(titleLabel);
            
            _versionLabel = new Label 
            { 
                Text = "v8.2", Font = new Font("Segoe UI", 9f), ForeColor = ModernTheme.TextMuted, 
                Location = new Point(190, 14), AutoSize = true 
            };
            _titleBar.Controls.Add(_versionLabel);
            
            Label btnMin = new Label 
            { 
                Text = "─", Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = ModernTheme.TextSecondary, 
                Location = new Point(this.Width - 135, 0), Size = new Size(45, 45), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand 
            };
            btnMin.Click += (s, e) => this.WindowState = FormWindowState.Minimized;
            btnMin.MouseEnter += (s, e) => { btnMin.BackColor = ModernTheme.BgHover; btnMin.ForeColor = ModernTheme.TextPrimary; };
            btnMin.MouseLeave += (s, e) => { btnMin.BackColor = Color.Transparent; btnMin.ForeColor = ModernTheme.TextSecondary; };
            _titleBar.Controls.Add(btnMin);
            
            Label btnMax = new Label 
            { 
                Text = "□", Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = ModernTheme.TextSecondary, 
                Location = new Point(this.Width - 90, 0), Size = new Size(45, 45), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand 
            };
            btnMax.Click += (s, e) => {
                if (!_isFakeMaximized) {
                    _normalBounds = this.Bounds;
                    Screen currentScreen = Screen.FromHandle(this.Handle);
                    this.Bounds = currentScreen.WorkingArea;
                    _isFakeMaximized = true;
                    btnMax.Text = "❐";
                } else {
                    this.Bounds = _normalBounds;
                    _isFakeMaximized = false;
                    btnMax.Text = "□";
                }
            };
            btnMax.MouseEnter += (s, e) => { btnMax.BackColor = ModernTheme.BgHover; btnMax.ForeColor = ModernTheme.TextPrimary; };
            btnMax.MouseLeave += (s, e) => { btnMax.BackColor = Color.Transparent; btnMax.ForeColor = ModernTheme.TextSecondary; };
            _titleBar.Controls.Add(btnMax);
            
            Label btnCloseWin = new Label 
            { 
                Text = "✕", Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = ModernTheme.TextSecondary, 
                Location = new Point(this.Width - 45, 0), Size = new Size(45, 45), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand 
            };
            btnCloseWin.Click += (s, e) => Close();
            btnCloseWin.MouseEnter += (s, e) => { btnCloseWin.BackColor = ModernTheme.Danger; btnCloseWin.ForeColor = Color.White; };
            btnCloseWin.MouseLeave += (s, e) => { btnCloseWin.BackColor = Color.Transparent; btnCloseWin.ForeColor = ModernTheme.TextSecondary; };
            _titleBar.Controls.Add(btnCloseWin);
            
            this.Resize += (s, e) => { 
                btnMin.Location = new Point(this.Width - 135, 0); 
                btnMax.Location = new Point(this.Width - 90, 0); 
                btnCloseWin.Location = new Point(this.Width - 45, 0); 
                if (_isFakeMaximized) { btnMax.Text = "❐"; } else { btnMax.Text = "□"; }
                _titleBar.Invalidate(); 
            };
            this.Controls.Add(_titleBar);
        }

        private void CreateUI(Panel parent)
        {
            ModernCard targetCard = new ModernCard { CardTitle = "目标地址", Location = new Point(10, 15), Size = new Size(560, 105) };
            Label lblTarget = new Label { Text = "IP地址 / 计算机名", Font = new Font("Segoe UI", 10.5f), ForeColor = ModernTheme.TextSecondary, Location = new Point(15, 62), AutoSize = true };
            _txtTarget = new ModernTextBox { Location = new Point(150, 55), Size = new Size(195, 36) }; 
            _btnContinuousPing = new ModernButton { Text = "Ping", ButtonColor = ModernTheme.Primary, Location = new Point(355, 55), Size = new Size(80, 36) }; 
            _btnContinuousPing.Click += ToggleContinuousPing;
            _btnDWRCC = new ModernButton { Text = "DWRCC远程", ButtonColor = ModernTheme.Warning, Location = new Point(445, 55), Size = new Size(100, 36) }; 
            _btnDWRCC.Click += (s, e) => ExecuteAsync(RunDWRCC);
            targetCard.Controls.AddRange(new Control[] { lblTarget, _txtTarget, _btnContinuousPing, _btnDWRCC });
            
            ModernCard diskCard = new ModernCard { CardTitle = "远程磁盘访问 · 支持拖放", Location = new Point(10, 130), Size = new Size(560, 265) };
            int cardY = 55; int btnH = 36; 
            _btnC = new ModernButton { Text = "📁 访问 C 盘", ButtonColor = ModernTheme.Success, Location = new Point(15, cardY), Size = new Size(260, btnH) };
            _btnC.Click += (s, e) => ConnectShareAsync("C$", "C盘");
            _btnD = new ModernButton { Text = "📁 访问 D 盘", ButtonColor = ModernTheme.Success, Location = new Point(285, cardY), Size = new Size(260, btnH) }; 
            _btnD.Click += (s, e) => ConnectShareAsync("D$", "D盘");
            cardY += btnH + 12;
            _btnDesktop = new ModernButton { Text = "🖥️ 快速访问桌面", ButtonColor = ModernTheme.Warning, Location = new Point(15, cardY), Size = new Size(530, btnH) }; 
            _btnDesktop.Click += (s, e) => ConnectShareAsync("c$\\Users\\Public\\Desktop", "桌面");
            cardY += btnH + 12;
            
            _btnDiskSpace = new ModernButton { Text = "📊 查看远程电脑磁盘信息", ButtonColor = ModernTheme.PrimaryLight, Location = new Point(15, cardY), Size = new Size(530, btnH) };
            _btnDiskSpace.Click += (s, e) => ExecuteAsync(CheckRemoteDiskSpace);
            
            cardY += btnH + 15;
            Panel sepLine = new Panel { Location = new Point(15, cardY), Size = new Size(530, 1), BackColor = ModernTheme.Border };
            diskCard.Controls.Add(sepLine);
            cardY += 12;
            _btnChangeCred = new ModernButton { Text = "🗑️ 清除凭证", ButtonColor = ModernTheme.Slate, Location = new Point(15, cardY), Size = new Size(260, btnH) };
            _btnChangeCred.Click += ClearCredentials;
            _btnDisconnectAll = new ModernButton { Text = "🔌 断开所有连接", ButtonColor = ModernTheme.Danger, Location = new Point(285, cardY), Size = new Size(260, btnH) };
            _btnDisconnectAll.Click += (s, e) => DisconnectAll();
            
            diskCard.Controls.AddRange(new Control[] { _btnC, _btnD, _btnDesktop, _btnDiskSpace, _btnChangeCred, _btnDisconnectAll });
            CreateDragDropButton(_btnC, "C$", "C盘");
            CreateDragDropButton(_btnD, "D$", "D盘");
            CreateDragDropButton(_btnDesktop, "c$\\Users\\Public\\Desktop", "桌面");
            
            ModernCard psexecCard = new ModernCard { CardTitle = "PsExec64 远程执行", Location = new Point(10, 405), Size = new Size(560, 105) };
            bool psExecExists = !string.IsNullOrEmpty(_psExecPath);
            
            _lblPsExecInfo = new Label { 
                Text = "模式: ProcessStartInfo 凭证运行 | 状态: " + (psExecExists ? "✓ 环境已就绪" : "✗ 未找到"), 
                Font = new Font("微软雅黑", 9.5f, FontStyle.Regular), 
                ForeColor = psExecExists ? ModernTheme.Success : ModernTheme.Danger, 
                Location = new Point(165, 16), 
                Size = new Size(380, 24),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = Color.Transparent 
            };
            psexecCard.Controls.Add(_lblPsExecInfo);
            
            int pBtnW = 125; int pBtnY = 55;
            _btnPsExecCmd = new ModernButton { Text = "远程CMD", ButtonColor = ModernTheme.Primary, Location = new Point(15, pBtnY), Size = new Size(pBtnW, 36), Enabled = psExecExists };
            _btnPsExecCmd.Click += (s, e) => RunPsExecWithCredential("cmd.exe /c start cmd.exe", false);
            
            _btnPsExecProgram = new ModernButton { Text = "运行程序(可拖放)", ButtonColor = ModernTheme.Success, Location = new Point(150, pBtnY), Size = new Size(pBtnW, 36), Enabled = psExecExists };
            _btnPsExecProgram.Click += (s, e) => RunPsExecProgram();
            SetupDragDropButton(_btnPsExecProgram);
            
            _btnPsExecCompmgmt = new ModernButton { Text = "计算机管理", ButtonColor = ModernTheme.Purple, Location = new Point(285, pBtnY), Size = new Size(pBtnW, 36), Enabled = psExecExists };
            _btnPsExecCompmgmt.Click += (s, e) => RunPsExecWithCredential("cmd.exe /c start \"\" mmc.exe compmgmt.msc", false);
            
            _btnPsExecPrint = new ModernButton { Text = "打印机管理", ButtonColor = ModernTheme.Warning, Location = new Point(420, pBtnY), Size = new Size(pBtnW, 36), Enabled = psExecExists }; 
            _btnPsExecPrint.Click += (s, e) => RunPsExecWithCredential("cmd.exe /c start \"\" mmc.exe printmanagement.msc", false);
            
            psexecCard.Controls.AddRange(new Control[] { _btnPsExecCmd, _btnPsExecProgram, _btnPsExecCompmgmt, _btnPsExecPrint });
            
            ModernCard toolsCard = new ModernCard { CardTitle = "快捷工具", Location = new Point(10, 520), Size = new Size(560, 203) };
            
            // ===== 重新分配颜色矩阵，确保相邻异色 =====
            // 行 1: 青色, 橙色, 蓝色, 绿色
            int tY1 = 55; 
            ModernButton btnInfo = new ModernButton { Text = "本机信息", ButtonColor = ModernTheme.Info, Location = new Point(15, tY1), Size = new Size(pBtnW, 36) };
            btnInfo.Click += (s, e) => ExecuteAsync(ShowLocalInfo);
            
            ModernButton btnCompInfo = new ModernButton { Text = "Computer信息", ButtonColor = ModernTheme.Warning, Location = new Point(150, tY1), Size = new Size(pBtnW, 36) };
            btnCompInfo.Click += (s, e) => ManageComputerInfo();
            
            ModernButton btnDNS = new ModernButton { Text = "DNS查询", ButtonColor = ModernTheme.Primary, Location = new Point(285, tY1), Size = new Size(pBtnW, 36) };
            btnDNS.Click += (s, e) => { string d = Microsoft.VisualBasic.Interaction.InputBox("请输入要查询的域名:", "DNS查询", "www.google.com", -1, -1); if (!string.IsNullOrEmpty(d)) ExecuteAsync(() => QuickDNS(d)); };
            
            ModernButton btnFlush = new ModernButton { Text = "DNS刷新", ButtonColor = ModernTheme.Success, Location = new Point(420, tY1), Size = new Size(pBtnW, 36) };
            btnFlush.Click += (s, e) => ExecuteAsync(QuickFlushDNS);
            
            // 行 2: 灰色, 紫色, 红色, 浅蓝
            int tY2 = 103;
            ModernButton btnPort = new ModernButton { Text = "端口扫描", ButtonColor = ModernTheme.Slate, Location = new Point(15, tY2), Size = new Size(pBtnW, 36) };
            btnPort.Click += (s, e) => { string ip = GetTargetComputer(); if (string.IsNullOrEmpty(ip)) ip = "127.0.0.1"; ExecuteAsync(() => FullPortScan(ip)); };
            
            ModernButton btnRoute = new ModernButton { Text = "路由追踪", ButtonColor = ModernTheme.Purple, Location = new Point(150, tY2), Size = new Size(pBtnW, 36) };
            btnRoute.Click += (s, e) => ExecuteAsync(QuickTracert);
            
            _btnUninstall = new ModernButton { Text = "软件管理", ButtonColor = ModernTheme.Danger, Location = new Point(285, tY2), Size = new Size(pBtnW, 36) };
            _btnUninstall.Click += (s, e) => ManageRemotePrograms();

            ModernButton btnEnvVar = new ModernButton { Text = "环境变量", ButtonColor = ModernTheme.PrimaryLight, Location = new Point(420, tY2), Size = new Size(pBtnW, 36) };
            btnEnvVar.Click += (s, e) => ManageEnvVars();
            
            // 行 3: 绿色, 青色
            int tY3 = 151;
            ModernButton btnReg = new ModernButton { Text = "注册表值", ButtonColor = ModernTheme.Success, Location = new Point(15, tY3), Size = new Size(pBtnW, 36) };
            btnReg.Click += (s, e) => ManageRegistry();

            ModernButton btnPrinter = new ModernButton { Text = "Printer管理", ButtonColor = ModernTheme.Info, Location = new Point(150, tY3), Size = new Size(pBtnW, 36) };
            btnPrinter.Click += (s, e) => ManagePrinters();
            SetupDragDropButton(btnPrinter);
            
            toolsCard.Controls.AddRange(new Control[] { btnInfo, btnCompInfo, btnDNS, btnFlush, btnPort, btnRoute, _btnUninstall, btnEnvVar, btnReg, btnPrinter });
            
            ModernCard logCard = new ModernCard { CardTitle = "执行日志", Location = new Point(580, 15), Size = new Size(parent.Width - 580 - 10, 708), 
                Anchor = AnchorStyles.Top | AnchorStyles.Left };
            
            _btnClear = new ModernButton { Text = "清空", ButtonColor = ModernTheme.Slate, Location = new Point(logCard.Width - 75, 11), 
                Size = new Size(60, 28), Font = new Font("Segoe UI", 9f), 
                Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _btnClear.Click += (s, e) => { _resultBox.Clear(); AppendResult("日志已清空", ModernTheme.TextMuted); };
            
            _resultBox = new ModernConsole { Location = new Point(15, 55), Size = new Size(logCard.Width - 30, logCard.Height - 70), 
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
                
            logCard.Controls.Add(_btnClear);
            logCard.Controls.Add(_resultBox);
            parent.Controls.AddRange(new Control[] { targetCard, diskCard, psexecCard, toolsCard, logCard });
            
            parent.Resize += (s, e) => {
                int targetWidth = parent.Width - 580 - 10;
                if (targetWidth > 100) 
                {
                    logCard.Width = targetWidth;
                }

                if (parent.Height > 400) 
                {
                    int targetHeight = parent.Height - 30;
                    if (targetHeight > 100) 
                    {
                        logCard.Height = targetHeight;
                    }
                }
            };
        }

        #region Computer信息功能
        private void ManageComputerInfo()
        {
            string pc = GetTargetComputer();
            bool isLocal = IsLocalMachine(pc);
            NetworkCredential cred = null;
            if (!isLocal) {
                cred = GetCredForWMI();
                if (cred == null) { AppendResult("✗ 未获取到凭证", ModernTheme.Danger); return; }
            }

            using (Form dialog = new Form())
            {
                dialog.Text = "Computer信息 - " + pc;
                dialog.Size = new Size(850, 650);
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.BackColor = ModernTheme.BgCard;
                dialog.FormBorderStyle = FormBorderStyle.Sizable;
                dialog.MaximizeBox = true;
                dialog.MinimizeBox = true;
                
                try { dialog.Icon = this.Icon; } catch { }

                Panel titleBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = ModernTheme.BgSecondary };
                Label titleLabel = new Label { Text = "Computer信息 - " + pc + (isLocal ? " (本机)" : ""), Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = ModernTheme.TextPrimary, Location = new Point(15, 10), AutoSize = true };
                titleBar.Controls.Add(titleLabel);

                Panel buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 55, BackColor = ModernTheme.BgSecondary };
                ModernButton btnRefresh = new ModernButton { Text = "刷新", ButtonColor = ModernTheme.Info, Location = new Point(10, 10), Size = new Size(100, 35) };
                ModernButton btnCopy = new ModernButton { Text = "复制选中", ButtonColor = ModernTheme.Primary, Location = new Point(120, 10), Size = new Size(100, 35) };
                ModernButton btnExport = new ModernButton { Text = "导出列表", ButtonColor = ModernTheme.Success, Location = new Point(230, 10), Size = new Size(100, 35) };
                ModernButton btnCloseDialog = new ModernButton { Text = "关闭", ButtonColor = Color.FromArgb(71, 85, 105), Location = new Point(730, 10), Size = new Size(100, 35), Anchor = AnchorStyles.Top | AnchorStyles.Right };
                btnCloseDialog.Click += (s, e) => dialog.Close();
                buttonPanel.Controls.AddRange(new Control[] { btnRefresh, btnCopy, btnExport, btnCloseDialog });

                ListView lvInfo = new ListView {
                    Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false,
                    BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, Font = new Font("Segoe UI", 9.5f), BorderStyle = BorderStyle.None
                };
                lvInfo.Columns.Add("分类 (Category)", 150);
                lvInfo.Columns.Add("属性 (Property)", 200);
                lvInfo.Columns.Add("值 (Value)", 450);

                lvInfo.Resize += (s, e) => {
                    if (lvInfo.Columns.Count >= 3) {
                        int w = lvInfo.ClientSize.Width - lvInfo.Columns[0].Width - lvInfo.Columns[1].Width;
                        if (w > 50) lvInfo.Columns[2].Width = w;
                    }
                };

                Label lblStatus = new Label {
                    Dock = DockStyle.Bottom, Height = 25, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextSecondary,
                    Text = "就绪", TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(5, 0, 0, 0)
                };

                dialog.Controls.Add(lvInfo);
                dialog.Controls.Add(buttonPanel);
                dialog.Controls.Add(lblStatus);
                dialog.Controls.Add(titleBar);

                btnCopy.Click += (s, e) => {
                    if (lvInfo.SelectedItems.Count > 0) {
                        StringBuilder sb = new StringBuilder();
                        foreach (ListViewItem item in lvInfo.SelectedItems) {
                            sb.AppendLine(string.Format("{0}: {1}", item.SubItems[1].Text, item.SubItems[2].Text));
                        }
                        Clipboard.SetText(sb.ToString().TrimEnd());
                        MessageBox.Show("已复制到剪贴板", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    } else {
                        MessageBox.Show("请先选择一项", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                };

                btnExport.Click += (s, e) => {
                    using (SaveFileDialog sfd = new SaveFileDialog()) {
                        sfd.Filter = "CSV文件|*.csv|文本文件|*.txt";
                        sfd.FileName = string.Format("ComputerInfo_{0}_{1}.csv", pc, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        if (sfd.ShowDialog() == DialogResult.OK) {
                            try {
                                StringBuilder sb = new StringBuilder();
                                sb.AppendLine("分类,属性,值");
                                foreach (ListViewItem item in lvInfo.Items) {
                                    sb.AppendLine(string.Format("\"{0}\",\"{1}\",\"{2}\"",
                                        item.Text.Replace("\"", "\"\""),
                                        item.SubItems[1].Text.Replace("\"", "\"\""),
                                        item.SubItems[2].Text.Replace("\"", "\"\"")));
                                }
                                File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                                MessageBox.Show(string.Format("已导出 {0} 项数据到文件", lvInfo.Items.Count), "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            } catch (Exception ex) {
                                MessageBox.Show("导出失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }
                };

                ContextMenuStrip ctxMenu = new ContextMenuStrip();
                ctxMenu.BackColor = ModernTheme.BgSecondary;
                ctxMenu.ForeColor = ModernTheme.TextPrimary;
                ToolStripMenuItem miCopy = new ToolStripMenuItem("复制选中");
                miCopy.Click += (s, e) => btnCopy.PerformClick();
                ctxMenu.Items.Add(miCopy);
                lvInfo.ContextMenuStrip = ctxMenu;

                Action loadInfo = () => {
                    try {
                        SetLabelSafe(lblStatus, "正在读取电脑全维度信息...");
                        ManagementScope scope = GetWmiScope(pc, cred, @"root\cimv2");
                        scope.Connect();

                        List<ListViewItem> items = new List<ListViewItem>();
                        Action<string, string, string> add = (cat, prop, val) => {
                            ListViewItem itm = new ListViewItem(cat);
                            itm.SubItems.Add(prop);
                            itm.SubItems.Add(val != null ? val : "");
                            items.Add(itm);
                        };

                        Func<string, string> parseDate = (d) => {
                            if (string.IsNullOrEmpty(d) || d.Length < 14) return d;
                            return d.Substring(0,4) + "/" + d.Substring(4,2) + "/" + d.Substring(6,2) + " " + d.Substring(8,2) + ":" + d.Substring(10,2) + ":" + d.Substring(12,2);
                        };

                        // Win32_OperatingSystem
                        try {
                            using(ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Caption, Version, OSArchitecture, InstallDate, LastBootUpTime, SystemDirectory FROM Win32_OperatingSystem"))) {
                                foreach(ManagementObject m in s.Get()) {
                                    add("Operating System", "O/S Name", m["Caption"] != null ? m["Caption"].ToString() : "");
                                    add("Operating System", "Version", m["Version"] != null ? m["Version"].ToString() : "");
                                    add("Operating System", "Architecture", m["OSArchitecture"] != null ? m["OSArchitecture"].ToString() : "");
                                    add("Operating System", "Installed", parseDate(m["InstallDate"] != null ? m["InstallDate"].ToString() : ""));
                                    add("Operating System", "System Drive", m["SystemDirectory"] != null ? m["SystemDirectory"].ToString().Substring(0, 3) : "");
                                    add("General", "Boot Time", parseDate(m["LastBootUpTime"] != null ? m["LastBootUpTime"].ToString() : ""));
                                }
                            }
                        } catch {}

                        // Win32_ComputerSystem
                        try {
                            using(ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Manufacturer, Model, TotalPhysicalMemory, UserName, Domain, Name FROM Win32_ComputerSystem"))) {
                                foreach(ManagementObject m in s.Get()) {
                                    add("General", "Host Name", m["Name"] != null ? m["Name"].ToString() : "");
                                    add("General", "Current User", m["UserName"] != null ? m["UserName"].ToString() : "");
                                    add("Active Directory", "Domain", m["Domain"] != null ? m["Domain"].ToString() : "");
                                    add("System", "Manufacturer", m["Manufacturer"] != null ? m["Manufacturer"].ToString() : "");
                                    add("System", "Model", m["Model"] != null ? m["Model"].ToString() : "");
                                    if (m["TotalPhysicalMemory"] != null) {
                                        ulong ram = Convert.ToUInt64(m["TotalPhysicalMemory"]);
                                        add("System", "Memory", (ram / 1073741824.0).ToString("0.00") + " GB");
                                    }
                                }
                            }
                        } catch {}

                        // Win32_Processor
                        try {
                            using(ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, NumberOfCores FROM Win32_Processor"))) {
                                foreach(ManagementObject m in s.Get()) {
                                    add("System", "Processor", (m["Name"] != null ? m["Name"].ToString() : "") + " (" + (m["NumberOfCores"] != null ? m["NumberOfCores"].ToString() : "") + " Cores)");
                                }
                            }
                        } catch {}

                        // Win32_BIOS
                        try {
                            using(ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT SMBIOSBIOSVersion, Manufacturer, SerialNumber FROM Win32_BIOS"))) {
                                foreach(ManagementObject m in s.Get()) {
                                    add("System", "BIOS Version", m["SMBIOSBIOSVersion"] != null ? m["SMBIOSBIOSVersion"].ToString() : "");
                                    add("System", "BIOS Manufacturer", m["Manufacturer"] != null ? m["Manufacturer"].ToString() : "");
                                    add("System", "Serial Number", m["SerialNumber"] != null ? m["SerialNumber"].ToString() : "");
                                }
                            }
                        } catch {}

                        // Win32_VideoController (显卡信息)
                        try {
                            using(ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, DriverVersion FROM Win32_VideoController"))) {
                                foreach(ManagementObject m in s.Get()) {
                                    string gpuName = m["Name"] != null ? m["Name"].ToString() : "";
                                    string gpuDriver = m["DriverVersion"] != null ? m["DriverVersion"].ToString() : "";
                                    if (!string.IsNullOrEmpty(gpuName)) {
                                        string lowerName = gpuName.ToLower();
                                        // 拦截 DameWare, Port Replicator, Mirror 等非物理显卡驱动
                                        if (!lowerName.Contains("dameware") && !lowerName.Contains("replicator") && !lowerName.Contains("mirror") && !lowerName.Contains("virtual")) {
                                            add("System", "Graphics Card", gpuName);
                                            add("System", "GPU Driver", gpuDriver);
                                        }
                                    }
                                }
                            }
                        } catch {}

                        // Win32_NetworkAdapterConfiguration
                        try {
                            using(ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT IPAddress, MACAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True"))) {
                                foreach(ManagementObject m in s.Get()) {
                                    string[] ips = m["IPAddress"] as string[];
                                    if (ips != null && ips.Length > 0) {
                                        add("General", "IP Address", string.Join(", ", ips));
                                    }
                                    add("General", "MAC Address", m["MACAddress"] != null ? m["MACAddress"].ToString() : "");
                                }
                            }
                        } catch {}

                        lvInfo.Invoke((Action)(() => {
                            lvInfo.Items.Clear();
                            lvInfo.Items.AddRange(items.ToArray());
                            SetLabelSafe(lblStatus, "读取完成，共查找到 " + items.Count + " 项数据");
                        }));

                    } catch (Exception ex) {
                        SetLabelSafe(lblStatus, "读取失败: " + ex.Message);
                    }
                };

                btnRefresh.Click += (s, e) => Task.Run(loadInfo);
                dialog.Shown += (s, e) => Task.Run(loadInfo);

                dialog.ShowDialog();
            }
        }
        #endregion

        #region 打印机管理功能
        private void ManagePrinters()
        {
            string pc = GetTargetComputer();
            bool isLocal = IsLocalMachine(pc);
            NetworkCredential cred = null;
            if (!isLocal) {
                cred = GetCredForWMI();
                if (cred == null) { AppendResult("✗ 未获取到凭证", ModernTheme.Danger); return; }
            }

            using (Form dialog = new Form())
            {
                dialog.Text = "Printer管理 - " + pc;
                dialog.Size = new Size(950, 650);
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.BackColor = ModernTheme.BgCard;
                dialog.FormBorderStyle = FormBorderStyle.Sizable;
                dialog.MaximizeBox = true;
                dialog.MinimizeBox = true;
                dialog.AllowDrop = true;
                
                try { dialog.Icon = this.Icon; } catch { }

                Panel titleBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = ModernTheme.BgSecondary };
                Label titleLabel = new Label { Text = "Printer管理 - " + pc + (isLocal ? " (本机)" : "") + " [支持拖放脚本安装]", Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = ModernTheme.TextPrimary, Location = new Point(15, 10), AutoSize = true };
                titleBar.Controls.Add(titleLabel);

                Panel buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 55, BackColor = ModernTheme.BgSecondary };
                ModernButton btnRefresh = new ModernButton { Text = "刷新列表", ButtonColor = ModernTheme.Info, Location = new Point(10, 10), Size = new Size(100, 35) };
                ModernButton btnAdd = new ModernButton { Text = "添加网络打印机", ButtonColor = ModernTheme.Success, Location = new Point(120, 10), Size = new Size(130, 35) };
                ModernButton btnEdit = new ModernButton { Text = "编辑属性", ButtonColor = ModernTheme.Primary, Location = new Point(260, 10), Size = new Size(80, 35) };
                ModernButton btnDelete = new ModernButton { Text = "删除", ButtonColor = ModernTheme.Danger, Location = new Point(350, 10), Size = new Size(80, 35) };
                ModernButton btnCloseDialog = new ModernButton { Text = "关闭", ButtonColor = Color.FromArgb(71, 85, 105), Location = new Point(830, 10), Size = new Size(100, 35), Anchor = AnchorStyles.Top | AnchorStyles.Right };
                btnCloseDialog.Click += (s, e) => dialog.Close();
                buttonPanel.Controls.AddRange(new Control[] { btnRefresh, btnAdd, btnEdit, btnDelete, btnCloseDialog });

                ListView lvPrinters = new ListView {
                    Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false,
                    BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, Font = new Font("Segoe UI", 9.5f), AllowDrop = true
                };
                lvPrinters.Columns.Add("名称", 200);
                lvPrinters.Columns.Add("描述", 120);
                lvPrinters.Columns.Add("驱动程序", 180);
                lvPrinters.Columns.Add("备注", 120);
                lvPrinters.Columns.Add("位置", 120);
                lvPrinters.Columns.Add("共享", 60);
                lvPrinters.Columns.Add("驱动版本", 100);

                lvPrinters.Resize += (s, e) => {
                    if (lvPrinters.Columns.Count >= 7) {
                        int w = lvPrinters.ClientSize.Width - 120 - 180 - 120 - 120 - 60 - 100;
                        if (w > 100) lvPrinters.Columns[0].Width = w;
                    }
                };

                Label lblStatus = new Label {
                    Dock = DockStyle.Bottom, Height = 25, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextSecondary,
                    Text = "就绪", TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(5, 0, 0, 0)
                };

                dialog.Controls.Add(lvPrinters);
                dialog.Controls.Add(buttonPanel);
                dialog.Controls.Add(lblStatus);
                dialog.Controls.Add(titleBar);

                DragEventHandler dragEnter = (s, e) => {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
                        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                        if (files != null && files.Length > 0) {
                            string ext = Path.GetExtension(files[0]).ToLower();
                            if (ext == ".exe" || ext == ".bat" || ext == ".cmd" || ext == ".vbs" || ext == ".ps1") {
                                e.Effect = DragDropEffects.Copy; return;
                            }
                        }
                    }
                    e.Effect = DragDropEffects.None;
                };
                DragEventHandler dragDrop = (s, e) => {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0) {
                        string fileName = Path.GetFileName(files[0]);
                        if (MessageBox.Show(string.Format("确定要在电脑 [{0}] 上执行打印机安装脚本 [{1}] 吗？", pc, fileName), "执行脚本", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
                            UploadAndRunRemoteProgramWithCredential(files, fileName);
                        }
                    }
                };
                dialog.DragEnter += dragEnter; dialog.DragDrop += dragDrop;
                lvPrinters.DragEnter += dragEnter; lvPrinters.DragDrop += dragDrop;

                // --- 新增：双击调用原生属性面板 ---
                lvPrinters.MouseDoubleClick += (s, e) => {
                    if (lvPrinters.SelectedItems.Count == 0) return;
                    string pName = lvPrinters.SelectedItems[0].Text;
                    try {
                        SetLabelSafe(lblStatus, "正在调用 Windows 原生打印机属性面板...");
                        string machineArg = isLocal ? "" : " /c\\\\" + pc;
                        ProcessStartInfo psi = new ProcessStartInfo {
                            FileName = "rundll32.exe",
                            Arguments = string.Format("printui.dll,PrintUIEntry /p /n\"{0}\"{1}", pName, machineArg),
                            UseShellExecute = false
                        };
                        Process.Start(psi);
                    } catch (Exception ex) {
                        MessageBox.Show("打开属性面板失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                // ----------------------------------

                Action loadPrinters = () => {
                    try {
                        SetLabelSafe(lblStatus, "正在读取打印机信息...");
                        ManagementScope scope = GetWmiScope(pc, cred, @"root\cimv2");
                        scope.Connect();

                        Dictionary<string, string> drvVers = new Dictionary<string, string>();
                        try {
                            ObjectQuery dq = new ObjectQuery("SELECT Name, Version FROM Win32_PrinterDriver");
                            using(ManagementObjectSearcher ds = new ManagementObjectSearcher(scope, dq)) {
                                foreach(ManagementObject d in ds.Get()) {
                                    string n = d["Name"] != null ? d["Name"].ToString() : "";
                                    if(n.Contains(",")) n = n.Split(',')[0];
                                    string v = d["Version"] != null ? d["Version"].ToString() : "";
                                    drvVers[n] = v;
                                }
                            }
                        } catch { }

                        ObjectQuery q = new ObjectQuery("SELECT Name, Description, DriverName, Comment, Location, Shared FROM Win32_Printer");
                        using(ManagementObjectSearcher s = new ManagementObjectSearcher(scope, q)) {
                            List<ListViewItem> items = new List<ListViewItem>();
                            foreach(ManagementObject m in s.Get()) {
                                string name = m["Name"] != null ? m["Name"].ToString() : "";
                                string desc = m["Description"] != null ? m["Description"].ToString() : "";
                                string driver = m["DriverName"] != null ? m["DriverName"].ToString() : "";
                                string comment = m["Comment"] != null ? m["Comment"].ToString() : "";
                                string location = m["Location"] != null ? m["Location"].ToString() : "";
                                bool shared = m["Shared"] != null && (bool)m["Shared"];
                                string ver = drvVers.ContainsKey(driver) ? drvVers[driver] : "";

                                ListViewItem itm = new ListViewItem(name);
                                itm.SubItems.Add(desc);
                                itm.SubItems.Add(driver);
                                itm.SubItems.Add(comment);
                                itm.SubItems.Add(location);
                                itm.SubItems.Add(shared ? "Yes" : "");
                                itm.SubItems.Add(ver);
                                items.Add(itm);
                            }
                            lvPrinters.Invoke((Action)(() => {
                                lvPrinters.Items.Clear();
                                lvPrinters.Items.AddRange(items.OrderBy(x => x.Text).ToArray());
                                SetLabelSafe(lblStatus, string.Format("读取完成，共找到 {0} 个打印机", items.Count));
                            }));
                        }
                    } catch (Exception ex) {
                        SetLabelSafe(lblStatus, "读取失败: " + ex.Message);
                    }
                };

                btnRefresh.Click += (s, e) => Task.Run(loadPrinters);
                dialog.Shown += (s, e) => Task.Run(loadPrinters);

                btnDelete.Click += (s, e) => {
                    if (lvPrinters.SelectedItems.Count == 0) { MessageBox.Show("请先选择要删除的打印机"); return; }
                    string pName = lvPrinters.SelectedItems[0].Text;
                    if (MessageBox.Show("确定要删除打印机 [" + pName + "] 吗？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
                        Task.Run(() => {
                            try {
                                SetLabelSafe(lblStatus, "正在删除...");
                                ManagementScope scope = GetWmiScope(pc, cred, @"root\cimv2");
                                scope.Connect();
                                ObjectQuery q = new ObjectQuery("SELECT * FROM Win32_Printer WHERE Name='" + pName.Replace("'", "\\'") + "'");
                                using(ManagementObjectSearcher ms = new ManagementObjectSearcher(scope, q)) {
                                    foreach(ManagementObject m in ms.Get()) { m.Delete(); }
                                }
                                SetLabelSafe(lblStatus, "删除成功");
                                loadPrinters();
                            } catch (Exception ex) {
                                SetLabelSafe(lblStatus, "删除失败: " + ex.Message);
                            }
                        });
                    }
                };

                btnEdit.Click += (s, e) => {
                    if (lvPrinters.SelectedItems.Count == 0) { MessageBox.Show("请先选择要编辑的打印机"); return; }
                    string pName = lvPrinters.SelectedItems[0].Text;
                    string pComment = lvPrinters.SelectedItems[0].SubItems[3].Text;
                    string pLocation = lvPrinters.SelectedItems[0].SubItems[4].Text;

                    using (Form fEdit = new Form { Text = "编辑打印机", Size = new Size(400, 230), StartPosition = FormStartPosition.CenterParent, BackColor = ModernTheme.BgCard, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false }) {
                        Label l1 = new Label { Text = "备注:", ForeColor = ModernTheme.TextPrimary, Location = new Point(20, 20), AutoSize = true };
                        TextBox t1 = new TextBox { Location = new Point(20, 45), Size = new Size(340, 25), Text = pComment, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.FixedSingle };

                        Label l2 = new Label { Text = "位置:", ForeColor = ModernTheme.TextPrimary, Location = new Point(20, 85), AutoSize = true };
                        TextBox t2 = new TextBox { Location = new Point(20, 110), Size = new Size(340, 25), Text = pLocation, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.FixedSingle };

                        Button bOk = new Button { Text = "确定", Location = new Point(190, 150), Size = new Size(80, 30), BackColor = ModernTheme.Primary, ForeColor = ModernTheme.TextPrimary, FlatStyle = FlatStyle.Flat };
                        Button bCancel = new Button { Text = "取消", Location = new Point(280, 150), Size = new Size(80, 30), BackColor = Color.FromArgb(71, 85, 105), ForeColor = ModernTheme.TextPrimary, FlatStyle = FlatStyle.Flat };

                        bCancel.Click += (ss, ee) => fEdit.DialogResult = DialogResult.Cancel;
                        bOk.Click += (ss, ee) => {
                            string nc = t1.Text.Trim(); string nl = t2.Text.Trim();
                            Task.Run(() => {
                                try {
                                    SetLabelSafe(lblStatus, "正在更新...");
                                    ManagementScope scope = GetWmiScope(pc, cred, @"root\cimv2");
                                    scope.Connect();
                                    ObjectQuery q = new ObjectQuery("SELECT * FROM Win32_Printer WHERE Name='" + pName.Replace("'", "\\'") + "'");
                                    using(ManagementObjectSearcher ms = new ManagementObjectSearcher(scope, q)) {
                                        foreach(ManagementObject m in ms.Get()) {
                                            m["Comment"] = nc; m["Location"] = nl; m.Put();
                                        }
                                    }
                                    SetLabelSafe(lblStatus, "更新成功");
                                    loadPrinters();
                                } catch (Exception ex) { SetLabelSafe(lblStatus, "更新失败: " + ex.Message); }
                            });
                            fEdit.DialogResult = DialogResult.OK;
                        };
                        fEdit.Controls.AddRange(new Control[] { l1, t1, l2, t2, bOk, bCancel });
                        fEdit.ShowDialog();
                    }
                };

                btnAdd.Click += (s, e) => {
                    string path = Microsoft.VisualBasic.Interaction.InputBox("请输入共享打印机路径 (如 \\\\Server\\Printer):\n\n【注】复杂本地打印机请直接拖放安装脚本到本窗口！", "添加网络打印机", "\\\\", -1, -1);
                    if(string.IsNullOrEmpty(path) || path == "\\\\") return;

                    Task.Run(() => {
                        try {
                            SetLabelSafe(lblStatus, "正在下发安装指令...");
                            string cmd = string.Format("rundll32 printui.dll,PrintUIEntry /ga /n\"{0}\"", path);

                            int sessionId = GetActiveSessionId(pc);
                            string domain = !string.IsNullOrEmpty(cred.Domain) ? cred.Domain : ".";
                            string fullUser = domain + "\\" + cred.UserName;
                            string psexecArgs = string.Format(@"\\{0} -u ""{1}"" -p ""{2}"" -i {3} -h -s -accepteula cmd.exe /c {4}", pc, fullUser, _currentPassword, sessionId, cmd);

                            ProcessStartInfo psi = new ProcessStartInfo {
                                FileName = _psExecPath, Arguments = psexecArgs,
                                WorkingDirectory = Environment.SystemDirectory,
                                UseShellExecute = false, CreateNoWindow = true,
                                UserName = cred.UserName, Password = GetSecureString(_currentPassword), Domain = cred.Domain
                            };
                            using (Process p = Process.Start(psi)) {
                                p.WaitForExit();
                                if(p.ExitCode == 0) SetLabelSafe(lblStatus, "添加指令执行成功，部分打印机需重启生效或等待加载。");
                                else SetLabelSafe(lblStatus, "添加失败，退出代码: " + p.ExitCode);
                            }
                        } catch (Exception ex) { SetLabelSafe(lblStatus, "添加失败: " + ex.Message); }
                    });
                };

                dialog.ShowDialog();
            }
        }
        #endregion

        private void SetupDragDropButton(ModernButton btn)
        {
            btn.AllowDrop = true;
            
            btn.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        string ext = Path.GetExtension(files[0]).ToLower();
                        if (ext == ".exe" || ext == ".bat" || ext == ".cmd" || ext == ".vbs" || ext == ".ps1")
                        {
                            e.Effect = DragDropEffects.Copy;
                            btn.ButtonColor = ControlPaint.Light(ModernTheme.Success, 0.3f);
                        }
                        else
                        {
                            e.Effect = DragDropEffects.None;
                        }
                    }
                }
            };
            
            btn.DragLeave += (s, e) =>
            {
                btn.ButtonColor = ModernTheme.Success;
            };
            
            btn.DragDrop += (s, e) =>
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    string pc = GetTargetComputer();
                    
                    string file = files[0];
                    string ext = Path.GetExtension(file).ToLower();
                    if (ext == ".exe" || ext == ".bat" || ext == ".cmd" || ext == ".vbs" || ext == ".ps1")
                    {
                        string fileName = Path.GetFileName(file);
                        DialogResult result = MessageBox.Show(
                            string.Format("确定要在电脑 [{0}] 上以管理员身份运行以下程序吗？\n\n{1}\n\n程序将被复制到 C:\\Temp 目录并执行。", pc, fileName),
                            "确认远程运行", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        
                        if (result == DialogResult.Yes)
                        {
                            UploadAndRunRemoteProgramWithCredential(files, fileName);
                        }
                    }
                    else
                    {
                        AppendResult("✗ 仅支持 .exe, .bat, .cmd, .vbs, .ps1 文件", ModernTheme.Warning);
                    }
                }
                btn.ButtonColor = ModernTheme.Success;
            };
        }

        private void CreateStatusBar(Panel parent)
        {
            Panel statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 35, BackColor = ModernTheme.BgSecondary };
            _statusLabel = new Label { Text = "● 就绪", Font = new Font("Segoe UI", 9f), ForeColor = ModernTheme.Success, Location = new Point(15, 8), Size = new Size(150, 22) };
            _uploadProgress = new ModernProgressBar { Location = new Point(170, 6), Size = new Size(200, 22), Style = ProgressBarStyle.Continuous, 
                Minimum = 0, Maximum = 100, Visible = false, ProgressColor = ModernTheme.Primary };
            _lblProgressPercent = new Label { Text = "", Font = new Font("Segoe UI", 8f), ForeColor = ModernTheme.TextSecondary, 
                Location = new Point(375, 8), Size = new Size(50, 22), Visible = false };
            _btnCancelUpload = new ModernButton { Text = "取消", ButtonColor = ModernTheme.Danger, Location = new Point(430, 5), 
                Size = new Size(60, 24), Font = new Font("Segoe UI", 8f), Visible = false };
            _btnCancelUpload.Click += (s, e) => { _cancelUpload = true; AppendResult("⚠ 正在取消上传...", ModernTheme.Warning); _btnCancelUpload.Enabled = false; };
            
            _timeLabel = new Label { 
                Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), 
                Font = new Font("Segoe UI", 9f), 
                ForeColor = ModernTheme.TextSecondary, 
                Size = new Size(160, 22), 
                Location = new Point(statusPanel.Width - 170, 8), 
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            statusPanel.Controls.Add(_statusLabel);
            statusPanel.Controls.Add(_uploadProgress);
            statusPanel.Controls.Add(_lblProgressPercent);
            statusPanel.Controls.Add(_btnCancelUpload);
            statusPanel.Controls.Add(_timeLabel);

            parent.Parent.Controls.Add(statusPanel);
        }

        private void CreateDragDropButton(ModernButton btn, string share, string targetName)
        {
            btn.AllowDrop = true;
            
            btn.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effect = DragDropEffects.Copy;
                    btn.ButtonColor = ControlPaint.Light(btn.ButtonColor, 0.3f);
                }
            };
            
            btn.DragLeave += (s, e) =>
            {
                btn.ButtonColor = ModernTheme.Primary; // 恢复原色
            };
            
            btn.DragDrop += (s, e) =>
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    string pc = GetTargetComputer();
                    
                    string file = files[0];
                    string ext = Path.GetExtension(file).ToLower();
                    
                    if (ext == ".exe" || ext == ".bat" || ext == ".cmd" || ext == ".vbs" || ext == ".ps1")
                    {
                        string fileName = Path.GetFileName(file);
                        DialogResult result = MessageBox.Show(
                            string.Format("确定要在电脑 [{0}] 上以管理员身份运行以下程序吗？\n\n{1}\n\n程序将被复制到 C:\\Temp 目录并执行。", pc, fileName),
                            "确认远程运行", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        
                        if (result == DialogResult.Yes)
                        {
                            UploadAndRunRemoteProgramWithCredential(files, fileName);
                        }
                    }
                    else
                    {
                        string remotePath = @"\\" + pc + @"\" + share;
                        UploadFilesToRemote(files, share, targetName);
                    }
                }
            };
        }

        private void ShowProgress(bool show, string fileName) { if (_uploadProgress.InvokeRequired) { _uploadProgress.Invoke(new Action(() => ShowProgress(show, fileName))); return; } 
            _uploadProgress.Visible = show; _lblProgressPercent.Visible = show; _btnCancelUpload.Visible = show; 
            if (show) { _statusLabel.Text = "📤 上传中: " + fileName; _statusLabel.ForeColor = ModernTheme.Warning; _btnCancelUpload.Enabled = true; } 
            else { _statusLabel.Text = "● 就绪"; _statusLabel.ForeColor = ModernTheme.Success; _uploadProgress.Value = 0; _lblProgressPercent.Text = ""; } }
        
        private void UpdateProgress(int percent, string fileName, int current, int total) { if (_uploadProgress.InvokeRequired) { _uploadProgress.Invoke(new Action(() => UpdateProgress(percent, fileName, current, total))); return; } 
            _uploadProgress.Value = Math.Min(100, Math.Max(0, percent)); _lblProgressPercent.Text = percent + "%"; _statusLabel.Text = string.Format("📤 上传中: {0} ({1}/{2})", fileName, current, total); }
        
        private CopyProgressResult CopyProgressHandler(long TotalFileSize, long TotalBytesTransferred, long StreamSize, long StreamBytesTransferred, 
            uint dwStreamNumber, CopyProgressCallbackReason dwCallbackReason, IntPtr hSourceFile, IntPtr hDestinationFile, IntPtr lpData) 
        { if (_cancelUpload) return CopyProgressResult.PROGRESS_CANCEL; int percent = (int)((double)TotalBytesTransferred / TotalFileSize * 100); 
            UpdateProgress(percent, _currentUploadingFile, _currentFileIndex, _totalFiles); return CopyProgressResult.PROGRESS_CONTINUE; }

        private void UploadFilesToRemote(string[] files, string shareName, string targetName)
        {
            Task.Run(() =>
            {
                try
                {
                    _cancelUpload = false; _currentFileIndex = 0; _totalFiles = files.Length;
                    
                    string pc = GetTargetComputer();
                    bool isLocal = IsLocalMachine(pc);
                    string remotePath = @"\\" + pc + @"\" + shareName;
                    string actualDestDir = remotePath;
                    
                    if (isLocal) {
                        actualDestDir = shareName.Replace("$", ":\\");
                        if (shareName.ToLower().Contains("desktop")) actualDestDir = @"C:\Users\Public\Desktop";
                        if (remotePath.EndsWith(@"C$\Temp", StringComparison.OrdinalIgnoreCase)) actualDestDir = @"C:\Temp";
                    }

                    ShowProgress(true, "准备连接...");
                    AppendResult("", ModernTheme.TextPrimary);
                    AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                    AppendResult("开始上传文件到 " + targetName, ModernTheme.Info);
                    AppendResult("目标路径: " + actualDestDir, ModernTheme.TextSecondary);
                    AppendResult("文件总数: " + _totalFiles, ModernTheme.TextSecondary);
                    AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                    
                    if (!isLocal && !IsPathConnected(remotePath))
                    {
                        AppendResult("正在建立网络驱动器连接...", ModernTheme.Info);
                        var cred = GetCred();
                        if (cred == null) { AppendResult("✗ 未获取到凭证，上传失败", ModernTheme.Danger); ShowProgress(false, ""); return; }
                        NETRESOURCE nr = new NETRESOURCE(); nr.dwType = 1; nr.lpRemoteName = remotePath;
                        string fullUser = !string.IsNullOrEmpty(cred.Domain) ? cred.Domain + "\\" + cred.UserName : cred.UserName;
                        int ret = WNetAddConnection2(nr, cred.Password, fullUser, 0);
                        if (ret != 0) { 
                            AppendResult("  " + GetConnectionErrorStr(ret, pc, remotePath), ModernTheme.Danger); 
                            ShowProgress(false, ""); return; 
                        }
                        lock (_connectedShares) { _connectedShares.Add(remotePath); }
                        AppendResult("✓ 远程连接已建立", ModernTheme.Success);
                    }
                    else if (isLocal)
                    {
                        AppendResult("✓ 检测到本机，绕过网络映射", ModernTheme.Success);
                        if (!Directory.Exists(actualDestDir)) {
                            try { Directory.CreateDirectory(actualDestDir); } catch { }
                        }
                    }

                    int successCount = 0, failCount = 0, skippedCount = 0;
                    for (int i = 0; i < files.Length; i++)
                    {
                        if (_cancelUpload) { AppendResult("", ModernTheme.TextPrimary); AppendResult("⚠ 用户取消了上传操作", ModernTheme.Warning); break; }
                        _currentFileIndex = i + 1;
                        string file = files[i], fileName = Path.GetFileName(file), destPath = Path.Combine(actualDestDir, fileName);
                        _currentUploadingFile = fileName;
                        UpdateProgress(0, fileName, _currentFileIndex, _totalFiles);
                        bool fileExists = false;
                        try { fileExists = File.Exists(destPath); } catch { }
                        if (fileExists)
                        {
                            DialogResult result = DialogResult.None;
                            if (InvokeRequired) result = (DialogResult)Invoke(new Func<DialogResult>(() => MessageBox.Show(
                                string.Format("文件 {0} 已存在，是否覆盖？", fileName), "文件已存在", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question)));
                            else result = MessageBox.Show(string.Format("文件 {0} 已存在，是否覆盖？", fileName), "文件已存在", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                            if (result == DialogResult.Cancel) { AppendResult("⊘ 用户取消上传", ModernTheme.Warning); break; }
                            else if (result != DialogResult.Yes) { AppendResult("⊘ 跳过: " + fileName, ModernTheme.Warning); skippedCount++; continue; }
                        }
                        AppendResult(string.Format("📤 [{0}/{1}] 上传: {2}", _currentFileIndex, _totalFiles, fileName), ModernTheme.Info);
                        try
                        {
                            bool cancel = false;
                            if (CopyFileEx(file, destPath, new CopyProgressRoutine(CopyProgressHandler), IntPtr.Zero, ref cancel, 0))
                            { successCount++; long fileSize = 0; try { fileSize = new FileInfo(file).Length; } catch { } 
                                AppendResult("  ✓ 完成 (" + FormatFileSize(fileSize) + ")", ModernTheme.Success); }
                            else { failCount++; AppendResult("  ✗ 失败: 错误代码 " + Marshal.GetLastWin32Error(), ModernTheme.Danger); }
                        }
                        catch (Exception ex) { failCount++; AppendResult("  ✗ 异常: " + ex.Message, ModernTheme.Danger); }
                    }
                    AppendResult("", ModernTheme.TextPrimary);
                    AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                    AppendResult(string.Format("上传完成: 成功 {0} 个, 失败 {1} 个, 跳过 {2} 个", successCount, failCount, skippedCount), 
                        successCount > 0 ? ModernTheme.Success : ModernTheme.Warning);
                    AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                    ShowProgress(false, "");
                }
                catch (Exception ex) { AppendResult("上传过程出错: " + ex.Message, ModernTheme.Danger); ShowProgress(false, ""); }
                finally { _cancelUpload = false; SetStatus("就绪", false); }
            });
        }

        private string FormatFileSize(long bytes) { if (bytes <= 0) return "0 B"; string[] sizes = { "B", "KB", "MB", "GB", "TB" }; 
            int order = 0; double len = bytes; while (len >= 1024 && order < sizes.Length - 1) { order++; len = len / 1024; } 
            return string.Format("{0:0.##} {1}", len, sizes[order]); }
        
        private bool IsPathConnected(string path) { lock (_connectedShares) { return _connectedShares.Contains(path); } }
        private void UpdateTimeDisplay(object sender, EventArgs e) { if (_timeLabel != null) _timeLabel.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }
        private void ExecuteAsync(Action action) { Task.Run(() => { try { action(); } catch (Exception ex) { AppendResult("执行错误: " + ex.Message, ModernTheme.Danger); } }); }
        private void SetStatus(string text, bool isBusy) { if (_statusLabel.InvokeRequired) { _statusLabel.Invoke(new Action(() => SetStatus(text, isBusy))); return; } 
            if (!_uploadProgress.Visible) { _statusLabel.Text = (isBusy ? "🔄 " : "● ") + text; _statusLabel.ForeColor = isBusy ? ModernTheme.Warning : ModernTheme.Success; } }
        private void AppendResult(string text, Color color) { _resultBox.AppendColoredText(text, color); }
        
        private void ToggleContinuousPing(object sender, EventArgs e) { string t = GetTargetComputer(); if (string.IsNullOrEmpty(t)) { AppendResult("✗ 请输入目标地址", ModernTheme.Danger); return; } 
            if (_continuePing) { _continuePing = false; _btnContinuousPing.Text = "Ping"; AppendResult("停止 Ping", ModernTheme.Warning); } 
            else { _continuePing = true; _btnContinuousPing.Text = "停止"; Task.Run(() => ContinuousPing(t)); } }
        
        private void CheckRemoteDiskSpace()
        {
            string pc = GetTargetComputer();
            
            AppendResult("", ModernTheme.TextPrimary);
            AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
            AppendResult(string.Format("正在查询目标电脑 [{0}] 的磁盘空间...", pc), ModernTheme.Info);
            
            if (!IsComputerOnline(pc))
            {
                AppendResult("  ✗ 查询失败: 目标电脑已离线", ModernTheme.Danger);
                AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                return;
            }
            
            try
            {
                ManagementScope scope = GetWmiScope(pc, GetCredForWMI(), @"root\cimv2");
                scope.Connect();
                ObjectQuery query = new ObjectQuery("SELECT Name, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
                ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
                ManagementObjectCollection queryCollection = searcher.Get();
                
                bool found = false;
                foreach (ManagementObject m in queryCollection)
                {
                    found = true;
                    string name = m["Name"] != null ? m["Name"].ToString() : "未知";
                    
                    if (m["Size"] != null && m["FreeSpace"] != null)
                    {
                        ulong size = Convert.ToUInt64(m["Size"]);
                        ulong free = Convert.ToUInt64(m["FreeSpace"]);
                        ulong used = size - free;
                        
                        double totalGB = size / 1073741824.0;
                        double freeGB = free / 1073741824.0;
                        double usedGB = used / 1073741824.0;
                        
                        double usedPct = size > 0 ? ((double)used / size) * 100 : 0;
                        double freePct = size > 0 ? ((double)free / size) * 100 : 0;
                        
                        AppendResult(string.Format("  【磁盘 {0}】", name), ModernTheme.PrimaryLight);
                        AppendResult(string.Format("    总 大 小: {0:0.00} GB", totalGB), ModernTheme.TextSecondary);
                        AppendResult(string.Format("    已 使 用: {0:0.00} GB  (使用率: {1:0.0}%)", usedGB, usedPct), usedPct > 90 ? ModernTheme.Danger : (usedPct > 80 ? ModernTheme.Warning : ModernTheme.TextSecondary));
                        AppendResult(string.Format("    剩余空间: {0:0.00} GB  (剩余率: {1:0.0}%)", freeGB, freePct), freePct < 10 ? ModernTheme.Danger : ModernTheme.Success);
                    }
                }
                if (!found)
                {
                    AppendResult("  ✗ 未找到任何本地磁盘信息", ModernTheme.Warning);
                }
            }
            catch (Exception ex)
            {
                AppendResult("  ✗ 查询失败: " + ex.Message, ModernTheme.Danger);
                AppendResult("    可能原因: RPC 服务未开启或防火墙拦截了 WMI 请求。", ModernTheme.TextMuted);
            }
            AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
        }
        
        private void ClearCredentials(object sender, EventArgs e) { if (File.Exists(_credFile)) File.Delete(_credFile); _currentCred = null; _currentPassword = ""; 
            AppendResult("凭证已清除", ModernTheme.Success); MessageBox.Show("已清除保存的账号密码", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); }

        #region 注册表值管理功能
        private void ManageRegistry() {
            string pc = GetTargetComputer();
            bool isLocal = IsLocalMachine(pc);
            NetworkCredential cred = null;
            if (!isLocal) {
                cred = GetCredForWMI();
                if (cred == null) { AppendResult("✗ 未获取到凭证", ModernTheme.Danger); return; }
            }

            using (Form f = new Form()) {
                f.Text = "注册表编辑器 - " + pc + (isLocal ? " (本机)" : "");
                f.Size = new Size(950, 650);
                f.StartPosition = FormStartPosition.CenterParent;
                f.BackColor = ModernTheme.BgCard;
                f.FormBorderStyle = FormBorderStyle.Sizable; 
                f.MaximizeBox = true; 
                f.MinimizeBox = true; 
                try { f.Icon = this.Icon; } catch { }

                Panel addressPanel = new Panel { Dock = DockStyle.Top, Height = 35, BackColor = ModernTheme.BgCard, Padding = new Padding(10, 5, 10, 5) };
                TextBox txtAddress = new TextBox { Dock = DockStyle.Fill, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9.5f) };
                addressPanel.Controls.Add(txtAddress);

                Panel bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = ModernTheme.BgSecondary };
                Label lStatus = new Label { Text = "就绪", ForeColor = ModernTheme.TextSecondary, Location = new Point(15, 20), Size = new Size(400, 20), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
                ModernButton btnRefresh = new ModernButton { Text = "刷新", ButtonColor = ModernTheme.Info, Location = new Point(480, 15), Size = new Size(80, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
                ModernButton btnAdd = new ModernButton { Text = "新建值", ButtonColor = ModernTheme.Success, Location = new Point(570, 15), Size = new Size(80, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
                ModernButton btnEdit = new ModernButton { Text = "修改值", ButtonColor = ModernTheme.Primary, Location = new Point(660, 15), Size = new Size(80, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
                ModernButton btnDelete = new ModernButton { Text = "删除值", ButtonColor = ModernTheme.Danger, Location = new Point(750, 15), Size = new Size(80, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
                ModernButton btnClose = new ModernButton { Text = "关闭", ButtonColor = Color.FromArgb(71, 85, 105), Location = new Point(840, 15), Size = new Size(80, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
                
                btnClose.Click += (s, e) => f.Close();
                bottomPanel.Controls.AddRange(new Control[] { lStatus, btnRefresh, btnAdd, btnEdit, btnDelete, btnClose });

                SplitContainer sc = new SplitContainer { Dock = DockStyle.Fill, BackColor = ModernTheme.Border };
                
                TreeView tvReg = new TreeView { Dock = DockStyle.Fill, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5f), HideSelection = false };
                ListView lvValues = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5f) };
                lvValues.Columns.Add("名称", 180);
                lvValues.Columns.Add("类型", 100);
                lvValues.Columns.Add("数据", 300);

                lvValues.Resize += (s, e) => {
                    if (lvValues.Columns.Count >= 3) {
                        int w = lvValues.ClientSize.Width - lvValues.Columns[0].Width - lvValues.Columns[1].Width;
                        if (w > 50) lvValues.Columns[2].Width = w;
                    }
                };

                sc.Panel1.Controls.Add(tvReg);
                sc.Panel2.Controls.Add(lvValues);
                sc.Panel1.BackColor = ModernTheme.BgCard;
                sc.Panel2.BackColor = ModernTheme.BgCard;

                f.Controls.Add(sc);
                f.Controls.Add(addressPanel);
                f.Controls.Add(bottomPanel);
                sc.BringToFront(); 

                Func<TreeNode, uint> getHive = (node) => {
                    TreeNode root = node;
                    while (root.Parent != null) root = root.Parent;
                    if (root.Text == "HKEY_LOCAL_MACHINE") return 0x80000002;
                    if (root.Text == "HKEY_CURRENT_USER") return 0x80000001;
                    if (root.Text == "HKEY_USERS") return 0x80000003;
                    return 0x80000002;
                };

                Action initRoots = () => {
                    tvReg.Nodes.Clear();
                    string[] roots = { "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER", "HKEY_USERS" };
                    foreach (string r in roots) {
                        TreeNode n = new TreeNode(r);
                        n.Tag = "";
                        n.Nodes.Add(""); // dummy
                        tvReg.Nodes.Add(n);
                    }
                };

                Action<TreeNode> loadSubKeys = (parentNode) => {
                    string subKeyPath = parentNode.Tag as string ?? "";
                    Task.Run(() => {
                        try {
                            ManagementScope scope = GetWmiScope(pc, cred, @"root\default");
                            scope.Connect();
                            ManagementClass regClass = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                            
                            uint hive = 0;
                            f.Invoke((Action)(() => hive = getHive(parentNode)));
                            
                            string actualSubKey = subKeyPath;
                            if (hive == 0x80000001) {
                                string sid = GetCurrentUserSid(pc, cred);
                                if (string.IsNullOrEmpty(sid)) {
                                    f.Invoke((Action)(() => { parentNode.Nodes.Clear(); parentNode.Nodes.Add("获取SID失败"); })); return;
                                }
                                hive = 0x80000003;
                                actualSubKey = string.IsNullOrEmpty(subKeyPath) ? sid : sid + "\\" + subKeyPath;
                            }

                            ManagementBaseObject inParams = regClass.GetMethodParameters("EnumKey");
                            inParams["hDefKey"] = hive;
                            inParams["sSubKeyName"] = actualSubKey;
                            ManagementBaseObject outParams = regClass.InvokeMethod("EnumKey", inParams, null);
                            string[] subKeys = outParams["sNames"] as string[];

                            f.Invoke((Action)(() => {
                                parentNode.Nodes.Clear();
                                if (subKeys != null) {
                                    Array.Sort(subKeys);
                                    foreach (string sk in subKeys) {
                                        TreeNode n = new TreeNode(sk);
                                        n.Tag = string.IsNullOrEmpty(subKeyPath) ? sk : subKeyPath + "\\" + sk;
                                        n.Nodes.Add(""); // Dummy
                                        parentNode.Nodes.Add(n);
                                    }
                                }
                            }));
                        } catch (Exception ex) {
                            f.Invoke((Action)(() => { parentNode.Nodes.Clear(); parentNode.Nodes.Add("错误: " + ex.Message); }));
                        }
                    });
                };

                tvReg.BeforeExpand += (s, e) => {
                    if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Text == "") {
                        e.Node.Nodes[0].Text = "加载中...";
                        loadSubKeys(e.Node);
                    }
                };

                Action loadValues = () => {
                    if (tvReg.SelectedNode == null) return;
                    TreeNode node = tvReg.SelectedNode;
                    string subKeyPath = node.Tag as string ?? "";
                    
                    lvValues.Items.Clear();
                    SetLabelSafe(lStatus, "正在读取值...");

                    Task.Run(() => {
                        try {
                            ManagementScope scope = GetWmiScope(pc, cred, @"root\default");
                            scope.Connect();
                            ManagementClass regClass = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                            
                            uint hive = 0;
                            f.Invoke((Action)(() => hive = getHive(node)));
                            
                            string actualSubKey = subKeyPath;
                            if (hive == 0x80000001) {
                                string sid = GetCurrentUserSid(pc, cred);
                                if (string.IsNullOrEmpty(sid)) { SetLabelSafe(lStatus, "失败: 无法获取SID"); return; }
                                hive = 0x80000003;
                                actualSubKey = string.IsNullOrEmpty(subKeyPath) ? sid : sid + "\\" + subKeyPath;
                            }

                            ManagementBaseObject inParams = regClass.GetMethodParameters("EnumValues");
                            inParams["hDefKey"] = hive;
                            inParams["sSubKeyName"] = actualSubKey;
                            ManagementBaseObject outParams = regClass.InvokeMethod("EnumValues", inParams, null);
                            
                            string[] names = outParams["sNames"] as string[];
                            int[] types = outParams["Types"] as int[];
                            
                            List<ListViewItem> items = new List<ListViewItem>();

                            if (names != null && types != null) {
                                for (int i = 0; i < names.Length; i++) {
                                    string vName = string.IsNullOrEmpty(names[i]) ? "(默认)" : names[i];
                                    int vType = types[i];
                                    string typeStr = "未知";
                                    string dataStr = "";
                                    
                                    string realName = names[i];

                                    if (vType == 1 || vType == 2) {
                                        typeStr = vType == 1 ? "REG_SZ" : "REG_EXPAND_SZ";
                                        string m = vType == 1 ? "GetStringValue" : "GetExpandedStringValue";
                                        ManagementBaseObject p2 = regClass.GetMethodParameters(m);
                                        p2["hDefKey"] = hive; p2["sSubKeyName"] = actualSubKey; p2["sValueName"] = realName;
                                        ManagementBaseObject r2 = regClass.InvokeMethod(m, p2, null);
                                        if (r2 != null && r2["sValue"] != null) dataStr = r2["sValue"].ToString();
                                    }
                                    else if (vType == 4) {
                                        typeStr = "REG_DWORD";
                                        ManagementBaseObject p2 = regClass.GetMethodParameters("GetDWORDValue");
                                        p2["hDefKey"] = hive; p2["sSubKeyName"] = actualSubKey; p2["sValueName"] = realName;
                                        ManagementBaseObject r2 = regClass.InvokeMethod("GetDWORDValue", p2, null);
                                        if (r2 != null && r2["uValue"] != null) {
                                            uint val = Convert.ToUInt32(r2["uValue"]);
                                            dataStr = string.Format("0x{0:x8} ({1})", val, val);
                                        }
                                    }
                                    else if (vType == 3) { typeStr = "REG_BINARY"; dataStr = "(不支持预览)"; }
                                    else if (vType == 7) { typeStr = "REG_MULTI_SZ"; dataStr = "(多字符串)"; }
                                    else if (vType == 11) { typeStr = "REG_QWORD"; dataStr = "(不支持预览)"; }

                                    ListViewItem itm = new ListViewItem(vName);
                                    itm.SubItems.Add(typeStr);
                                    itm.SubItems.Add(dataStr);
                                    itm.Tag = realName + "|" + vType;
                                    items.Add(itm);
                                }
                            }

                            f.Invoke((Action)(() => {
                                lvValues.Items.Clear();
                                if(items.Count > 0) {
                                    bool hasDefault = items.Any(x => x.Text == "(默认)");
                                    if(!hasDefault) {
                                        ListViewItem def = new ListViewItem("(默认)"); def.SubItems.Add("REG_SZ"); def.SubItems.Add("(数值未设置)"); def.Tag = "|" + 1;
                                        lvValues.Items.Add(def);
                                    }
                                    lvValues.Items.AddRange(items.OrderBy(x => x.Text == "(默认)" ? "" : x.Text).ToArray());
                                } else {
                                    ListViewItem def = new ListViewItem("(默认)"); def.SubItems.Add("REG_SZ"); def.SubItems.Add("(数值未设置)"); def.Tag = "|" + 1;
                                    lvValues.Items.Add(def);
                                }
                                SetLabelSafe(lStatus, string.Format("路径: {0} | 找到 {1} 个值", subKeyPath, items.Count));
                            }));

                        } catch (Exception ex) {
                            SetLabelSafe(lStatus, "读取值失败: " + ex.Message);
                        }
                    });
                };

                txtAddress.KeyDown += (s, e) => {
                    if (e.KeyCode == Keys.Enter) {
                        e.SuppressKeyPress = true;
                        string inputPath = txtAddress.Text.Trim();
                        string[] parts = inputPath.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0) return;
                        
                        int startIndex = 0;
                        if (parts[0].Equals("Computer", StringComparison.OrdinalIgnoreCase) || parts[0].Equals(pc, StringComparison.OrdinalIgnoreCase)) {
                            startIndex = 1;
                        }
                        if (startIndex >= parts.Length) return;
                        
                        string hiveStr = parts[startIndex].ToUpper();
                        TreeNode current = null;
                        foreach(TreeNode n in tvReg.Nodes) {
                            if(n.Text.ToUpper() == hiveStr) { current = n; break; }
                        }
                        if (current == null) return;
                        
                        SetLabelSafe(lStatus, "正在导航...");
                        
                        Task.Run(() => {
                            try {
                                ManagementScope scope = GetWmiScope(pc, cred, @"root\default");
                                scope.Connect();
                                ManagementClass regClass = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                                
                                uint hive = 0x80000002;
                                if (current.Text == "HKEY_LOCAL_MACHINE") hive = 0x80000002;
                                else if (current.Text == "HKEY_CURRENT_USER") hive = 0x80000001;
                                else if (current.Text == "HKEY_USERS") hive = 0x80000003;
                                
                                if (hive == 0x80000001) {
                                    string sid = GetCurrentUserSid(pc, cred);
                                    if (!string.IsNullOrEmpty(sid)) {
                                        hive = 0x80000003;
                                    }
                                }

                                for (int i = startIndex + 1; i < parts.Length; i++) {
                                    bool needsLoad = false;
                                    f.Invoke((Action)(() => {
                                        if (current.Nodes.Count == 1 && current.Nodes[0].Text == "") needsLoad = true;
                                    }));

                                    if (needsLoad) {
                                        string curPath = "";
                                        f.Invoke((Action)(() => { curPath = current.Tag as string ?? ""; }));
                                        
                                        string actualSubKey = curPath;
                                        if (current.Text == "HKEY_CURRENT_USER" || (hive == 0x80000003 && curPath == "")) {
                                            string sid = GetCurrentUserSid(pc, cred);
                                            actualSubKey = string.IsNullOrEmpty(curPath) ? sid : sid + "\\" + curPath;
                                        }

                                        ManagementBaseObject inParams = regClass.GetMethodParameters("EnumKey");
                                        inParams["hDefKey"] = hive;
                                        inParams["sSubKeyName"] = actualSubKey;
                                        ManagementBaseObject outParams = regClass.InvokeMethod("EnumKey", inParams, null);
                                        string[] subKeys = outParams["sNames"] as string[];
                                        
                                        f.Invoke((Action)(() => {
                                            current.Nodes.Clear();
                                            if (subKeys != null) {
                                                Array.Sort(subKeys);
                                                foreach (string sk in subKeys) {
                                                    TreeNode n = new TreeNode(sk);
                                                    n.Tag = string.IsNullOrEmpty(curPath) ? sk : curPath + "\\" + sk;
                                                    n.Nodes.Add("");
                                                    current.Nodes.Add(n);
                                                }
                                            }
                                        }));
                                    }
                                    
                                    bool found = false;
                                    f.Invoke((Action)(() => {
                                        current.Expand();
                                        foreach (TreeNode child in current.Nodes) {
                                            if (child.Text.Equals(parts[i], StringComparison.OrdinalIgnoreCase)) {
                                                current = child;
                                                found = true;
                                                break;
                                            }
                                        }
                                    }));
                                    if (!found) break; 
                                }
                                
                                f.Invoke((Action)(() => {
                                    tvReg.SelectedNode = current;
                                    current.EnsureVisible();
                                    SetLabelSafe(lStatus, "导航完成");
                                }));
                                
                            } catch (Exception ex) {
                                SetLabelSafe(lStatus, "导航失败: " + ex.Message);
                            }
                        });
                    }
                };

                tvReg.AfterSelect += (s, e) => { 
                    TreeNode node = tvReg.SelectedNode;
                    if (node != null) {
                        TreeNode root = node;
                        while (root.Parent != null) root = root.Parent;
                        string subKeyPath = node.Tag as string ?? "";
                        string displayPc = (pc == "." || pc.ToLower() == "localhost" || pc == "127.0.0.1") ? "Computer" : pc;
                        if (string.IsNullOrEmpty(subKeyPath)) {
                            txtAddress.Text = displayPc + "\\" + root.Text;
                        } else {
                            txtAddress.Text = displayPc + "\\" + root.Text + "\\" + subKeyPath;
                        }
                    }
                    loadValues(); 
                };
                
                btnRefresh.Click += (s, e) => { loadValues(); };

                Action<bool> addOrEditValue = (isEdit) => {
                    if (tvReg.SelectedNode == null) { MessageBox.Show("请先在左侧选择一个项(文件夹)"); return; }
                    if (isEdit && lvValues.SelectedItems.Count == 0) { MessageBox.Show("请先在右侧选择一个值"); return; }
                    
                    TreeNode node = tvReg.SelectedNode;
                    string subKeyPath = node.Tag as string ?? "";
                    
                    string oldName = "";
                    string oldVal = "";
                    int oldType = 1; // Default REG_SZ

                    if (isEdit) {
                        string[] tParts = lvValues.SelectedItems[0].Tag.ToString().Split('|');
                        oldName = tParts[0];
                        oldType = int.Parse(tParts[1]);
                        
                        if (oldType == 1 || oldType == 2) oldVal = lvValues.SelectedItems[0].SubItems[2].Text;
                        else if (oldType == 4) {
                            string txt = lvValues.SelectedItems[0].SubItems[2].Text;
                            int idx = txt.IndexOf('(');
                            if (idx != -1) {
                                oldVal = txt.Substring(idx+1).TrimEnd(')');
                            }
                        }
                    }

                    if (isEdit && oldType != 1 && oldType != 2 && oldType != 4) {
                        MessageBox.Show("目前只支持编辑字符串(REG_SZ)和DWORD(REG_DWORD)类型的值。"); return;
                    }

                    using(Form frm = new Form() { Text = isEdit ? "编辑值" : "新建值", Size = new Size(400, 280), StartPosition = FormStartPosition.CenterParent, BackColor = ModernTheme.BgCard, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false }) {
                        Label l1 = new Label { Text = "值名称:", ForeColor = ModernTheme.TextPrimary, Location = new Point(20, 20), AutoSize = true };
                        TextBox t1 = new TextBox { Location = new Point(20, 45), Size = new Size(340, 25), Text = oldName, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.FixedSingle };
                        if (isEdit) t1.Enabled = false; 

                        Label l2 = new Label { Text = "数值数据:", ForeColor = ModernTheme.TextPrimary, Location = new Point(20, 85), AutoSize = true };
                        TextBox t2 = new TextBox { Location = new Point(20, 110), Size = new Size(340, 25), Text = oldVal, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.FixedSingle };
                        
                        Label l3 = new Label { Text = "类型:", ForeColor = ModernTheme.TextPrimary, Location = new Point(20, 150), AutoSize = true };
                        ComboBox cType = new ComboBox { Location = new Point(20, 175), Size = new Size(340, 25), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary };
                        cType.Items.AddRange(new object[] { "REG_SZ", "REG_DWORD" });
                        cType.SelectedIndex = (oldType == 4) ? 1 : 0;
                        if (isEdit) cType.Enabled = false;

                        Button bOk = new Button { Text = "确定", Location = new Point(190, 220), Size = new Size(80, 30), BackColor = ModernTheme.Primary, ForeColor = ModernTheme.TextPrimary, FlatStyle = FlatStyle.Flat };
                        Button bCancel = new Button { Text = "取消", Location = new Point(280, 220), Size = new Size(80, 30), BackColor = Color.FromArgb(71, 85, 105), ForeColor = ModernTheme.TextPrimary, FlatStyle = FlatStyle.Flat };
                        
                        bCancel.Click += (ss, ee) => frm.DialogResult = DialogResult.Cancel;
                        bOk.Click += (ss, ee) => {
                            string n = t1.Text.Trim(); string v = t2.Text.Trim(); bool isDword = cType.SelectedIndex == 1;
                            
                            Task.Run(() => {
                                try {
                                    SetLabelSafe(lStatus, "正在保存...");
                                    ManagementScope scope = GetWmiScope(pc, cred, @"root\default");
                                    scope.Connect();
                                    ManagementClass regClass = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                                    
                                    uint hive = 0; f.Invoke((Action)(() => hive = getHive(node)));
                                    string actualSubKey = subKeyPath;
                                    
                                    if (hive == 0x80000001) {
                                        string sid = GetCurrentUserSid(pc, cred);
                                        if (string.IsNullOrEmpty(sid)) { SetLabelSafe(lStatus, "保存失败: 无法获取 SID"); return; }
                                        hive = 0x80000003; actualSubKey = string.IsNullOrEmpty(subKeyPath) ? sid : sid + "\\" + subKeyPath;
                                    }

                                    string method = isDword ? "SetDWORDValue" : "SetStringValue";
                                    ManagementBaseObject inParams = regClass.GetMethodParameters(method);
                                    inParams["hDefKey"] = hive;
                                    inParams["sSubKeyName"] = actualSubKey;
                                    inParams["sValueName"] = n;
                                    
                                    if(isDword) {
                                        uint dwordVal;
                                        if(uint.TryParse(v, out dwordVal)) inParams["uValue"] = dwordVal;
                                        else { SetLabelSafe(lStatus, "数据不是有效的 DWORD"); return; }
                                    } else {
                                        inParams["sValue"] = v;
                                    }

                                    ManagementBaseObject outParams = regClass.InvokeMethod(method, inParams, null);
                                    if (outParams != null && outParams["ReturnValue"] != null && outParams["ReturnValue"].ToString() == "0") {
                                        SetLabelSafe(lStatus, "保存成功");
                                        f.Invoke((Action)(() => loadValues()));
                                    } else {
                                        SetLabelSafe(lStatus, "保存失败，权限不足或路径错误");
                                    }
                                } catch (Exception ex) {
                                    SetLabelSafe(lStatus, "保存失败: " + ex.Message);
                                }
                            });
                            frm.DialogResult = DialogResult.OK;
                        };
                        frm.Controls.AddRange(new Control[] { l1, t1, l2, t2, l3, cType, bOk, bCancel });
                        frm.ShowDialog();
                    }
                };

                btnAdd.Click += (s, e) => addOrEditValue(false);
                btnEdit.Click += (s, e) => addOrEditValue(true);
                lvValues.MouseDoubleClick += (s, e) => addOrEditValue(true);

                btnDelete.Click += (s, e) => {
                    if (tvReg.SelectedNode == null) return;
                    if (lvValues.SelectedItems.Count == 0) { MessageBox.Show("请先在右侧选择要删除的值"); return; }
                    
                    string[] tParts = lvValues.SelectedItems[0].Tag.ToString().Split('|');
                    string vname = tParts[0];
                    if (vname == "") { MessageBox.Show("不能删除(默认)值本身，只能修改"); return; }

                    if (MessageBox.Show("确定要删除该值吗？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
                        TreeNode node = tvReg.SelectedNode;
                        string subKeyPath = node.Tag as string ?? "";
                        Task.Run(() => {
                            try {
                                SetLabelSafe(lStatus, "正在删除...");
                                ManagementScope scope = GetWmiScope(pc, cred, @"root\default");
                                scope.Connect();
                                ManagementClass regClass = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                                uint hive = 0; f.Invoke((Action)(() => hive = getHive(node)));
                                string actualSubKey = subKeyPath;
                                if (hive == 0x80000001) {
                                    string sid = GetCurrentUserSid(pc, cred);
                                    if (string.IsNullOrEmpty(sid)) { SetLabelSafe(lStatus, "删除失败: 无法获取 SID"); return; }
                                    hive = 0x80000003; actualSubKey = string.IsNullOrEmpty(subKeyPath) ? sid : sid + "\\" + subKeyPath;
                                }
                                ManagementBaseObject delParams = regClass.GetMethodParameters("DeleteValue");
                                delParams["hDefKey"] = hive;
                                delParams["sSubKeyName"] = actualSubKey;
                                delParams["sValueName"] = vname;
                                regClass.InvokeMethod("DeleteValue", delParams, null);
                                SetLabelSafe(lStatus, "删除成功");
                                f.Invoke((Action)(() => loadValues()));
                            } catch (Exception ex) {
                                SetLabelSafe(lStatus, "删除失败: " + ex.Message);
                            }
                        });
                    }
                };

                // ===== 左侧 TreeView 的右键菜单（新建项、删除项、重命名项） =====
                ContextMenuStrip tvMenu = new ContextMenuStrip();
                tvMenu.BackColor = ModernTheme.BgSecondary;
                tvMenu.ForeColor = ModernTheme.TextPrimary;

                ToolStripMenuItem miNewKey = new ToolStripMenuItem("新建项");
                ToolStripMenuItem miRenameKey = new ToolStripMenuItem("重命名项");
                ToolStripMenuItem miDelKey = new ToolStripMenuItem("删除项");

                tvMenu.Items.AddRange(new ToolStripItem[] { miNewKey, miRenameKey, miDelKey });

                tvReg.MouseDown += (s, e) => {
                    if (e.Button == MouseButtons.Right) {
                        TreeNode n = tvReg.GetNodeAt(e.X, e.Y);
                        if (n != null) {
                            tvReg.SelectedNode = n;
                        }
                    }
                };

                tvMenu.Opening += (s, e) => {
                    bool isRoot = tvReg.SelectedNode != null && tvReg.SelectedNode.Parent == null;
                    miNewKey.Visible = tvReg.SelectedNode != null;
                    miRenameKey.Visible = tvReg.SelectedNode != null && !isRoot;
                    miDelKey.Visible = tvReg.SelectedNode != null && !isRoot;
                };
                tvReg.ContextMenuStrip = tvMenu;

                miNewKey.Click += (s, e) => {
                    TreeNode node = tvReg.SelectedNode;
                    if (node == null) return;
                    string newName = Microsoft.VisualBasic.Interaction.InputBox("请输入新建项的名称:", "新建项", "新项 #1");
                    if (string.IsNullOrEmpty(newName)) return;

                    string subKeyPath = node.Tag as string ?? "";
                    
                    Task.Run(() => {
                        try {
                            SetLabelSafe(lStatus, "正在创建项...");
                            ManagementScope scope = GetWmiScope(pc, cred, @"root\default");
                            scope.Connect();
                            ManagementClass regClass = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                            
                            uint hive = 0; f.Invoke((Action)(() => hive = getHive(node)));
                            string actualSubKey = subKeyPath;
                            if (hive == 0x80000001) {
                                string sid = GetCurrentUserSid(pc, cred);
                                if (string.IsNullOrEmpty(sid)) { SetLabelSafe(lStatus, "失败: 无法获取SID"); return; }
                                hive = 0x80000003;
                                actualSubKey = string.IsNullOrEmpty(subKeyPath) ? sid : sid + "\\" + subKeyPath;
                            }

                            string newPath = string.IsNullOrEmpty(actualSubKey) ? newName : actualSubKey + "\\" + newName;
                            
                            ManagementBaseObject inParams = regClass.GetMethodParameters("CreateKey");
                            inParams["hDefKey"] = hive;
                            inParams["sSubKeyName"] = newPath;
                            ManagementBaseObject outParams = regClass.InvokeMethod("CreateKey", inParams, null);
                            
                            if (outParams != null && outParams["ReturnValue"] != null && outParams["ReturnValue"].ToString() == "0") {
                                SetLabelSafe(lStatus, "创建项成功");
                                f.Invoke((Action)(() => {
                                    TreeNode n = new TreeNode(newName);
                                    n.Tag = string.IsNullOrEmpty(subKeyPath) ? newName : subKeyPath + "\\" + newName;
                                    n.Nodes.Add(""); // Dummy
                                    node.Nodes.Add(n);
                                    node.Expand();
                                }));
                            } else {
                                SetLabelSafe(lStatus, "创建项失败，权限不足或已存在");
                            }
                        } catch (Exception ex) {
                            SetLabelSafe(lStatus, "创建项失败: " + ex.Message);
                        }
                    });
                };

                miDelKey.Click += (s, e) => {
                    TreeNode node = tvReg.SelectedNode;
                    if (node == null || node.Parent == null) return;
                    if (MessageBox.Show("确定要彻底删除项 '" + node.Text + "' 及其所有子项和键值吗？\n此操作不可逆！", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
                        string subKeyPath = node.Tag as string ?? "";
                        
                        Task.Run(() => {
                            try {
                                SetLabelSafe(lStatus, "正在彻底删除项...");
                                ManagementScope scope = GetWmiScope(pc, cred, @"root\cimv2");
                                scope.Connect();
                                
                                uint hive = 0; f.Invoke((Action)(() => hive = getHive(node)));
                                string actualSubKey = subKeyPath;
                                string rootName = "HKLM";
                                if (hive == 0x80000001) {
                                    string sid = GetCurrentUserSid(pc, cred);
                                    if (string.IsNullOrEmpty(sid)) { SetLabelSafe(lStatus, "失败: 无法获取SID"); return; }
                                    rootName = "HKU";
                                    actualSubKey = string.IsNullOrEmpty(subKeyPath) ? sid : sid + "\\" + subKeyPath;
                                } else if (hive == 0x80000003) {
                                    rootName = "HKU";
                                }

                                string fullRegPath = rootName + "\\" + actualSubKey;
                                string cmd = string.Format("cmd.exe /c reg delete \"{0}\" /f", fullRegPath);
                                
                                ManagementClass processClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), null);
                                ManagementBaseObject inParams = processClass.GetMethodParameters("Create");
                                inParams["CommandLine"] = cmd;
                                ManagementBaseObject outParams = processClass.InvokeMethod("Create", inParams, null);
                                
                                if (outParams != null && outParams["ReturnValue"] != null && outParams["ReturnValue"].ToString() == "0") {
                                    SetLabelSafe(lStatus, "删除项指令已下发执行");
                                    System.Threading.Thread.Sleep(1000); 
                                    f.Invoke((Action)(() => {
                                        TreeNode p = node.Parent;
                                        node.Remove();
                                        tvReg.SelectedNode = p;
                                    }));
                                } else {
                                    SetLabelSafe(lStatus, "删除项失败: 返回码 " + (outParams != null ? outParams["ReturnValue"] : "null"));
                                }
                            } catch (Exception ex) {
                                SetLabelSafe(lStatus, "删除项失败: " + ex.Message);
                            }
                        });
                    }
                };

                miRenameKey.Click += (s, e) => {
                    TreeNode node = tvReg.SelectedNode;
                    if (node == null || node.Parent == null) return;
                    string oldName = node.Text;
                    string newName = Microsoft.VisualBasic.Interaction.InputBox("请输入新的项名称:", "重命名项", oldName);
                    if (string.IsNullOrEmpty(newName) || newName == oldName) return;
                    
                    string subKeyPath = node.Tag as string ?? "";
                    string parentPath = node.Parent.Tag as string ?? "";
                    
                    Task.Run(() => {
                        try {
                            SetLabelSafe(lStatus, "正在重命名项...");
                            ManagementScope scope = GetWmiScope(pc, cred, @"root\cimv2");
                            scope.Connect();
                            
                            uint hive = 0; f.Invoke((Action)(() => hive = getHive(node)));
                            string actualOldSubKey = subKeyPath;
                            string actualNewSubKey = string.IsNullOrEmpty(parentPath) ? newName : parentPath + "\\" + newName;
                            
                            string rootName = "HKLM";
                            if (hive == 0x80000001) {
                                string sid = GetCurrentUserSid(pc, cred);
                                if (string.IsNullOrEmpty(sid)) { SetLabelSafe(lStatus, "失败: 无法获取SID"); return; }
                                rootName = "HKU";
                                actualOldSubKey = string.IsNullOrEmpty(subKeyPath) ? sid : sid + "\\" + subKeyPath;
                                actualNewSubKey = string.IsNullOrEmpty(parentPath) ? sid + "\\" + newName : sid + "\\" + parentPath + "\\" + newName;
                            } else if (hive == 0x80000003) {
                                rootName = "HKU";
                            }

                            string fullOldPath = rootName + "\\" + actualOldSubKey;
                            string fullNewPath = rootName + "\\" + actualNewSubKey;
                            
                            // 核心指令：复制所有子项与键值到新名称，然后删除旧项
                            string cmd = string.Format("cmd.exe /c reg copy \"{0}\" \"{1}\" /s /f && reg delete \"{0}\" /f", fullOldPath, fullNewPath);
                            
                            ManagementClass processClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), null);
                            ManagementBaseObject inParams = processClass.GetMethodParameters("Create");
                            inParams["CommandLine"] = cmd;
                            ManagementBaseObject outParams = processClass.InvokeMethod("Create", inParams, null);
                            
                            if (outParams != null && outParams["ReturnValue"] != null && outParams["ReturnValue"].ToString() == "0") {
                                SetLabelSafe(lStatus, "重命名项指令已下发执行");
                                System.Threading.Thread.Sleep(1500); 
                                f.Invoke((Action)(() => {
                                    node.Text = newName;
                                    node.Tag = string.IsNullOrEmpty(parentPath) ? newName : parentPath + "\\" + newName;
                                    node.Collapse();
                                    node.Nodes.Clear();
                                    node.Nodes.Add("");
                                    tvReg.SelectedNode = node;
                                }));
                            } else {
                                SetLabelSafe(lStatus, "重命名项失败: 返回码 " + (outParams != null ? outParams["ReturnValue"] : "null"));
                            }
                        } catch (Exception ex) {
                            SetLabelSafe(lStatus, "重命名项失败: " + ex.Message);
                        }
                    });
                };

                ContextMenuStrip ctxMenu = new ContextMenuStrip();
                ctxMenu.BackColor = ModernTheme.BgSecondary;
                ctxMenu.ForeColor = ModernTheme.TextPrimary;
                ToolStripMenuItem miAdd = new ToolStripMenuItem("新建值");
                miAdd.Click += (s, e) => addOrEditValue(false);
                ToolStripMenuItem miEdit = new ToolStripMenuItem("修改值");
                miEdit.Click += (s, e) => addOrEditValue(true);
                ToolStripMenuItem miDel = new ToolStripMenuItem("删除值");
                miDel.Click += (s, e) => btnDelete.PerformClick();
                
                ctxMenu.Items.AddRange(new ToolStripItem[] { miAdd, miEdit, miDel });
                ctxMenu.Opening += (s, e) => {
                    bool hasSel = lvValues.SelectedItems.Count > 0;
                    miEdit.Visible = hasSel;
                    miDel.Visible = hasSel;
                };
                lvValues.ContextMenuStrip = ctxMenu;

                f.Shown += (s, e) => {
                    sc.SplitterDistance = 280;
                    initRoots();
                };
                f.ShowDialog();
            }
        }
        #endregion

        #region 环境变量管理功能
        private Dictionary<string, string> GetAllLoggedInUsers(string computerName, NetworkCredential cred) {
            Dictionary<string, string> users = new Dictionary<string, string>();
            try {
                ManagementScope cimv2Scope = GetWmiScope(computerName, cred, @"root\cimv2");
                cimv2Scope.Connect();
                using (var searcher = new ManagementObjectSearcher(cimv2Scope, new ObjectQuery("SELECT * FROM Win32_Process WHERE Name='explorer.exe'"))) {
                    foreach (ManagementObject m in searcher.Get()) {
                        try {
                            string sid = null; string user = "";
                            ManagementBaseObject sidOut = m.InvokeMethod("GetOwnerSid", null, null);
                            if (sidOut != null && sidOut["Sid"] != null) sid = sidOut["Sid"].ToString();
                            object[] ownerInfo = new object[2];
                            m.InvokeMethod("GetOwner", ownerInfo);
                            if (ownerInfo[0] != null) {
                                string u = ownerInfo[0].ToString();
                                string d = ownerInfo[1] != null ? ownerInfo[1].ToString() : "";
                                user = (string.IsNullOrEmpty(d) ? "" : d + "\\") + u;
                            }
                            if (!string.IsNullOrEmpty(sid) && !users.ContainsKey(sid)) users[sid] = string.IsNullOrEmpty(user) ? "未知用户" : user;
                        } catch {}
                    }
                }
            } catch {}
            return users;
        }

        private void ManageEnvVars() {
            string pc = GetTargetComputer();
            bool isLocal = IsLocalMachine(pc);
            
            NetworkCredential cred = null;
            if (!isLocal) {
                cred = GetCredForWMI();
                if (cred == null) { AppendResult("✗ 未获取到凭证", ModernTheme.Danger); return; }
            }

            using (Form dialog = new Form()) {
                dialog.Text = "环境变量 - " + pc;
                dialog.Size = new Size(800, 520);
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.BackColor = ModernTheme.BgCard;
                dialog.FormBorderStyle = FormBorderStyle.Sizable;
                dialog.MaximizeBox = true;
                dialog.MinimizeBox = true;
                try { dialog.Icon = this.Icon; } catch { }

                Panel titleBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = ModernTheme.BgSecondary };
                Label titleLabel = new Label { Text = "环境变量管理 - " + pc + (isLocal ? " (本机)" : ""), Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = ModernTheme.TextPrimary, Location = new Point(15, 10), Size = new Size(400, 25) };
                titleBar.Controls.Add(titleLabel);

                TabControl tabControl = new TabControl { Location = new Point(15, 55), Size = new Size(755, 360), Font = new Font("Segoe UI", 9.5f), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
                
                TabPage tabSystem = new TabPage { Text = "系统环境变量", BackColor = ModernTheme.BgSecondary };
                ListView lvSystem = CreateEnvListView();
                tabSystem.Controls.Add(lvSystem);
                tabControl.TabPages.Add(tabSystem);

                ModernButton btnRefresh = new ModernButton { Text = "刷新", ButtonColor = ModernTheme.Info, Location = new Point(15, 430), Size = new Size(80, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
                ModernButton btnAdd = new ModernButton { Text = "添加", ButtonColor = ModernTheme.Success, Location = new Point(105, 430), Size = new Size(80, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
                ModernButton btnEdit = new ModernButton { Text = "编辑", ButtonColor = ModernTheme.Primary, Location = new Point(195, 430), Size = new Size(80, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
                ModernButton btnDelete = new ModernButton { Text = "删除", ButtonColor = ModernTheme.Danger, Location = new Point(285, 430), Size = new Size(80, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
                ModernButton btnClose = new ModernButton { Text = "关闭", ButtonColor = Color.FromArgb(71, 85, 105), Location = new Point(690, 430), Size = new Size(80, 35), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
                btnClose.Click += (s, e) => dialog.Close();

                Label lblStatus = new Label { Text = "就绪", ForeColor = ModernTheme.TextSecondary, Location = new Point(15, 475), Size = new Size(600, 20), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };

                dialog.Controls.AddRange(new Control[] { titleBar, tabControl, btnRefresh, btnAdd, btnEdit, btnDelete, btnClose, lblStatus });

                Action<bool> addOrEditAction = null; // 预先声明以供委托绑定

                Action loadAction = () => {
                    try {
                        SetLabelSafe(lblStatus, "正在加载环境变量...");
                        ManagementScope defaultScope = GetWmiScope(pc, cred, @"root\default");
                        defaultScope.Connect();
                        ManagementClass regClass = new ManagementClass(defaultScope, new ManagementPath("StdRegProv"), null);
                        
                        var sysVars = ReadRegValues(regClass, 0x80000002, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
                        dialog.Invoke((Action)(() => {
                            lvSystem.Items.Clear();
                            foreach(var kvp in sysVars.OrderBy(k => k.Key)) {
                                lvSystem.Items.Add(new ListViewItem(new string[] { kvp.Key, kvp.Value }));
                            }
                        }));

                        Dictionary<string, string> activeUsers = GetAllLoggedInUsers(pc, cred);

                        dialog.Invoke((Action)(() => {
                            // 清除除系统变量以外的所有选项卡，为重新生成做准备
                            while (tabControl.TabPages.Count > 1) {
                                tabControl.TabPages.RemoveAt(1);
                            }

                            if (activeUsers.Count > 0) {
                                foreach (var kvp in activeUsers) {
                                    string sId = kvp.Key; string uName = kvp.Value;
                                    TabPage userTab = new TabPage { Text = string.Format("用户变量 ({0})", uName), BackColor = ModernTheme.BgSecondary, Tag = sId };
                                    ListView lvUser = CreateEnvListView();
                                    lvUser.MouseDoubleClick += (s, e) => addOrEditAction(true); // 动态绑定双击事件
                                    userTab.Controls.Add(lvUser);
                                    tabControl.TabPages.Add(userTab);

                                    var userVars = ReadRegValues(regClass, 0x80000003, sId + @"\Environment");
                                    foreach(var varKvp in userVars.OrderBy(k => k.Key)) {
                                        lvUser.Items.Add(new ListViewItem(new string[] { varKvp.Key, varKvp.Value }));
                                    }
                                }
                            } else {
                                TabPage noUserTab = new TabPage { Text = "用户变量 (未登录)", BackColor = ModernTheme.BgSecondary };
                                ListView lvUser = CreateEnvListView();
                                lvUser.MouseDoubleClick += (s, e) => addOrEditAction(true);
                                noUserTab.Controls.Add(lvUser);
                                tabControl.TabPages.Add(noUserTab);
                            }
                        }));
                        SetLabelSafe(lblStatus, "加载完成");
                    } catch(Exception ex) {
                        SetLabelSafe(lblStatus, "加载失败: " + ex.Message);
                    }
                };

                btnRefresh.Click += (s, e) => Task.Run(loadAction);
                dialog.Shown += (s, e) => Task.Run(loadAction);

                addOrEditAction = (isEdit) => {
                    bool isSystem = tabControl.SelectedIndex == 0;
                    TabPage currentTab = tabControl.SelectedTab;
                    ListView targetLv = currentTab.Controls.OfType<ListView>().FirstOrDefault();
                    if (targetLv == null) return;
                    
                    string targetSid = isSystem ? "" : (currentTab.Tag as string ?? "");
                    string targetUserName = isSystem ? "系统" : currentTab.Text.Replace("用户变量 (", "").TrimEnd(')');

                    if (isEdit && targetLv.SelectedItems.Count == 0) { MessageBox.Show("请先选择一项进行编辑", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    if (!isSystem && string.IsNullOrEmpty(targetSid)) { MessageBox.Show("当前无用户登录，无法操作用户变量", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    
                    string oldName = isEdit ? targetLv.SelectedItems[0].Text : "";
                    string oldVal = isEdit ? targetLv.SelectedItems[0].SubItems[1].Text : "";
                    
                    using(Form f = new Form() { Text = isEdit ? "编辑变量" : "添加变量", Size = new Size(400, 250), StartPosition = FormStartPosition.CenterParent, BackColor = ModernTheme.BgCard, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false }) {
                        int currentY = 20;

                        Label l1 = new Label { Text = string.Format("变量名 [{0}]:", targetUserName), ForeColor = ModernTheme.TextPrimary, Location = new Point(20, currentY), AutoSize = true };
                        TextBox t1 = new TextBox { Location = new Point(20, currentY + 25), Size = new Size(340, 25), Text = oldName, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.FixedSingle };
                        currentY += 65;
                        
                        Label l2 = new Label { Text = "变量值:", ForeColor = ModernTheme.TextPrimary, Location = new Point(20, currentY), AutoSize = true };
                        TextBox t2 = new TextBox { Location = new Point(20, currentY + 25), Size = new Size(340, 25), Text = oldVal, BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.FixedSingle };
                        currentY += 50;

                        Button bOk = new Button { Text = "确定", Location = new Point(160, currentY), Size = new Size(80, 30), BackColor = ModernTheme.Primary, ForeColor = ModernTheme.TextPrimary, FlatStyle = FlatStyle.Flat };
                        Button bCancel = new Button { Text = "取消", Location = new Point(260, currentY), Size = new Size(80, 30), BackColor = Color.FromArgb(71, 85, 105), ForeColor = ModernTheme.TextPrimary, FlatStyle = FlatStyle.Flat };
                        
                        bCancel.Click += (ss, ee) => f.DialogResult = DialogResult.Cancel;
                        bOk.Click += (ss, ee) => {
                            string n = t1.Text.Trim(); string v = t2.Text.Trim();
                            if (string.IsNullOrEmpty(n)) { MessageBox.Show("变量名不能为空"); return; }
                            
                            Task.Run(() => {
                                try {
                                    SetLabelSafe(lblStatus, "正在保存...");
                                    ManagementScope scope = GetWmiScope(pc, cred, @"root\default");
                                    scope.Connect();
                                    ManagementClass regClass = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                                    
                                    uint root = isSystem ? (uint)0x80000002 : (uint)0x80000003;
                                    string path = isSystem ? @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment" : targetSid + @"\Environment";
                                    
                                    if (isEdit && oldName != n) {
                                        ManagementBaseObject delParams = regClass.GetMethodParameters("DeleteValue");
                                        delParams["hDefKey"] = root; delParams["sSubKeyName"] = path; delParams["sValueName"] = oldName;
                                        regClass.InvokeMethod("DeleteValue", delParams, null);
                                    }
                                    
                                    string method = v.Contains("%") ? "SetExpandedStringValue" : "SetStringValue";
                                    ManagementBaseObject inParams = regClass.GetMethodParameters(method);
                                    inParams["hDefKey"] = root; inParams["sSubKeyName"] = path; inParams["sValueName"] = n; inParams["sValue"] = v;
                                    regClass.InvokeMethod(method, inParams, null);
                                    
                                    SetLabelSafe(lblStatus, "保存成功 (部分应用需重启生效)");
                                    loadAction();
                                } catch (Exception ex) { SetLabelSafe(lblStatus, "保存失败: " + ex.Message); }
                            });
                            f.DialogResult = DialogResult.OK;
                        };
                        f.Controls.AddRange(new Control[] { l1, t1, l2, t2, bOk, bCancel });
                        f.ShowDialog();
                    }
                };
                
                btnAdd.Click += (s, e) => addOrEditAction(false);
                btnEdit.Click += (s, e) => addOrEditAction(true);
                lvSystem.MouseDoubleClick += (s, e) => addOrEditAction(true);
                
                btnDelete.Click += (s, e) => {
                    bool isSystem = tabControl.SelectedIndex == 0;
                    TabPage currentTab = tabControl.SelectedTab;
                    ListView targetLv = currentTab.Controls.OfType<ListView>().FirstOrDefault();
                    if (targetLv == null) return;

                    string targetSid = isSystem ? "" : (currentTab.Tag as string ?? "");
                    string targetUserName = isSystem ? "系统" : currentTab.Text.Replace("用户变量 (", "").TrimEnd(')');

                    if (targetLv.SelectedItems.Count == 0) { MessageBox.Show("请先选择一项", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    if (!isSystem && string.IsNullOrEmpty(targetSid)) { return; }
                    
                    string name = targetLv.SelectedItems[0].Text;

                    if (MessageBox.Show(string.Format("确定要删除 {0} 的变量 [{1}] 吗？\n注意：误删核心变量可能导致系统或软件故障！", targetUserName, name), "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
                        Task.Run(() => {
                            try {
                                SetLabelSafe(lblStatus, "正在删除...");
                                ManagementScope scope = GetWmiScope(pc, cred, @"root\default");
                                scope.Connect();
                                ManagementClass regClass = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                                
                                uint root = isSystem ? (uint)0x80000002 : (uint)0x80000003;
                                string path = isSystem ? @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment" : targetSid + @"\Environment";
                                
                                ManagementBaseObject delParams = regClass.GetMethodParameters("DeleteValue");
                                delParams["hDefKey"] = root; delParams["sSubKeyName"] = path; delParams["sValueName"] = name;
                                regClass.InvokeMethod("DeleteValue", delParams, null);
                                
                                SetLabelSafe(lblStatus, "删除成功 (部分应用需重启生效)");
                                loadAction();
                            } catch (Exception ex) { SetLabelSafe(lblStatus, "删除失败: " + ex.Message); }
                        });
                    }
                };

                dialog.ShowDialog();
            }
        }

        private ListView CreateEnvListView() {
            ListView lv = new ListView {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false,
                BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary,
                Font = new Font("Segoe UI", 9.5f), BorderStyle = BorderStyle.None
            };
            lv.Columns.Add("变量", 200);
            lv.Columns.Add("值", 530);
            lv.Resize += (s, e) => {
                if (lv.Columns.Count >= 2) {
                    int w = lv.ClientSize.Width - lv.Columns[0].Width;
                    if (w > 50) lv.Columns[1].Width = w;
                }
            };
            return lv;
        }
        #endregion

        #region 网络功能
        private void ContinuousPing(string target) { 
            int count = 0, success = 0; 
            AppendResult(">>> 开始 Ping " + target + " (再次点击按钮结束)", ModernTheme.Info); 
            while (_continuePing) { 
                count++; 
                try { 
                    using (Ping ping = new Ping()) { 
                        PingReply r = ping.Send(target, 1000); 
                        if (r.Status == IPStatus.Success) { 
                            success++; 
                            string ttl = r.Options != null ? r.Options.Ttl.ToString() : "N/A";
                            AppendResult(string.Format("[{0}] 来自 {1} 的回复: 时间={2}ms TTL={3}", count, r.Address, r.RoundtripTime, ttl), ModernTheme.Success); 
                        } 
                        else AppendResult(string.Format("[{0}] 请求超时", count), ModernTheme.Warning); 
                    } 
                } 
                catch (Exception ex) { 
                    string errMsg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    AppendResult(string.Format("[{0}] 错误: {1}", count, errMsg), ModernTheme.Danger); 
                } 
                Thread.Sleep(1000); 
            } 
            AppendResult(string.Format(">>> Ping 已停止 | 总计={0}, 成功={1}, 丢失={2}", count, success, count - success), ModernTheme.Info); 
        }
        
        private void QuickTracert() { string target = GetTargetComputer(); if (string.IsNullOrEmpty(target)) { AppendResult("✗ 请输入目标地址", ModernTheme.Danger); return; } 
            string ipToTrace = target; try { IPAddress parsedIp; if (!IPAddress.TryParse(target, out parsedIp) || parsedIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) { foreach (var addr in Dns.GetHostAddresses(target)) { if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { ipToTrace = addr.ToString(); break; } } } } catch { }
            SetStatus("路由追踪 " + target, true); AppendResult("> 路由追踪 " + target + (target != ipToTrace ? " [" + ipToTrace + "]" : ""), ModernTheme.TextPrimary); 
            for (int i = 1; i <= 30; i++) { try { using (Ping ping = new Ping()) { PingOptions options = new PingOptions(); options.Ttl = i; 
                byte[] buffer = Encoding.UTF8.GetBytes("t"); PingReply p = ping.Send(ipToTrace, 1000, buffer, options); 
                string addrStr = p.Address != null ? p.Address.ToString() : "*";
                AppendResult(string.Format("  {0,2}. {1} [{2}ms]", i, addrStr, p.RoundtripTime), ModernTheme.TextSecondary); 
                if (p.Status == IPStatus.Success || addrStr == ipToTrace) break; } } catch { AppendResult(string.Format("  {0,2}. * 超时", i), ModernTheme.Warning); } } 
            AppendResult("> 完成", ModernTheme.Success); SetStatus("就绪", false); }
        
        private void QuickDNS(string domain) 
        { 
            SetStatus("DNS查询 " + domain, true); 
            AppendResult("> DNS查询 " + domain, ModernTheme.TextPrimary); 
            try 
            { 
                foreach (var ip in Dns.GetHostAddresses(domain)) 
                { 
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) 
                        AppendResult("  ✓ " + ip, ModernTheme.Success); 
                } 
            } 
            catch (Exception ex) 
            { 
                AppendResult("  ✗ 错误: " + ex.Message, ModernTheme.Danger); 
            } 
            AppendResult("> 完成", ModernTheme.Success); 
            SetStatus("就绪", false); 
        }
        
        private void FullPortScan(string ip)
        {
            if (string.IsNullOrEmpty(ip)) ip = "127.0.0.1";
            
            SetStatus("端口扫描 " + ip, true);
            AppendResult("> 端口扫描 " + ip, ModernTheme.TextPrimary);
            
            int[] ports = { 21, 22, 23, 25, 53, 80, 110, 135, 139, 143, 443, 445, 993, 995, 1433, 3306, 3389, 5432, 5900, 8080, 8443 };
            string[] portNames = { 
                "FTP", "SSH", "Telnet", "SMTP", "DNS", "HTTP", "POP3", "RPC", "NetBIOS",
                "IMAP", "HTTPS", "SMB", "IMAPS", "POP3S", "MSSQL", "MySQL", "RDP", "PostgreSQL", "VNC", "HTTP-Alt", "HTTPS-Alt"
            };
            
            AppendResult(string.Format("  正在扫描 {0} 个常用端口...", ports.Length), ModernTheme.Info);
            
            int openCount = 0;
            var openPortsList = new List<string>();
            
            for (int i = 0; i < ports.Length; i++)
            {
                try
                {
                    using (var client = new System.Net.Sockets.TcpClient())
                    {
                        var result = client.BeginConnect(ip, ports[i], null, null);
                        bool success = result.AsyncWaitHandle.WaitOne(500, false);
                        if (success)
                        {
                            client.EndConnect(result);
                            AppendResult(string.Format("  ✓ 端口 {0,5} ({1}) 开放", ports[i], portNames[i]), ModernTheme.Success);
                            openCount++;
                            openPortsList.Add(string.Format("{0} ({1})", ports[i], portNames[i]));
                        }
                    }
                }
                catch { }
                
                if ((i + 1) % 5 == 0)
                {
                    AppendResult(string.Format("  进度: {0}/{1}", i + 1, ports.Length), ModernTheme.TextMuted);
                }
            }
            
            AppendResult("", ModernTheme.TextPrimary);
            AppendResult(string.Format("  扫描完成，找到 {0} 个开放端口", openCount), openCount > 0 ? ModernTheme.Success : ModernTheme.Warning);
            if (openCount > 0)
                AppendResult("  开放端口: " + string.Join(", ", openPortsList), ModernTheme.Info);
            AppendResult("> 完成", ModernTheme.Success);
            SetStatus("就绪", false);
        }
        
        private void QuickFlushDNS() { SetStatus("刷新DNS缓存", true); AppendResult("> 刷新DNS缓存", ModernTheme.TextPrimary); 
            try { Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit(); 
                AppendResult("  ✓ DNS缓存已刷新", ModernTheme.Success); } catch (Exception ex) { AppendResult("  ✗ 失败: " + ex.Message, ModernTheme.Danger); } 
            AppendResult("> 完成", ModernTheme.Success); SetStatus("就绪", false); }
        
        private void ShowLocalInfo() { SetStatus("获取本机信息", true); AppendResult("> 本机信息", ModernTheme.TextPrimary); AppendResult("", ModernTheme.TextPrimary); 
            AppendResult("  【系统信息】", ModernTheme.Info); AppendResult("    主机名: " + Environment.MachineName, ModernTheme.TextSecondary); 
            AppendResult("    操作系统: " + Environment.OSVersion, ModernTheme.TextSecondary); 
            AppendResult("    64位系统: " + (Environment.Is64BitOperatingSystem ? "是" : "否"), ModernTheme.TextSecondary); AppendResult("", ModernTheme.TextPrimary); 
            AppendResult("  【网络适配器】", ModernTheme.Info); foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces()) 
            { if (ni.OperationalStatus == OperationalStatus.Up) { AppendResult("    " + ni.Name, ModernTheme.Success); 
                AppendResult("      类型: " + ni.NetworkInterfaceType, ModernTheme.TextSecondary); 
                AppendResult("      速度: " + (ni.Speed / 1000000) + " Mbps", ModernTheme.TextSecondary); 
                IPInterfaceProperties ip = ni.GetIPProperties(); foreach (UnicastIPAddressInformation addr in ip.UnicastAddresses) 
                { if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) 
                { AppendResult("      IPv4: " + addr.Address, ModernTheme.Success); if (addr.IPv4Mask != null) AppendResult("      掩码: " + addr.IPv4Mask, ModernTheme.TextSecondary); } } 
                foreach (GatewayIPAddressInformation gateway in ip.GatewayAddresses) { if (gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) 
                    AppendResult("      网关: " + gateway.Address, ModernTheme.TextSecondary); } 
                foreach (IPAddress dns in ip.DnsAddresses) { if (dns.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) 
                    AppendResult("      DNS: " + dns, ModernTheme.TextSecondary); } AppendResult("", ModernTheme.TextPrimary); } } 
            AppendResult("> 完成", ModernTheme.Success); SetStatus("就绪", false); }
        #endregion

        #region 远程连接功能
        private void ConnectShareAsync(string share, string name) { 
            if (_isConnecting) { AppendResult("! 正在连接中，请稍后...", ModernTheme.Warning); return; } 
            string pc = GetTargetComputer(); 
            
            if (IsLocalMachine(pc)) {
                string localPath = share.Replace("$", ":\\");
                if (share.ToLower().Contains("desktop")) { localPath = @"C:\Users\Public\Desktop"; }
                AppendResult("> 访问本地 " + name + " (" + localPath + ")", ModernTheme.TextPrimary);
                try { Process.Start("explorer.exe", localPath); } catch (Exception ex) { AppendResult("  ✗ 失败: " + ex.Message, ModernTheme.Danger); }
                return;
            }

            string path = @"\\" + pc + @"\" + share; SetStatus("连接 " + name + "...", true); AppendResult("> 连接 " + name + " (" + path + ")", ModernTheme.TextPrimary); 
            _isConnecting = true; Task.Run(() => { try { var cred = GetCred(); if (cred == null) { AppendResult("  ✗ 未获取到凭证", ModernTheme.Danger); return; } 
                NETRESOURCE nr = new NETRESOURCE(); nr.dwType = 1; nr.lpRemoteName = path; 
                string fullUser = !string.IsNullOrEmpty(cred.Domain) ? cred.Domain + "\\" + cred.UserName : cred.UserName; 
                int ret = WNetAddConnection2(nr, cred.Password, fullUser, 0); if (ret == 0) { lock (_connectedShares) { _connectedShares.Add(path); } 
                AppendResult("  ✓ 已成功连接到 " + name, ModernTheme.Success); Process.Start("explorer.exe", path); } 
                else { 
                    AppendResult("  " + GetConnectionErrorStr(ret, pc, path), ModernTheme.Danger); 
                } } 
                catch (Exception ex) { AppendResult("  ✗ 异常: " + ex.Message, ModernTheme.Danger); } finally { _isConnecting = false; SetStatus("就绪", false); } }); }
        
        private NetworkCredential GetCred() { if (_currentCred != null) return _currentCred; string savedUser, savedPwd; 
            if (LoadCredentialFromFile(out savedUser, out savedPwd) && !string.IsNullOrEmpty(savedUser) && !string.IsNullOrEmpty(savedPwd)) 
            { string domain = "", user = savedUser; if (user.Contains("\\")) { string[] parts = user.Split('\\'); domain = parts[0]; user = parts[1]; } 
            _currentCred = new NetworkCredential(user, savedPwd, domain); _currentPassword = savedPwd; return _currentCred; } 
            return ShowCredentialDialog("请输入管理员账号"); }
        
        private NetworkCredential ShowCredentialDialog(string title) { using (var d = new Form() { Text = title, Size = new Size(420, 260), 
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.CenterParent, BackColor = ModernTheme.BgCard }) 
            { Panel titleBar = new Panel { Dock = DockStyle.Top, Height = 35, BackColor = ModernTheme.BgSecondary }; 
            Label titleLabel = new Label { Text = title, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = ModernTheme.TextPrimary, 
                Location = new Point(15, 8), Size = new Size(300, 20) }; 
            Label btnCloseDialog = new Label { Text = "✕", Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = ModernTheme.TextMuted, 
                Location = new Point(d.Width - 40, 5), Size = new Size(30, 25), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand }; 
            btnCloseDialog.Click += (s, e) => d.DialogResult = DialogResult.Cancel; 
            btnCloseDialog.MouseEnter += (s, e) => { btnCloseDialog.BackColor = ModernTheme.Danger; btnCloseDialog.ForeColor = Color.White; }; 
            btnCloseDialog.MouseLeave += (s, e) => { btnCloseDialog.BackColor = Color.Transparent; btnCloseDialog.ForeColor = ModernTheme.TextMuted; }; 
            titleBar.Controls.Add(titleLabel); titleBar.Controls.Add(btnCloseDialog); 
            Label lblDomain = new Label() { Text = "域名", ForeColor = ModernTheme.TextSecondary, Location = new Point(30, 55), Size = new Size(60, 25) }; 
            TextBox tDomain = new TextBox() { Location = new Point(100, 55), Size = new Size(260, 25), Text = "CONTOSO", BackColor = ModernTheme.BgSecondary, 
                ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.FixedSingle }; 
            Label lblUser = new Label() { Text = "用户名", ForeColor = ModernTheme.TextSecondary, Location = new Point(30, 95), Size = new Size(60, 25) }; 
            TextBox tUser = new TextBox() { Location = new Point(100, 95), Size = new Size(260, 25), BackColor = ModernTheme.BgSecondary, 
                ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.FixedSingle }; 
            Label lblPwd = new Label() { Text = "密码", ForeColor = ModernTheme.TextSecondary, Location = new Point(30, 135), Size = new Size(60, 25) }; 
            TextBox tPwd = new TextBox() { Location = new Point(100, 135), Size = new Size(260, 25), PasswordChar = '●', BackColor = ModernTheme.BgSecondary, 
                ForeColor = ModernTheme.TextPrimary, BorderStyle = BorderStyle.FixedSingle }; 
            CheckBox chkSave = new CheckBox() { Text = "保存凭证", ForeColor = ModernTheme.TextSecondary, Location = new Point(100, 175), Size = new Size(100, 25), Checked = true }; 
            ModernButton btnOk = new ModernButton() { Text = "确定", ButtonColor = ModernTheme.Success, Location = new Point(100, 210), Size = new Size(100, 35) }; 
            btnOk.Click += (s, e) => d.DialogResult = DialogResult.OK; 
            ModernButton btnCancel = new ModernButton() { Text = "取消", ButtonColor = ModernTheme.Danger, Location = new Point(220, 210), Size = new Size(100, 35) }; 
            btnCancel.Click += (s, e) => d.DialogResult = DialogResult.Cancel; 
            d.Controls.AddRange(new Control[] { titleBar, lblDomain, tDomain, lblUser, tUser, lblPwd, tPwd, chkSave, btnOk, btnCancel }); 
            d.Paint += (s, e) => { using (Pen pen = new Pen(ModernTheme.Border, 1)) { e.Graphics.DrawRectangle(pen, 0, 0, d.Width - 1, d.Height - 1); } }; 
            if (d.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(tUser.Text)) { string domain = tDomain.Text.Trim(); string user = tUser.Text.Trim(); 
                string pwd = tPwd.Text; string fullUser = domain + "\\" + user; if (chkSave.Checked) SaveCredential(fullUser, pwd); 
                _currentCred = new NetworkCredential(user, pwd, domain); _currentPassword = pwd; return _currentCred; } } return null; }
        
        private void DisconnectAll() { int c = 0; lock (_connectedShares) { foreach (var s in _connectedShares.ToArray()) 
            { try { if (WNetCancelConnection2(s, 0, true) == 0) { c++; _connectedShares.Remove(s); } } catch { } } } 
            AppendResult(string.Format("✓ 已断开 {0} 个软件建立的连接", c), ModernTheme.Success); }
        
        private void MainWindow_FormClosing(object sender, FormClosingEventArgs e) { _continuePing = false; Thread.Sleep(500); DisconnectAll(); }
        #endregion

        #region DWRCC 功能
        private void RunDWRCC() { string pc = GetTargetComputer(); 
            if (IsLocalMachine(pc)) { AppendResult("✗ DWRCC 无法控制本机", ModernTheme.Warning); return; }
            string dwrccPath = @"C:\Program Files\SolarWinds\Dameware Mini Remote Control x64\DWRCC.exe"; 
            if (!File.Exists(dwrccPath)) { AppendResult("✗ 未找到 DWRCC.exe", ModernTheme.Danger); AppendResult("  期望路径: " + dwrccPath, ModernTheme.TextMuted); return; } 
            AppendResult("> 启动 DWRCC 远程连接", ModernTheme.TextPrimary); AppendResult("  目标电脑: " + pc, ModernTheme.TextMuted); 
            try { var cred = GetCredForPsExec(); if (cred == null) { AppendResult("  ✗ 未获取到凭证或用户取消", ModernTheme.Danger); return; } 
                string domain = !string.IsNullOrEmpty(cred.Domain) ? cred.Domain : "CONTOSO"; string userName = cred.UserName; string password = _currentPassword; 
                if (string.IsNullOrEmpty(password)) { AppendResult("  ✗ 密码为空，请重新输入凭证", ModernTheme.Danger); _currentCred = null; _currentPassword = ""; 
                    cred = ShowCredentialDialog("请输入管理员账号 (DWRCC)"); if (cred == null) { AppendResult("  ✗ 未获取到凭证", ModernTheme.Danger); return; } 
                    domain = !string.IsNullOrEmpty(cred.Domain) ? cred.Domain : "CONTOSO"; userName = cred.UserName; password = _currentPassword; } 
                string arguments = string.Format("-h -c -m:{0} -u:{1} -p:{2} -d:{3}", pc, userName, password, domain); 
                AppendResult(string.Format("  ✓ 已启动 DWRCC，正在连接 {0}...", pc), ModernTheme.Success); 
                AppendResult("  提示：如果远程电脑需要确认，请在目标电脑上批准连接", ModernTheme.Info); 
                Process.Start(new ProcessStartInfo { FileName = dwrccPath, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true }); } 
            catch (Exception ex) { AppendResult("  ✗ 启动失败: " + ex.Message, ModernTheme.Danger); } }
        #endregion

       #region 拖放运行功能
private void UploadAndRunRemoteProgramWithCredential(string[] files, string fileName)
{
    Task.Run(() =>
    {
        try
        {
            string pc = GetTargetComputer();
            bool isLocal = IsLocalMachine(pc);
            string remotePath = isLocal ? @"C:\Temp" : @"\\" + pc + @"\C$\Temp";
            
            AppendResult("", ModernTheme.TextPrimary);
            AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
            AppendResult("【程序运行】准备执行: " + fileName, ModernTheme.Info);
            AppendResult("目标电脑: " + pc, ModernTheme.TextSecondary);
            
            NetworkCredential cred = null;
            if (!isLocal) {
                cred = GetCredForPsExec();
                if (cred == null) { AppendResult("✗ 未获取到凭证", ModernTheme.Danger); return; }
                if (string.IsNullOrEmpty(_currentPassword)) {
                    AppendResult("✗ 密码为空，请重新输入凭证", ModernTheme.Danger);
                    _currentCred = null; _currentPassword = "";
                    cred = ShowCredentialDialog("请输入管理员账号");
                    if (cred == null) { AppendResult("✗ 未获取到凭证", ModernTheme.Danger); return; }
                }
            }
            
            if (!isLocal && !IsPathConnected(remotePath))
            {
                AppendResult("正在建立远程连接...", ModernTheme.Info);
                NETRESOURCE nr = new NETRESOURCE(); nr.dwType = 1; nr.lpRemoteName = remotePath;
                string fullUser = !string.IsNullOrEmpty(cred.Domain) ? cred.Domain + "\\" + cred.UserName : cred.UserName;
                int ret = WNetAddConnection2(nr, cred.Password, fullUser, 0);
                if (ret != 0) {
                    AppendResult("  " + GetConnectionErrorStr(ret, pc, remotePath), ModernTheme.Danger); return;
                }
                lock (_connectedShares) { _connectedShares.Add(remotePath); }
                AppendResult("✓ 远程连接已建立", ModernTheme.Success);
            } else if (isLocal) {
                AppendResult("✓ 识别为本机，免密操作...", ModernTheme.Success);
            }
            
            if (isLocal && !Directory.Exists(remotePath)) {
                try { Directory.CreateDirectory(remotePath); } catch { }
            }
            
            string destPath = Path.Combine(remotePath, fileName);
            AppendResult(string.Format("正在加载文件: {0}", fileName), ModernTheme.Info);
            
            bool copySuccess = false;
            try
            {
                File.Copy(files[0], destPath, true);
                copySuccess = true;
                AppendResult("✓ 文件就绪", ModernTheme.Success);
            }
            catch (Exception ex)
            {
                AppendResult("✗ 文件加载失败: " + ex.Message, ModernTheme.Danger);
            }
            
            if (copySuccess)
            {
                if (isLocal) {
                    this.Invoke((Action)(() => AppendResult("正在请求 UAC 提权并启动本机程序...", ModernTheme.Info)));
                    ProcessStartInfo localPsi = new ProcessStartInfo {
                        FileName = destPath,
                        UseShellExecute = true,
                        Verb = "runas"
                    };

                    try {
                        using (Process p = Process.Start(localPsi)) {
                            // 对于 UI 程序，一般不需要等待退出
                            this.Invoke((Action)(() => { AppendResult("  ✓ 程序已启动 (原生弹出)", ModernTheme.Success); }));
                        }
                    } catch (System.ComponentModel.Win32Exception) {
                        this.Invoke((Action)(() => AppendResult("  ✗ 操作取消: 用户拒绝了 UAC 提权", ModernTheme.Danger)));
                    }
                    AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                    return;
                }

                // 远端 PsExec 逻辑...
                AppendResult("正在提升权限并执行程序...", ModernTheme.Info);
                string remoteFilePath = @"C:\Temp\" + fileName;
                
                // 专门针对 .ps1 和 .vbs 优化底层执行命令
                string fileExt = Path.GetExtension(fileName).ToLower();
                string execCommand = "\"" + remoteFilePath + "\"";
                if (fileExt == ".ps1") execCommand = "powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + remoteFilePath + "\"";
                else if (fileExt == ".vbs") execCommand = "cscript.exe //nologo //B \"" + remoteFilePath + "\"";
                else if (fileExt == ".bat" || fileExt == ".cmd") execCommand = "cmd.exe /c \"" + remoteFilePath + "\"";
                
                int sessionId = GetActiveSessionId(pc);
                bool isGui = IsGuiProgram(fileName);
                
                string domain = !string.IsNullOrEmpty(cred.Domain) ? cred.Domain : ".";
                string fullUser = domain + "\\" + cred.UserName;
                
                string psexecArgs = isGui ? 
                    string.Format(@"\\{0} -u ""{1}"" -p ""{2}"" -i {3} -h -s -d -accepteula cmd.exe /c start """" {4}", pc, fullUser, _currentPassword, sessionId, execCommand) :
                    string.Format(@"\\{0} -u ""{1}"" -p ""{2}"" -i {3} -h -s -accepteula {4}", pc, fullUser, _currentPassword, sessionId, execCommand);
                
                AppendResult(string.Format("  使用凭证: {0}", fullUser), ModernTheme.Info);
                AppendResult(string.Format("  会话 ID: {0}", sessionId), ModernTheme.TextSecondary);
                if (isGui) AppendResult("  检测到图形界面程序，将以后台模式启动", ModernTheme.Info);
                
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = _psExecPath,
                    Arguments = psexecArgs,
                    WorkingDirectory = Environment.SystemDirectory,
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    UserName = cred.UserName, Password = GetSecureString(_currentPassword), Domain = cred.Domain
                };
                
                using (Process process = new Process())
                {
                    process.StartInfo = psi;
                    StringBuilder outputBuilder = new StringBuilder();
                    
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data) && !e.Data.Contains("PsExec v") && !e.Data.Contains("Copyright") && !e.Data.Contains("Sysinternals"))
                            this.Invoke((Action)(() => AppendResult("  " + e.Data, ModernTheme.TextSecondary)));
                    };
                    
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data) && !e.Data.Contains("PsExec v") && !e.Data.Contains("Copyright") && !e.Data.Contains("Sysinternals") && !e.Data.Contains("Connecting") && !e.Data.Contains("Starting") && !e.Data.Contains("Copying"))
                            this.Invoke((Action)(() => AppendResult("  [信息] " + e.Data, ModernTheme.Info)));
                    };
                    
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    
                    if (!isGui)
                    {
                        DateTime startTime = DateTime.Now;
                        process.WaitForExit();
                        TimeSpan elapsed = DateTime.Now - startTime;
                        int exitCode = process.ExitCode;
                        int remotePid = ParseRemotePidFromOutput(outputBuilder.ToString());
                        
                        this.Invoke((Action)(() => {
                            AppendResult("", ModernTheme.TextPrimary);
                            AppendResult(string.Format("  执行完成，耗时: {0:0.00} 秒", elapsed.TotalSeconds), ModernTheme.Info);
                            if (remotePid > 0) AppendResult("  PID: " + remotePid, ModernTheme.Success);
                            AppendResult("  退出代码: " + exitCode, exitCode == 0 ? ModernTheme.Success : ModernTheme.Warning);
                        }));
                    }
                    else
                    {
                        this.Invoke((Action)(() => { AppendResult("  ✓ 程序已启动", ModernTheme.Success); }));
                        Thread.Sleep(2000);
                    }
                }
            }
            AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
        }
        catch (Exception ex)
        {
            AppendResult("运行失败: " + ex.Message, ModernTheme.Danger);
        }
    });
}
#endregion

        #region 远程软件管理功能
        private void ManageRemotePrograms()
        {
            string pc = GetTargetComputer();
            bool isLocal = IsLocalMachine(pc);
            
            NetworkCredential cred = null;
            if (!isLocal) {
                cred = GetCredForWMI();
                if (cred == null) { AppendResult("✗ 未获取到凭证", ModernTheme.Danger); return; }
            }
            
            using (Form dialog = new Form())
            {
                dialog.Text = "远程软件管理 - " + pc;
                dialog.Size = new Size(850, 650);
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.BackColor = ModernTheme.BgCard;
                dialog.FormBorderStyle = FormBorderStyle.Sizable;
                dialog.MaximizeBox = true;
                dialog.MinimizeBox = true;
                
                try { dialog.Icon = this.Icon; } catch { }
                
                Panel titleBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = ModernTheme.BgSecondary };
                Label titleLabel = new Label { Text = "远程软件管理 - " + pc + (isLocal ? " (本机)" : ""), Font = new Font("Segoe UI", 12f, FontStyle.Bold), 
                    ForeColor = ModernTheme.TextPrimary, Location = new Point(15, 10), Size = new Size(400, 25) };
                titleBar.Controls.Add(titleLabel);
                
                Panel searchPanel = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = ModernTheme.BgCard };
                Label lblSearch = new Label { Text = "搜索:", ForeColor = ModernTheme.TextSecondary, Location = new Point(15, 10), Size = new Size(40, 25) };
                TextBox txtSearch = new TextBox 
                { 
                    Location = new Point(55, 8), Size = new Size(250, 25), 
                    BackColor = ModernTheme.BgSecondary, ForeColor = ModernTheme.TextPrimary,
                    BorderStyle = BorderStyle.FixedSingle
                };
                searchPanel.Controls.Add(lblSearch);
                searchPanel.Controls.Add(txtSearch);
                
                Panel buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 55, BackColor = ModernTheme.BgSecondary };
                ModernButton btnRefresh = new ModernButton { Text = "刷新列表", ButtonColor = ModernTheme.Info, 
                    Location = new Point(10, 10), Size = new Size(100, 35) };
                ModernButton btnUninstallSelected = new ModernButton { Text = "卸载选中", ButtonColor = ModernTheme.Danger, 
                    Location = new Point(120, 10), Size = new Size(100, 35) };
                ModernButton btnExport = new ModernButton { Text = "导出列表", ButtonColor = ModernTheme.Success, 
                    Location = new Point(230, 10), Size = new Size(100, 35) };
                ModernButton btnCloseDialog = new ModernButton { Text = "关闭", ButtonColor = Color.FromArgb(71, 85, 105), 
                    Location = new Point(730, 10), Size = new Size(100, 35), Anchor = AnchorStyles.Top | AnchorStyles.Right };
                btnCloseDialog.Click += (s, e) => dialog.Close();
                buttonPanel.Controls.AddRange(new Control[] { btnRefresh, btnUninstallSelected, btnExport, btnCloseDialog });
                
                ListView lvPrograms = new ListView
                {
                    Dock = DockStyle.Fill,
                    View = View.Details,
                    FullRowSelect = true,
                    GridLines = false,
                    BackColor = ModernTheme.BgSecondary,
                    ForeColor = ModernTheme.TextPrimary,
                    Font = new Font("Segoe UI", 9f)
                };
                lvPrograms.Columns.Add("序号", 50);
                lvPrograms.Columns.Add("程序名称", 320);
                lvPrograms.Columns.Add("版本", 100);
                lvPrograms.Columns.Add("发布者", 150);
                lvPrograms.Columns.Add("安装日期", 100);
                lvPrograms.Columns.Add("大小(MB)", 80);
                
                lvPrograms.Resize += (s, e) => {
                    if (lvPrograms.Columns.Count >= 6) {
                        int fixedWidth = lvPrograms.Columns[0].Width + lvPrograms.Columns[2].Width + lvPrograms.Columns[4].Width + lvPrograms.Columns[5].Width;
                        int available = lvPrograms.ClientSize.Width - fixedWidth;
                        if (available > 200) {
                            int nameWidth = available / 2;
                            if (nameWidth > 450) nameWidth = 450;
                            else if (nameWidth < 250) nameWidth = 250;
                            
                            lvPrograms.Columns[1].Width = nameWidth;
                            lvPrograms.Columns[3].Width = available - nameWidth;
                        }
                    }
                };

                ProgressBar progressBar = new ProgressBar
                {
                    Dock = DockStyle.Bottom,
                    Height = 5,
                    Style = ProgressBarStyle.Marquee,
                    Visible = false
                };
                
                Label lblStatus = new Label
                {
                    Dock = DockStyle.Bottom,
                    Height = 25,
                    BackColor = ModernTheme.BgSecondary,
                    ForeColor = ModernTheme.TextSecondary,
                    Text = "就绪",
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(5, 0, 0, 0)
                };
                
                dialog.Controls.Add(lvPrograms);
                dialog.Controls.Add(searchPanel);
                dialog.Controls.Add(buttonPanel);
                dialog.Controls.Add(titleBar);
                dialog.Controls.Add(progressBar);
                dialog.Controls.Add(lblStatus);
                
                List<ListViewItem> allItems = new List<ListViewItem>();
                
                txtSearch.TextChanged += (s, e) =>
                {
                    string searchText = txtSearch.Text.ToLower();
                    lvPrograms.BeginUpdate();
                    lvPrograms.Items.Clear();
                    int visibleCount = 0;
                    foreach (var item in allItems)
                    {
                        if (string.IsNullOrEmpty(searchText) || item.SubItems[1].Text.ToLower().Contains(searchText))
                        {
                            lvPrograms.Items.Add(item);
                            visibleCount++;
                        }
                    }
                    lvPrograms.EndUpdate();
                    lblStatus.Text = string.Format("显示 {0} / 共 {1} 个程序", visibleCount, allItems.Count);
                };
                
                btnRefresh.Click += (s, e) =>
                {
                    txtSearch.Text = "";
                    allItems.Clear();
                    Task.Run(() => LoadRemoteProgramsRegistry(pc, cred, lvPrograms, progressBar, lblStatus, allItems));
                };
                
                btnUninstallSelected.Click += (s, e) =>
                {
                    if (lvPrograms.SelectedItems.Count == 0)
                    {
                        MessageBox.Show("请先选择要卸载的程序", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    
                    string programName = lvPrograms.SelectedItems[0].SubItems[1].Text;
                    string uninstallString = lvPrograms.SelectedItems[0].Tag as string ?? "";
                    
                    if (string.IsNullOrEmpty(uninstallString))
                    {
                        MessageBox.Show("该程序没有卸载信息，无法卸载", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    
                    DialogResult result = MessageBox.Show(string.Format("确定要卸载以下程序吗？\n\n{0}\n\n卸载命令:\n{1}\n\n此操作将执行卸载命令，请确认！", 
                        programName, uninstallString), "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    
                    if (result == DialogResult.Yes)
                    {
                        btnUninstallSelected.Enabled = false;
                        btnRefresh.Enabled = false;
                        
                        Task.Run(() => {
                            UninstallRemoteProgram(pc, cred, uninstallString, programName, lblStatus, btnRefresh);
                            btnUninstallSelected.Invoke((Action)(() => {
                                btnUninstallSelected.Enabled = true;
                                btnRefresh.Enabled = true;
                            }));
                        });
                    }
                };
                
                lvPrograms.MouseDoubleClick += (s, e) =>
                {
                    ListViewHitTestInfo hit = lvPrograms.HitTest(e.Location);
                    if (hit.Item != null)
                    {
                        string programName = hit.Item.SubItems[1].Text;
                        string uninstallString = hit.Item.Tag as string ?? "";
                        
                        if (string.IsNullOrEmpty(uninstallString))
                        {
                            MessageBox.Show("该程序没有卸载信息", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            using (Form detailDialog = new Form())
                            {
                                detailDialog.Text = "卸载信息 - " + programName;
                                detailDialog.Size = new Size(700, 350);
                                detailDialog.StartPosition = FormStartPosition.CenterParent;
                                detailDialog.BackColor = ModernTheme.BgCard;
                                detailDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                                detailDialog.MaximizeBox = false;
                                detailDialog.MinimizeBox = false;
                                
                                Label lblInfo = new Label
                                {
                                    Text = string.Format("程序名称: {0}\n\n卸载命令:", programName),
                                    Location = new Point(15, 15),
                                    Size = new Size(650, 60),
                                    ForeColor = ModernTheme.TextPrimary,
                                    Font = new Font("Segoe UI", 10f)
                                };
                                
                                TextBox txtUninstall = new TextBox
                                {
                                    Location = new Point(15, 90),
                                    Size = new Size(650, 100),
                                    Multiline = true,
                                    ScrollBars = ScrollBars.Vertical,
                                    Text = uninstallString,
                                    BackColor = ModernTheme.BgSecondary,
                                    ForeColor = ModernTheme.TextPrimary,
                                    Font = new Font("Consolas", 9f),
                                    ReadOnly = true
                                };
                                
                                Button btnCopy = new Button
                                {
                                    Text = "复制到剪贴板",
                                    Location = new Point(15, 210),
                                    Size = new Size(120, 35),
                                    BackColor = ModernTheme.Primary,
                                    ForeColor = ModernTheme.TextPrimary,
                                    FlatStyle = FlatStyle.Flat
                                };
                                btnCopy.Click += (s2, e2) =>
                                {
                                    Clipboard.SetText(uninstallString);
                                    MessageBox.Show("已复制到剪贴板", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                };
                                
                                Button btnCloseDetail = new Button
                                {
                                    Text = "关闭",
                                    Location = new Point(150, 210),
                                    Size = new Size(100, 35),
                                    BackColor = Color.FromArgb(71, 85, 105),
                                    ForeColor = ModernTheme.TextPrimary,
                                    FlatStyle = FlatStyle.Flat
                                };
                                btnCloseDetail.Click += (s2, e2) => detailDialog.Close();
                                
                                detailDialog.Controls.Add(lblInfo);
                                detailDialog.Controls.Add(txtUninstall);
                                detailDialog.Controls.Add(btnCopy);
                                detailDialog.Controls.Add(btnCloseDetail);
                                
                                detailDialog.ShowDialog();
                            }
                        }
                    }
                };
                
                btnExport.Click += (s, e) =>
                {
                    using (SaveFileDialog sfd = new SaveFileDialog())
                    {
                        sfd.Filter = "CSV文件|*.csv|文本文件|*.txt";
                        sfd.FileName = string.Format("软件列表_{0}_{1}.csv", pc, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        if (sfd.ShowDialog() == DialogResult.OK)
                        {
                            ExportProgramList(allItems, sfd.FileName);
                        }
                    }
                };
                
                dialog.Shown += (s, e) => btnRefresh.PerformClick();
                
                dialog.ShowDialog();
            }
        }

        private void ExportProgramList(List<ListViewItem> items, string filePath)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("序号,程序名称,版本,发布者,安装日期,大小(MB),卸载字符串");
                foreach (var item in items)
                {
                    sb.AppendLine(string.Format("\"{0}\",\"{1}\",\"{2}\",\"{3}\",\"{4}\",\"{5}\",\"{6}\"",
                        item.Text,
                        item.SubItems[1].Text.Replace("\"", "\"\""),
                        item.SubItems[2].Text.Replace("\"", "\"\""),
                        item.SubItems[3].Text.Replace("\"", "\"\""),
                        item.SubItems[4].Text.Replace("\"", "\"\""),
                        item.SubItems[5].Text,
                        (item.Tag != null ? item.Tag.ToString() : "").Replace("\"", "\"\"")));
                }
                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
                AppendResult(string.Format("✓ 已导出 {0} 个程序到文件: {1}", items.Count, filePath), ModernTheme.Success);
                MessageBox.Show(string.Format("已导出 {0} 个程序到文件", items.Count), "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendResult("导出失败: " + ex.Message, ModernTheme.Danger);
            }
        }

        private void LoadRemoteProgramsRegistry(string computerName, NetworkCredential cred, ListView lvPrograms, ProgressBar progressBar, Label lblStatus, List<ListViewItem> allItems)
        {
            try
            {
                lvPrograms.Invoke((Action)(() => { lvPrograms.Items.Clear(); allItems.Clear(); progressBar.Visible = true; lblStatus.Text = "正在读取注册表..."; }));
                AppendResult(string.Format("正在读取 {0} 的已安装程序...", computerName), ModernTheme.Info);
                
                List<ProgramInfoRegistry> programs = new List<ProgramInfoRegistry>();
                string[] regPaths = {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };
                
                foreach (string regPath in regPaths)
                {
                    try {
                        List<ProgramInfoRegistry> regPrograms = ReadRemoteRegistry(computerName, cred, regPath, lvPrograms, lblStatus);
                        programs.AddRange(regPrograms);
                    } catch (Exception ex) {
                        AppendResult(string.Format("读取注册表路径 {0} 时出错: {1}", regPath, ex.Message), ModernTheme.Warning);
                    }
                }
                
                var uniquePrograms = programs
                    .Where(p => !string.IsNullOrEmpty(p.DisplayName) && p.DisplayName != "DisplayName")
                    .GroupBy(p => p.DisplayName)
                    .Select(g => g.First())
                    .OrderBy(p => p.DisplayName)
                    .ToList();
                
                lvPrograms.Invoke((Action)(() =>
                {
                    int seq = 1;
                    foreach (var prog in uniquePrograms)
                    {
                        var item = new ListViewItem(seq.ToString());
                        item.SubItems.Add(prog.DisplayName);
                        item.SubItems.Add(prog.DisplayVersion ?? "");
                        item.SubItems.Add(prog.Publisher ?? "");
                        item.SubItems.Add(prog.InstallDate ?? "");
                        item.SubItems.Add(prog.EstimatedSizeMB.ToString());
                        item.Tag = prog.UninstallString ?? "";
                        lvPrograms.Items.Add(item);
                        allItems.Add(item);
                        seq++;
                    }
                    
                    progressBar.Visible = false;
                    lblStatus.Text = string.Format("共 {0} 个程序 (读取完成)", lvPrograms.Items.Count);
                    AppendResult(string.Format("✓ 读取完成，找到 {0} 个已安装程序", lvPrograms.Items.Count), ModernTheme.Success);
                }));
            }
            catch (Exception ex)
            {
                AppendResult("读取程序列表失败: " + ex.Message, ModernTheme.Danger);
                lvPrograms.Invoke((Action)(() => { progressBar.Visible = false; lblStatus.Text = "读取失败"; }));
            }
        }

        private List<ProgramInfoRegistry> ReadRemoteRegistry(string computerName, NetworkCredential cred, string regPath, ListView lvPrograms, Label lblStatus)
        {
            List<ProgramInfoRegistry> programs = new List<ProgramInfoRegistry>();
            try
            {
                ManagementScope scope;
                if (IsLocalMachine(computerName)) {
                    scope = new ManagementScope(@"\\.\root\default");
                } else {
                    ConnectionOptions options = new ConnectionOptions();
                    options.Username = cred.UserName;
                    options.Password = cred.Password;
                    if (!string.IsNullOrEmpty(cred.Domain)) { options.Authority = "ntlmdomain:" + cred.Domain; }
                    options.Authentication = AuthenticationLevel.PacketPrivacy;
                    options.Impersonation = ImpersonationLevel.Impersonate;
                    scope = new ManagementScope(string.Format(@"\\{0}\root\default", computerName), options);
                }
                
                scope.Connect();
                ManagementClass regClass = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
                ManagementBaseObject inParams = regClass.GetMethodParameters("EnumKey");
                inParams["hDefKey"] = 0x80000002;
                inParams["sSubKeyName"] = regPath;
                
                ManagementBaseObject outParams = regClass.InvokeMethod("EnumKey", inParams, null);
                string[] subKeys = outParams["sNames"] as string[];
                
                if (subKeys != null && subKeys.Length > 0)
                {
                    int total = subKeys.Length;
                    int processed = 0;
                    
                    foreach (string subKey in subKeys)
                    {
                        processed++;
                        string fullSubKey = regPath + "\\" + subKey;
                        
                        try
                        {
                            inParams = regClass.GetMethodParameters("GetStringValue");
                            inParams["hDefKey"] = 0x80000002;
                            inParams["sSubKeyName"] = fullSubKey;
                            inParams["sValueName"] = "DisplayName";
                            outParams = regClass.InvokeMethod("GetStringValue", inParams, null);
                            string displayName = outParams["sValue"] as string;
                            
                            if (!string.IsNullOrEmpty(displayName))
                            {
                                ProgramInfoRegistry prog = new ProgramInfoRegistry();
                                prog.DisplayName = displayName;
                                
                                inParams = regClass.GetMethodParameters("GetStringValue");
                                inParams["hDefKey"] = 0x80000002;
                                inParams["sSubKeyName"] = fullSubKey;
                                inParams["sValueName"] = "DisplayVersion";
                                outParams = regClass.InvokeMethod("GetStringValue", inParams, null);
                                prog.DisplayVersion = outParams["sValue"] as string;
                                
                                inParams = regClass.GetMethodParameters("GetStringValue");
                                inParams["hDefKey"] = 0x80000002;
                                inParams["sSubKeyName"] = fullSubKey;
                                inParams["sValueName"] = "Publisher";
                                outParams = regClass.InvokeMethod("GetStringValue", inParams, null);
                                prog.Publisher = outParams["sValue"] as string;
                                
                                inParams = regClass.GetMethodParameters("GetStringValue");
                                inParams["hDefKey"] = 0x80000002;
                                inParams["sSubKeyName"] = fullSubKey;
                                inParams["sValueName"] = "InstallDate";
                                outParams = regClass.InvokeMethod("GetStringValue", inParams, null);
                                prog.InstallDate = outParams["sValue"] as string;
                                
                                inParams = regClass.GetMethodParameters("GetStringValue");
                                inParams["hDefKey"] = 0x80000002;
                                inParams["sSubKeyName"] = fullSubKey;
                                inParams["sValueName"] = "UninstallString";
                                outParams = regClass.InvokeMethod("GetStringValue", inParams, null);
                                prog.UninstallString = outParams["sValue"] as string;
                                
                                inParams = regClass.GetMethodParameters("GetDWORDValue");
                                inParams["hDefKey"] = 0x80000002;
                                inParams["sSubKeyName"] = fullSubKey;
                                inParams["sValueName"] = "EstimatedSize";
                                outParams = regClass.InvokeMethod("GetDWORDValue", inParams, null);
                                uint size = Convert.ToUInt32(outParams["uValue"]);
                                prog.EstimatedSizeMB = (int)(size / 1024);
                                
                                if (!displayName.Contains("KB") && !displayName.Contains("Update for") && 
                                    !displayName.Contains("Hotfix") && !string.IsNullOrEmpty(prog.UninstallString))
                                {
                                    programs.Add(prog);
                                }
                            }
                        }
                        catch { }
                        
                        if (processed % 20 == 0)
                        {
                            lvPrograms.Invoke((Action)(() =>
                            {
                                lblStatus.Text = string.Format("正在读取... {0}/{1} ({2}个程序)", processed, total, programs.Count);
                            }));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendResult("本地/远程注册表读取失败: " + ex.Message, ModernTheme.Warning);
            }
            
            return programs;
        }

        private void UninstallRemoteProgram(string computerName, NetworkCredential cred, string uninstallString, string programName, Label lblStatus, ModernButton btnRefresh)
        {
            try
            {
                bool isLocal = IsLocalMachine(computerName);
                if (lblStatus.InvokeRequired) lblStatus.Invoke((Action)(() => lblStatus.Text = string.Format("正在卸载: {0}...", programName)));
                else lblStatus.Text = string.Format("正在卸载: {0}...", programName);
                
                AppendResult("", ModernTheme.TextPrimary);
                AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                AppendResult(string.Format("开始卸载: {0}", programName), ModernTheme.Info);
                AppendResult(string.Format("卸载命令: {0}", uninstallString), ModernTheme.TextSecondary);
                
                if (isLocal) {
                    this.Invoke((Action)(() => AppendResult("模式: 本机执行 (原生弹出)", ModernTheme.Info)));
                    ProcessStartInfo localPsi = new ProcessStartInfo {
                        FileName = "cmd.exe",
                        Arguments = "/c " + uninstallString,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    try {
                        using (Process p = Process.Start(localPsi)) {
                            p.WaitForExit();
                            if (p.ExitCode == 0) this.Invoke((Action)(() => AppendResult(string.Format("✓ {0} 卸载成功！", programName), ModernTheme.Success)));
                            else this.Invoke((Action)(() => AppendResult(string.Format("✗ {0} 卸载结束，退出代码: {1}", programName, p.ExitCode), ModernTheme.Warning)));
                        }
                    } catch (System.ComponentModel.Win32Exception) {
                        this.Invoke((Action)(() => AppendResult("✗ 启动失败: 用户取消了 UAC 提权", ModernTheme.Danger)));
                    }
                } else {
                    string command = string.Format("cmd.exe /c \"{0}\"", uninstallString);
                    int sessionId = GetActiveSessionId(computerName);
                    
                    string domain = !string.IsNullOrEmpty(cred.Domain) ? cred.Domain : ".";
                    string fullUser = domain + "\\" + cred.UserName;
                    string psexecArgs = string.Format(@"\\{0} -u ""{1}"" -p ""{2}"" -i {3} -h -s -accepteula {4}", 
                        computerName, fullUser, _currentPassword, sessionId, command);
                    
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = _psExecPath, Arguments = psexecArgs,
                        WorkingDirectory = Environment.SystemDirectory,
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                        UserName = cred.UserName, Password = GetSecureString(_currentPassword), Domain = cred.Domain
                    };
                    
                    using (Process process = new Process())
                    {
                        process.StartInfo = psi;
                        process.OutputDataReceived += (sender, e) => {
                            if (!string.IsNullOrEmpty(e.Data) && !e.Data.Contains("PsExec v") && !e.Data.Contains("Copyright") && !e.Data.Contains("Sysinternals"))
                                this.Invoke((Action)(() => AppendResult("  " + e.Data, ModernTheme.TextSecondary)));
                        };
                        process.ErrorDataReceived += (sender, e) => {
                            if (!string.IsNullOrEmpty(e.Data) && !e.Data.Contains("PsExec v") && !e.Data.Contains("Copyright") && !e.Data.Contains("Sysinternals") && !e.Data.Contains("Connecting"))
                                this.Invoke((Action)(() => AppendResult("  [信息] " + e.Data, ModernTheme.Info)));
                        };
                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        process.WaitForExit(180000);
                        
                        if (process.ExitCode == 0) AppendResult(string.Format("✓ {0} 卸载成功！", programName), ModernTheme.Success);
                        else AppendResult(string.Format("✗ {0} 卸载结束，退出代码: {1}", programName, process.ExitCode), ModernTheme.Warning);
                    }
                }
                
                AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                Thread.Sleep(5000);
                if (lblStatus.InvokeRequired) lblStatus.Invoke((Action)(() => lblStatus.Text = "卸载完成，正在刷新列表..."));
                else lblStatus.Text = "卸载完成，正在刷新列表...";
                
                if (btnRefresh.InvokeRequired) btnRefresh.Invoke((Action)(() => btnRefresh.PerformClick()));
                else btnRefresh.PerformClick();
            }
            catch (Exception ex)
            {
                AppendResult(string.Format("卸载失败: {0}", ex.Message), ModernTheme.Danger);
                if (lblStatus.InvokeRequired) lblStatus.Invoke((Action)(() => lblStatus.Text = "卸载失败"));
            }
        }

        private NetworkCredential GetCredForWMI()
        {
            if (_currentCred != null && !string.IsNullOrEmpty(_currentPassword)) return _currentCred;
            string savedUser, savedPwd;
            if (LoadCredentialFromFile(out savedUser, out savedPwd) && !string.IsNullOrEmpty(savedUser) && !string.IsNullOrEmpty(savedPwd))
            {
                string domain = "", user = savedUser;
                if (user.Contains("\\")) { string[] parts = user.Split('\\'); domain = parts[0]; user = parts[1]; }
                _currentCred = new NetworkCredential(user, savedPwd, domain);
                _currentPassword = savedPwd;
                return _currentCred;
            }
            return ShowCredentialDialog("请输入管理员账号 (WMI)");
        }

        private class ProgramInfoRegistry
        {
            public string DisplayName { get; set; }
            public string DisplayVersion { get; set; }
            public string Publisher { get; set; }
            public string InstallDate { get; set; }
            public string UninstallString { get; set; }
            public int EstimatedSizeMB { get; set; }
        }
        #endregion

        #region PsExec 功能
        private int GetActiveSessionId(string computerName)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo { 
                    FileName = "query", 
                    Arguments = IsLocalMachine(computerName) ? "session" : "session /server:" + computerName, 
                    UseShellExecute = false, 
                    CreateNoWindow = true, 
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.Default
                };
                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    
                    Match match = Regex.Match(output, @"(?<=\s)(\d+)\s+(Active|运行中)\b", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        return int.Parse(match.Groups[1].Value);
                    }
                    
                    string[] lines = output.Split('\n');
                    foreach (string line in lines)
                    {
                        if (line.Contains("console") && !line.Contains("65536"))
                        {
                            string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            for (int i = 0; i < parts.Length; i++)
                            {
                                int id; 
                                if (int.TryParse(parts[i], out id) && id != 65536) return id;
                            }
                        }
                    }
                }
            }
            catch { }
            return 1; 
        }

        private int ParseRemotePidFromOutput(string output)
        {
            try
            {
                if (string.IsNullOrEmpty(output)) return 0;
                string[] patterns = { @"Process ID:\s*(\d+)", @"PID:\s*(\d+)", @"process ID\s*(\d+)", @"started process (\d+)", 
                    @"with PID (\d+)", @"Started.*?PID (\d+)", @"remote PID:\s*(\d+)", @"\[(\d+)\] started", @"PsExec.*?started.*?(\d+)", @"(\d+)\s+started" };
                foreach (string pattern in patterns)
                {
                    Match match = Regex.Match(output, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    if (match.Success && match.Groups.Count > 1) { int pid; if (int.TryParse(match.Groups[1].Value, out pid) && pid > 0) return pid; }
                }
                return 0;
            }
            catch { return 0; }
        }

        private SecureString GetSecureString(string password)
        {
            SecureString secureString = new SecureString();
            foreach (char c in password) { secureString.AppendChar(c); }
            return secureString;
        }

        private bool IsGuiProgram(string command)
        {
            string lowerCommand = command.ToLower();
            string[] guiPrograms = { 
                "notepad", "calc", "mspaint", "write", "wordpad", "explorer", 
                "control", "mmc", "taskmgr", "regedit", "winver", "osk", 
                "snippingtool", "charmap", "dxdiag", "msinfo32", "cleanmgr"
            };
            
            foreach (string gui in guiPrograms)
            {
                if (lowerCommand.Contains(gui))
                {
                    return true;
                }
            }
            return false;
        }

        private void RunPsExecWithCredential(string command, bool waitForExit = true)
        {
            string pc = GetTargetComputer();
            bool isLocal = IsLocalMachine(pc);
            
            if (!isLocal && string.IsNullOrEmpty(_psExecPath)) { AppendResult("✗ 未找到 PsExec64.exe", ModernTheme.Danger); return; }
            
            SetStatus("Execution: " + command, true);
            AppendResult("> 凭证模式: ProcessStartInfo 提权唤醒", ModernTheme.TextPrimary);
            AppendResult("  目标: " + pc + (isLocal ? " (本机)" : ""), ModernTheme.TextMuted);
            AppendResult("  命令: " + command, ModernTheme.TextMuted);
            
            Task.Run(() =>
            {
                try
                {
                    if (isLocal)
                    {
                        this.Invoke((Action)(() => AppendResult("  模式: 本地执行 (原生提权弹出)", ModernTheme.Info)));
                        
                        string args = command;
                        string exe = "cmd.exe";
                        if (command.StartsWith("cmd.exe /c ", StringComparison.OrdinalIgnoreCase)) {
                            args = command.Substring(7).Trim();
                        } else if (command.StartsWith("cmd.exe ", StringComparison.OrdinalIgnoreCase)) {
                            args = "/c " + command.Substring(8).Trim();
                        } else {
                            args = "/c " + command;
                        }

                        ProcessStartInfo localPsi = new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = args,
                            UseShellExecute = true, // 必须为 true 才能弹出桌面 UI 和触发 UAC
                            Verb = "runas"
                        };

                        this.Invoke((Action)(() => AppendResult("  正在请求 UAC 提权并启动...", ModernTheme.Info)));
                        
                        using (Process p = new Process())
                        {
                            p.StartInfo = localPsi;
                            try {
                                p.Start();
                                if (waitForExit) {
                                    this.Invoke((Action)(() => AppendResult("  等待程序执行完成...", ModernTheme.Info)));
                                    DateTime startTime = DateTime.Now;
                                    p.WaitForExit();
                                    TimeSpan elapsed = DateTime.Now - startTime;
                                    int exitCode = p.ExitCode;
                                    
                                    this.Invoke((Action)(() => {
                                        AppendResult("", ModernTheme.TextPrimary);
                                        AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                                        AppendResult("  执行完成", ModernTheme.Success);
                                        AppendResult(string.Format("  耗时: {0:0.00} 秒", elapsed.TotalSeconds), ModernTheme.Info);
                                        AppendResult("  退出代码: " + exitCode, exitCode == 0 ? ModernTheme.Success : ModernTheme.Warning);
                                        AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                                    }));
                                } else {
                                    this.Invoke((Action)(() => { AppendResult("  ✓ 已启动 (原生弹出)", ModernTheme.Success); }));
                                }
                            } catch (System.ComponentModel.Win32Exception) {
                                this.Invoke((Action)(() => { AppendResult("  ✗ 操作取消: 用户拒绝了 UAC 提权", ModernTheme.Danger); }));
                            }
                        }
                        return; // 结束本地执行分支
                    }

                    // 远端 PsExec 逻辑...
                    NetworkCredential cred = GetCredForPsExec();
                    if (cred == null) { AppendResult("  ✗ 未获取到凭证或用户取消", ModernTheme.Danger); return; }
                    if (string.IsNullOrEmpty(_currentPassword)) {
                        AppendResult("  ✗ 密码为空，请重新输入凭证", ModernTheme.Danger);
                        _currentCred = null; _currentPassword = "";
                        cred = ShowCredentialDialog("请输入管理员账号");
                        if (cred == null) { AppendResult("  ✗ 未获取到凭证", ModernTheme.Danger); return; }
                    }
                    
                    string domain = !string.IsNullOrEmpty(cred.Domain) ? cred.Domain : ".";
                    string fullUser = domain + "\\" + cred.UserName;
                    
                    int sessionId = GetActiveSessionId(pc);
                    string psexecArgs = waitForExit ? 
                        string.Format(@"\\{0} -u ""{1}"" -p ""{2}"" -i {3} -h -s -accepteula {4}", pc, fullUser, _currentPassword, sessionId, command) :
                        string.Format(@"\\{0} -u ""{1}"" -p ""{2}"" -i {3} -h -s -d -accepteula {4}", pc, fullUser, _currentPassword, sessionId, command);
                    
                    AppendResult(string.Format("  使用凭证: {0}", fullUser), ModernTheme.Info);
                    
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = _psExecPath, Arguments = psexecArgs,
                        WorkingDirectory = Environment.SystemDirectory,
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                        UserName = cred.UserName, Password = GetSecureString(_currentPassword), Domain = cred.Domain
                    };
                    
                    AppendResult("  正在启动 PsExec...", ModernTheme.Info);
                    
                    using (Process process = new Process())
                    {
                        process.StartInfo = psi;
                        process.Start();
                        
                        if (waitForExit)
                        {
                            AppendResult("  等待程序执行完成...", ModernTheme.Info);
                            DateTime startTime = DateTime.Now;
                            process.WaitForExit();
                            TimeSpan elapsed = DateTime.Now - startTime;
                            int exitCode = process.ExitCode;
                            
                            this.Invoke((Action)(() => {
                                AppendResult("", ModernTheme.TextPrimary);
                                AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                                AppendResult("  执行完成", ModernTheme.Success);
                                AppendResult(string.Format("  耗时: {0:0.00} 秒", elapsed.TotalSeconds), ModernTheme.Info);
                                AppendResult("  PsExec 退出代码: " + exitCode, exitCode == 0 ? ModernTheme.Success : ModernTheme.Warning);
                                AppendResult("════════════════════════════════════════", ModernTheme.TextMuted);
                            }));
                        }
                        else
                        {
                            this.Invoke((Action)(() => { AppendResult("  ✓ 已启动（后台运行）", ModernTheme.Success); }));
                        }
                    }
                }
                catch (Exception ex) 
                { 
                    this.Invoke((Action)(() => AppendResult("  ✗ 异常: " + ex.Message, ModernTheme.Danger))); 
                }
                finally { this.Invoke((Action)(() => SetStatus("就绪", false))); }
            });
        }

        private void RunPsExecProgram()
        {
            string pc = GetTargetComputer();
            
            string programPath = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入要远程运行的程序路径\n\n【提示】\n• 仅支持图形界面程序（如 notepad, calc, mspaint 等）\n• 命令行程序不支持\n\n【示例】\nnotepad\ncalc\nmspaint\ncontrol",
                "远程运行程序", "notepad", -1, -1);
            
            if (string.IsNullOrEmpty(programPath)) { return; }
            
            string[] blockedCommands = { 
                "ipconfig", "ping", "dir", "cd", "copy", "del", "erase", "mkdir", "rmdir",
                "netstat", "tasklist", "taskkill", "systeminfo", "whoami", "hostname",
                "nslookup", "tracert", "pathping", "route", "arp", "getmac", "net",
                "sc", "reg", "wmic", "powershell", "cmd", "bash", "telnet", "ssh"
            };
            
            string lowerProgram = programPath.ToLower().Trim();
            bool isBlocked = false;
            string matchedBlock = "";
            
            foreach (string blocked in blockedCommands)
            {
                if (lowerProgram == blocked || lowerProgram.StartsWith(blocked + " ") || lowerProgram.StartsWith(blocked + ".exe"))
                {
                    isBlocked = true; matchedBlock = blocked; break;
                }
            }
            
            if (isBlocked) { AppendResult(string.Format("✗ 不支持的命令: {0}", matchedBlock), ModernTheme.Danger); return; }
            RunPsExecWithCredential("cmd.exe /c start \"\" " + programPath, false);
        }

        private NetworkCredential GetCredForPsExec()
        {
            if (_currentCred != null && !string.IsNullOrEmpty(_currentPassword)) return _currentCred;
            string savedUser, savedPwd;
            if (LoadCredentialFromFile(out savedUser, out savedPwd) && !string.IsNullOrEmpty(savedUser) && !string.IsNullOrEmpty(savedPwd))
            {
                string domain = "", user = savedUser;
                if (user.Contains("\\")) { string[] parts = user.Split('\\'); domain = parts[0]; user = parts[1]; }
                _currentCred = new NetworkCredential(user, savedPwd, domain);
                _currentPassword = savedPwd;
                return _currentCred;
            }
            return ShowCredentialDialog("请输入管理员账号 (PsExec)");
        }
        #endregion
    }
    
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainWindow());
        }
    }
}
"@
    
    $tempFile = [System.IO.Path]::GetTempFileName() + ".cs"
    [System.IO.File]::WriteAllText($tempFile, $csharpCode, [System.Text.Encoding]::UTF8)
    
    Write-ColorOutput "正在进行标准编译..." "Info"
    $argsList = @(
        "/target:winexe", 
        "/out:`"$($config.OutputExe)`"", 
        "/reference:System.Windows.Forms.dll", 
        "/reference:System.Drawing.dll", 
        "/reference:System.dll", 
        "/reference:System.Core.dll", 
        "/reference:System.Security.dll", 
        "/reference:System.Management.dll", 
        "/reference:Microsoft.VisualBasic.dll"
    )
    if ($config.Optimization) { $argsList += "/optimize+" }
    if ($hasIcon) { $argsList += "/win32icon:`"$tempIconPath`"" }
    $argsList += "`"$tempFile`""
    
    $processInfo = New-Object System.Diagnostics.ProcessStartInfo
    $processInfo.FileName = $csc
    $processInfo.Arguments = ($argsList -join " ")
    $processInfo.UseShellExecute = $false
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.CreateNoWindow = $true
    
    $process = [System.Diagnostics.Process]::Start($processInfo)
    $compileOutput = $process.StandardOutput.ReadToEnd()
    $compileError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    
    if ($process.ExitCode -eq 0 -and (Test-Path $config.OutputExe)) {
        Write-ColorOutput "编译成功！" "Success"
        Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
        if ($hasIcon) { Remove-Item $tempIconPath -Force -ErrorAction SilentlyContinue }
        
        Write-Host "`n🎉 v8.2 编译修复与环境管理版 已完成：" -ForegroundColor Cyan
        Write-Host "   • 🛠️ 编译修复：完整重装了 v8.1 意外丢失的软件管理与凭证读取核心模块，彻底解除 CS0103 错误。" -ForegroundColor White
        Write-Host "   • ⚙️ 系统集成：全新加入原生环境变量管理，通过底层注册表精准无感挂载读取。" -ForegroundColor White
        Write-Host "   • 👤 用户侦测：智能识别目标机器当前的登陆账号，直接读取并允许在线增删改该用户的私有环境变量。" -ForegroundColor White
        Write-Host "   • 🛡️ 异常拦截：保留 ICMP 底层探测，准确区分“目标电脑已离线”与“未发现 X 盘分区”。" -ForegroundColor White
        Write-Host "   • 🌐 自动下载：加入了开机环境检测，若缺失PsExec64则会静默下载补全。" -ForegroundColor White
        Write-Host "   • 🔑 注册表管理：新增了远程后台无感读取与修改注册表键值功能，且内置树状结构浏览器。" -ForegroundColor White
        Write-Host "   • 🪟 界面优化：注册表页面新增最大化/最小化支持，列表栏自适应拉伸防遮挡。" -ForegroundColor White
        Write-Host "   • 📂 路径导航：注册表编辑器支持顶部地址栏复制以及回车自动深层导航与解析。" -ForegroundColor White
        Write-Host "   • 🖱️ 右键菜单：注册表左侧树状结构加入原生“新建/重命名/彻底删除项”右键菜单。" -ForegroundColor White
        Write-Host "   • 🔐 权限修复：直接提取底层 Explorer.exe 进程的 SID，彻底解决了 AzureAD 账号的读取失败问题。" -ForegroundColor White
        Write-Host "   • ✨ UI 纯净版：移专门移除了多余的隐藏表格，去除了列表网格线，布局更贴近原生 Windows 风格。" -ForegroundColor White
        Write-Host "   • 🖨️ 打印机管理：新增远程打印机增删改模块，双击直接调用原生属性，支持.ps1/.vbs脚本拖放安装。" -ForegroundColor White
        Write-Host "   • 🖥️ 电脑信息：新增远程 Computer 硬件(包含显卡)、网络与系统信息全维度一键抓取与展示。" -ForegroundColor White
        Write-Host "   • 📐 布局修复：修复快捷工具区第三排菜单边距问题，绝对对齐还原。" -ForegroundColor White
        Write-Host "   • 🎨 视觉优化：重排工具区按钮配色，防冲突；显卡查询增加过滤机制拦截虚拟网卡。" -ForegroundColor White
        Write-Host "   • 📑 资产导出：“Computer信息”模块已全面集成数据行右键复制与整表 CSV 导出功能。" -ForegroundColor White
        Write-Host "   • 🐞 底层修复：修复传入身份凭证执行PsExec因为工作目录权限问题导致抛出无效目录异常。" -ForegroundColor Green
        
        Write-Host ""
        choice.exe /C YN /M "是否立即运行程序？"
        if ($LASTEXITCODE -eq 1) {
            Start-Process -FilePath ".\$($config.OutputExe)"
        }
        
    } else {
        Write-ColorOutput "编译失败！" "Error"
        Write-Host "`n========== 详细编译日志 ==========" -ForegroundColor Yellow
        if ($compileOutput) { Write-Host $compileOutput -ForegroundColor Yellow }
        if ($compileError)  { Write-Host $compileError -ForegroundColor Red }
        Write-Host "==================================" -ForegroundColor Yellow
        
        Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
        if ($hasIcon) { Remove-Item $tempIconPath -Force -ErrorAction SilentlyContinue }
        
        Write-Host ""
        choice.exe /C YN /M "编译失败，按 Y 或 N 退出"
        exit 1
    }
}
try { Start-Compilation } catch { Write-ColorOutput "脚本执行出错" "Error"; exit 1 }