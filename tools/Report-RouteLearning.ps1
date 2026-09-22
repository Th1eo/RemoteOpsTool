[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Path = (Join-Path $env:APPDATA 'RemoteAdmin\route-learning.json'),

    [string]$ExportCsv
)

$ErrorActionPreference = 'Stop'

function Get-Percentile {
    param(
        [object[]]$Samples,
        [double]$Percentile,
        [double]$Fallback
    )

    if ($null -eq $Samples -or $Samples.Count -eq 0) {
        return [Math]::Max(0, $Fallback)
    }

    $sorted = @($Samples | ForEach-Object { [double]$_ } | Sort-Object)
    $index = [int][Math]::Ceiling($Percentile * $sorted.Count) - 1
    if ($index -lt 0) { $index = 0 }
    if ($index -ge $sorted.Count) { $index = $sorted.Count - 1 }
    return [Math]::Max(0, $sorted[$index])
}

function Format-Percent {
    param([double]$Value)
    return ('{0:P1}' -f $Value)
}

function Format-Fingerprint {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return '-' }
    if ($Value.Length -le 12) { return $Value }
    return $Value.Substring(0, 8) + '...'
}

function Get-RecentFailureText {
    param($Record)

    $transportFailure = [string]$Record.LastFailureKind
    $commandFailure = [string]$Record.LastCommandFailureKind

    if ($transportFailure -and $transportFailure -ne 'None') {
        if ($commandFailure -and $commandFailure -ne 'None') {
            return "传输=$transportFailure; 命令=$commandFailure"
        }
        return "传输=$transportFailure"
    }

    if ($commandFailure -and $commandFailure -ne 'None') {
        return "命令=$commandFailure"
    }

    return '-'
}

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "未找到路由学习文件: $Path。先运行需要远程执行的功能，或使用 -Path 指定其他文件。"
}

try {
    $document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "路由学习文件不是有效 JSON: $Path。$($_.Exception.Message)"
}

$records = @($document.Records)
if ($records.Count -eq 0) {
    Write-Host "路由学习文件没有记录: $Path"
    return
}

$rows = foreach ($record in $records) {
    $attempts = [int]$record.SuccessCount + [int]$record.FailureCount
    $successRate = if ($attempts -gt 0) { [double]$record.SuccessCount / $attempts } else { 0 }
    $transportFailureRate = if ($attempts -gt 0) { [double]$record.FailureCount / $attempts } else { 0 }
    $commandNonZeroRate = if ([int]$record.SuccessCount -gt 0) { [double]$record.CommandNonZeroCount / [int]$record.SuccessCount } else { 0 }
    $fallbackRate = if ($attempts -gt 0) { [double]$record.FallbackCount / $attempts } else { 0 }
    $durations = @($record.RecentDurationMs)
    $p50 = Get-Percentile -Samples $durations -Percentile 0.50 -Fallback ([double]$record.AverageDurationMs)
    $p95 = Get-Percentile -Samples $durations -Percentile 0.95 -Fallback ([double]$record.AverageDurationMs)

    $lastSuccess = [DateTimeOffset]::MinValue
    $lastFailure = [DateTimeOffset]::MinValue
    if ($record.LastSuccessAt) { $lastSuccess = [DateTimeOffset]$record.LastSuccessAt }
    if ($record.LastFailureAt) { $lastFailure = [DateTimeOffset]$record.LastFailureAt }
    $lastAttempt = if ($lastSuccess -ge $lastFailure) { $lastSuccess } else { $lastFailure }
    $lastAttemptText = if ($lastAttempt -eq [DateTimeOffset]::MinValue) { '-' } else { $lastAttempt.LocalDateTime.ToString('yyyy-MM-dd HH:mm:ss') }

    [PSCustomObject]@{
        '主机' = [string]$record.Host
        '凭据指纹' = Format-Fingerprint ([string]$record.CredentialFingerprint)
        '操作' = [string]$record.Operation
        '命令形态' = [string]$record.CommandShape
        '通道' = [string]$record.Transport
        '总尝试' = $attempts
        '成功率' = Format-Percent $successRate
        '传输失败率' = Format-Percent $transportFailureRate
        '命令非零率' = Format-Percent $commandNonZeroRate
        '回退率' = Format-Percent $fallbackRate
        '回退次数' = [int]$record.FallbackCount
        'P50(ms)' = [Math]::Round($p50, 1)
        'P95(ms)' = [Math]::Round($p95, 1)
        '平均首输出(ms)' = [Math]::Round([double]$record.AverageFirstOutputMs, 1)
        '输出(KB)' = [Math]::Round(([double]$record.TotalOutputBytes / 1024), 1)
        '最近失败类型' = Get-RecentFailureText $record
        '最后尝试' = $lastAttemptText
    }
}

$sortedRows = $rows | Sort-Object '主机', '操作', '命令形态', '通道'
Write-Host "路由学习报表: $Path"
Write-Host "记录数: $($sortedRows.Count)"

Write-Host ''
Write-Host '路由与结果统计'
$sortedRows |
    Select-Object '主机', '凭据指纹', '操作', '命令形态', '通道', '总尝试', '成功率', '传输失败率', '命令非零率', '回退率' |
    Format-Table -AutoSize -Wrap | Out-String -Width 4096 | Write-Host

Write-Host '性能与失败统计'
$sortedRows |
    Select-Object '主机', '操作', '通道', '回退次数', 'P50(ms)', 'P95(ms)', '平均首输出(ms)', '输出(KB)', '最近失败类型', '最后尝试' |
    Format-Table -AutoSize -Wrap | Out-String -Width 4096 | Write-Host

if (-not [string]::IsNullOrWhiteSpace($ExportCsv)) {
    $sortedRows | Export-Csv -LiteralPath $ExportCsv -NoTypeInformation -Encoding UTF8
    Write-Host "CSV 已导出: $ExportCsv"
}