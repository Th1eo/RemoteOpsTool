param(
    [int]$Sample = 12
)

$ErrorActionPreference = 'Stop'

$HKCR = [Convert]::ToUInt32('80000000', 16)
$HKCU = [Convert]::ToUInt32('80000001', 16)
$HKLM = [Convert]::ToUInt32('80000002', 16)

$useWmi = $true
try {
    $reg = [wmiclass]'root\default:StdRegProv'
} catch {
    $useWmi = $false
    Write-Warning "WMI StdRegProv unavailable in current context: $($_.Exception.Message)"
    Write-Warning "Falling back to local Registry provider for data retrieval."
}

function Get-HiveName([uint32]$Hive) {
    switch ($Hive) {
        $HKCR { 'HKCR' }
        $HKCU { 'HKCU' }
        $HKLM { 'HKLM' }
        default { "0x{0:X8}" -f $Hive }
    }
}

function Get-HiveProviderRoot([uint32]$Hive) {
    switch ($Hive) {
        $HKCR { [Microsoft.Win32.RegistryHive]::ClassesRoot }
        $HKCU { [Microsoft.Win32.RegistryHive]::CurrentUser }
        $HKLM { [Microsoft.Win32.RegistryHive]::LocalMachine }
        default { throw "Unsupported hive: $Hive" }
    }
}

function Get-RegString([uint32]$Hive, [string]$SubKey, [string]$ValueName) {
    if (-not $useWmi) {
        $baseKey = $null
        $key = $null
        try {
            $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey((Get-HiveProviderRoot $Hive), [Microsoft.Win32.RegistryView]::Default)
            $key = $baseKey.OpenSubKey($SubKey)
            if ($null -eq $key) { return '' }
            $value = $key.GetValue($ValueName)
            if ($null -ne $value) { return [string]$value }
        } catch {}
        finally {
            if ($null -ne $key) { $key.Dispose() }
            if ($null -ne $baseKey) { $baseKey.Dispose() }
        }
        return ''
    }

    try {
        $out = $reg.GetStringValue($Hive, $SubKey, $ValueName)
        if ($out.ReturnValue -eq 0) { return [string]$out.sValue }
    } catch {}
    return ''
}

function Get-SubKeys([uint32]$Hive, [string]$SubKey) {
    if (-not $useWmi) {
        $baseKey = $null
        $key = $null
        try {
            $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey((Get-HiveProviderRoot $Hive), [Microsoft.Win32.RegistryView]::Default)
            $key = $baseKey.OpenSubKey($SubKey)
            if ($null -eq $key) { return @() }
            return @($key.GetSubKeyNames())
        } catch {}
        finally {
            if ($null -ne $key) { $key.Dispose() }
            if ($null -ne $baseKey) { $baseKey.Dispose() }
        }
        return @()
    }

    try {
        $out = $reg.EnumKey($Hive, $SubKey)
        if ($out.ReturnValue -eq 0 -and $out.sNames) { return @($out.sNames) }
    } catch {}
    return @()
}

function First-Text([string[]]$Values) {
    foreach ($value in $Values) {
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
    }
    return ''
}

function Get-UninstallEntries {
    $paths = @(
        @{ Hive = $HKLM; SubKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; Display = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' },
        @{ Hive = $HKLM; SubKey = 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'; Display = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' },
        @{ Hive = $HKCU; SubKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'; Display = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' }
    )

    foreach ($path in $paths) {
        foreach ($child in Get-SubKeys $path.Hive $path.SubKey) {
            $key = "$($path.SubKey)\$child"
            $displayName = Get-RegString $path.Hive $key 'DisplayName'
            if ([string]::IsNullOrWhiteSpace($displayName)) { continue }

            [pscustomobject]@{
                Source = 'Uninstall'
                Name = $displayName
                RegistryKey = "$($path.Display)\$child"
            }
        }
    }
}

function Get-InstallerProductEntries {
    $paths = @(
        @{ Hive = $HKCR; SubKey = 'Installer\Products'; Display = 'HKCR:\Installer\Products' }
    )

    foreach ($path in $paths) {
        foreach ($child in Get-SubKeys $path.Hive $path.SubKey) {
            $productKey = "$($path.SubKey)\$child"
            $installPropertiesKey = "$productKey\InstallProperties"
            $name = First-Text @(
                (Get-RegString $path.Hive $installPropertiesKey 'DisplayName'),
                (Get-RegString $path.Hive $productKey 'ProductName'),
                (Get-RegString $path.Hive $productKey 'DisplayName'),
                $child
            )

            [pscustomobject]@{
                Source = 'Products'
                Name = $name
                RegistryKey = "$($path.Display)\$child"
            }
        }
    }
}

$uninstall = @(Get-UninstallEntries)
$products = @(Get-InstallerProductEntries)
$normalDeduped = @(
    $uninstall |
        Group-Object -Property RegistryKey |
        ForEach-Object { $_.Group[0] } |
        Group-Object -Property Name |
        ForEach-Object { $_.Group[0] }
)
$deepNoDedup = @($uninstall + $products)

Write-Host "Local registry probe mode: $(if ($useWmi) { 'WMI StdRegProv' } else { '.NET RegistryKey provider' })"
Write-Host "Uninstall entries: $($uninstall.Count)"
Write-Host "Normal mode entries after RegistryKey + DisplayName dedupe: $($normalDeduped.Count)"
Write-Host "Installer Products entries: $($products.Count)"
Write-Host "Deep cleanup entries without dedupe: $($deepNoDedup.Count)"
Write-Host ''
Write-Host 'Installer Products samples:'
$products |
    Select-Object -First $Sample Name, RegistryKey |
    Format-Table -AutoSize
