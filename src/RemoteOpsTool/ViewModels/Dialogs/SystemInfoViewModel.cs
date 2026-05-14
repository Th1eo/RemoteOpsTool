using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class SystemInfoViewModel : ObservableObject
{
    private readonly ISystemInfoService? _service;
    private readonly string? _host;
    private readonly string? _username;
    private readonly string? _password;
    private readonly ICacheService _cache;

    [ObservableProperty]
    private string _systemInfoText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private SystemInfoData? _infoData;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _formattedText = string.Empty;

    [ObservableProperty]
    private string _lastRefreshText = string.Empty;

    [ObservableProperty]
    private string _queryMethodInfo = string.Empty;

    [ObservableProperty]
    private FlowDocument? _infoDocument;

    public SystemInfoViewModel(ISystemInfoService service, string host, string username, string password)
    {
        _service = service;
        _host = host;
        _username = username;
        _password = password;
        _cache = App.GetService<ICacheService>();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_service == null || string.IsNullOrEmpty(_host)) return;
        IsLoading = true;
        try
        {
            await _cache.PopulateFromCacheAsync<SystemInfoData>(_host, "systeminfo", data => { if (data.HasData) ShowData(data); });

            if (!await _cache.HasValidCacheAsync(_host, "systeminfo"))
            {
                var data = await _service.GetStructuredSystemInfoAsync(_host, _username ?? "", _password ?? "");
                await _cache.SaveAndPopulateAsync(_host, "systeminfo", data, ShowData);
            }

            LastRefreshText = _cache.GetCacheAge(_host, "systeminfo") is string age ? $"缓存于 {age}" : "实时查询";
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"查询异常: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    private void ShowData(SystemInfoData data)
    {
        InfoData = data;
        HasData = data.HasData;
        HasError = data.HasError;
        ErrorMessage = data.ErrorMessage;
        QueryMethodInfo = string.IsNullOrEmpty(data.QueryMethod) ? "" : $"查询方式: {data.QueryMethod}";
        if (HasData)
        {
            FormattedText = BuildFormattedText(data);
            InfoDocument = BuildDocument(data);
        }

        SystemInfoText = !string.IsNullOrWhiteSpace(data.RawOutput)
            ? data.RawOutput
            : !string.IsNullOrWhiteSpace(FormattedText)
                ? FormattedText
                : data.ErrorMessage ?? string.Empty;
    }

    private static string BuildFormattedText(SystemInfoData data)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"Host Name: {data.HostName}",
            $"OS: {data.OsCaption} ({data.OsVersion} {data.OsArchitecture})",
            $"Build: {data.BuildNumber}",
            $"Install Date: {data.InstallDate}",
            $"Boot Time: {data.LastBootUpTime}",
            $"Manufacturer: {data.Manufacturer}",
            $"Model: {data.Model}",
            $"System Type: {data.SystemType}",
            $"Processor: {data.ProcessorName}",
            $"Cores: {data.ProcessorCores} ({data.ProcessorLogicalProcessors} Logical)",
            $"Max Clock: {data.ProcessorMaxClockSpeed}",
            $"Memory: {data.TotalPhysicalMemoryGB} (Free: {data.FreePhysicalMemoryGB}, Used: {data.UsedPhysicalMemoryGB})",
            $"Domain: {data.Domain}",
            $"Current User: {data.CurrentUser}",
            $"Owner: {data.RegisteredOwner}",
            $"Organization: {data.RegisteredOrganization}",
            $"System Drive: {data.SystemDrive}",
            $"System Dir: {data.SystemDirectory}",
            $"Windows Dir: {data.WindowsDirectory}",
            $"Time Zone: {data.TimeZone}",
            $"BIOS: {data.BiosManufacturer} {data.BiosVersion} (S/N: {data.BiosSerialNumber})",
            $"BaseBoard: {data.BaseBoardManufacturer} {data.BaseBoardProduct} (S/N: {data.BaseBoardSerialNumber})",
            $"Graphics: {data.GraphicsCards}",
            $"GPU Drivers: {data.GpuDriverVersions}",
            $"Network Adapters: {data.NetworkAdapters}",
            $"IP Addresses: {data.IpAddresses}",
            $"MAC Addresses: {data.MacAddresses}",
            $"Logical Disks: {data.LogicalDisks}",
            $"Recent HotFixes: {data.RecentHotFixes}"
        });
    }

    private static readonly SolidColorBrush SectionHeaderBrush = new(System.Windows.Media.Color.FromRgb(0x5B, 0x61, 0xD6));
    private static readonly SolidColorBrush SubHeaderBrush = new(System.Windows.Media.Color.FromRgb(0x00, 0x89, 0x7B));
    private static readonly SolidColorBrush ValueBrush = new(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush RowAltBrush = new(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly SolidColorBrush DividerBrush = new(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly SolidColorBrush KeyCellBgBrush = new(System.Windows.Media.Color.FromRgb(0x15, 0x97, 0xFF));
    private static readonly SolidColorBrush KeyCellFgBrush = new(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly System.Windows.Media.FontFamily MonoFamily = new("Consolas, Source Han Sans SC");

    private static FlowDocument BuildDocument(SystemInfoData d)
    {
        var doc = new FlowDocument { FontFamily = MonoFamily, FontSize = 13, Foreground = ValueBrush, PagePadding = new Thickness(0) };

        // ── 系统概览 ──
        doc.Blocks.Add(SectionHeader("系统概览"));
        doc.Blocks.Add(BuildTable(new (string, string)[]
        {
            ("操作系统", d.OsCaption),
            ("系统版本", d.OsVersion),
            ("系统架构", d.OsArchitecture),
            ("编译版本", d.BuildNumber),
            ("安装日期", d.InstallDate),
            ("最后启动", d.LastBootUpTime),
            ("运行时间", d.UptimeDisplay),
            ("系统驱动器", d.SystemDrive),
            ("Windows 目录", d.WindowsDirectory),
            ("时区", d.TimeZone),
        }));
        doc.Blocks.Add(Divider());

        // ── 硬件 / BIOS / 主板 ──
        doc.Blocks.Add(SectionHeader("硬件信息"));
        doc.Blocks.Add(BuildTable(new (string, string)[]
        {
            ("制造商", d.Manufacturer),
            ("型号", d.Model),
            ("系统类型", d.SystemType),
            ("处理器", d.ProcessorName),
            ("物理核心数", d.ProcessorCores),
            ("逻辑处理器", d.ProcessorLogicalProcessors),
            ("最大主频", d.ProcessorMaxClockSpeed),
        }));
        doc.Blocks.Add(SectionSpacer());
        doc.Blocks.Add(SubHeader("BIOS"));
        doc.Blocks.Add(BuildTable(new (string, string)[]
        {
            ("BIOS 厂商", d.BiosManufacturer),
            ("BIOS 版本", d.BiosVersion),
            ("序列号", d.BiosSerialNumber),
        }));
        doc.Blocks.Add(SectionSpacer());
        doc.Blocks.Add(SubHeader("主板"));
        doc.Blocks.Add(BuildTable(new (string, string)[]
        {
            ("主板厂商", d.BaseBoardManufacturer),
            ("主板型号", d.BaseBoardProduct),
            ("主板序列号", d.BaseBoardSerialNumber),
        }));
        doc.Blocks.Add(Divider());

        // ── 内存 ──
        doc.Blocks.Add(SectionHeader("内存信息"));
        doc.Blocks.Add(BuildTable(new (string, string)[]
        {
            ("物理内存总计", d.TotalPhysicalMemoryGB),
            ("空闲物理内存", d.FreePhysicalMemoryGB),
            ("已用物理内存", d.UsedPhysicalMemoryGB),
            ("虚拟内存总计", d.TotalVirtualMemoryGB),
        }));
        doc.Blocks.Add(Divider());

        // ── 域与用户 ──
        doc.Blocks.Add(SectionHeader("域与用户信息"));
        doc.Blocks.Add(BuildTable(new (string, string)[]
        {
            ("所在域", d.Domain),
            ("当前用户", d.CurrentUser),
            ("注册用户", d.RegisteredOwner),
            ("注册组织", d.RegisteredOrganization),
        }));
        doc.Blocks.Add(Divider());

        // ── 显卡 ──
        doc.Blocks.Add(SectionHeader("显卡信息"));
        doc.Blocks.Add(BuildTable(new (string, string)[]
        {
            ("显卡", d.GraphicsCards),
            ("驱动版本", d.GpuDriverVersions),
        }));
        doc.Blocks.Add(Divider());

        // ── 网络 ──
        doc.Blocks.Add(SectionHeader("网络信息"));
        doc.Blocks.Add(BuildTable(new (string, string)[]
        {
            ("网卡", d.NetworkAdapters),
            ("IP 地址", d.IpAddresses),
            ("MAC 地址", d.MacAddresses),
        }));
        doc.Blocks.Add(Divider());

        // ── 磁盘 ──
        doc.Blocks.Add(SectionHeader("磁盘信息"));
        doc.Blocks.Add(BuildTable(new (string, string)[]
        {
            ("逻辑磁盘", d.LogicalDisks),
        }));
        doc.Blocks.Add(Divider());

        // ── 补丁 ──
        doc.Blocks.Add(SectionHeader("补丁信息"));
        doc.Blocks.Add(BuildTable(new (string, string)[]
        {
            ("最近补丁", d.RecentHotFixes),
        }));

        return doc;
    }

    private static Paragraph SectionHeader(string text)
    {
        return new Paragraph(new Run(text))
        {
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = SectionHeaderBrush,
            Margin = new Thickness(0, 12, 0, 8)
        };
    }

    private static Paragraph SubHeader(string text)
    {
        return new Paragraph(new Run(text))
        {
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = SubHeaderBrush,
            Margin = new Thickness(0, 4, 0, 4)
        };
    }

    private static Paragraph SectionSpacer()
    {
        return new Paragraph(new Run(string.Empty)) { Margin = new Thickness(0, 2, 0, 2) };
    }

    private static Block Divider()
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 8),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 4)
        };
        return p;
    }

    private static Table BuildTable((string Key, string Value)[] rows)
    {
        var table = new Table
        {
            CellSpacing = 0,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4)
        };

        table.Columns.Add(new TableColumn { Width = new GridLength(120) });
        table.Columns.Add(new TableColumn { Width = GridLength.Auto });

        var rowGroup = new TableRowGroup();
        for (var i = 0; i < rows.Length; i++)
        {
            var (key, value) = rows[i];
            var row = new TableRow();

            if (i % 2 == 1)
                row.Background = RowAltBrush;

            var keyCell = new TableCell(new Paragraph(new Run(key))
            {
                Margin = new Thickness(0),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = KeyCellFgBrush,
            })
            {
                Padding = new Thickness(6, 4, 8, 4),
                BorderThickness = new Thickness(0),
                Background = KeyCellBgBrush,
            };

            var valCell = new TableCell(new Paragraph(new Run(value ?? "-"))
            {
                Margin = new Thickness(0),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = ValueBrush,
            })
            {
                Padding = new Thickness(6, 4, 4, 4),
                BorderThickness = new Thickness(0),
            };

            row.Cells.Add(keyCell);
            row.Cells.Add(valCell);
            rowGroup.Rows.Add(row);
        }

        table.RowGroups.Add(rowGroup);
        return table;
    }
}
