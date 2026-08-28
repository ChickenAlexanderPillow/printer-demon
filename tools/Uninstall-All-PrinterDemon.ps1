[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Force
)

$ErrorActionPreference = 'SilentlyContinue'

if (-not $Force) {
    $answer = Read-Host 'Remove all Printer Demon installations and leftovers? Type YES to continue'
    if ($answer -cne 'YES') {
        Write-Output 'Cancelled.'
        exit 0
    }
}

Write-Output 'Stopping Printer Demon processes...'
Get-Process -Name 'PrinterDemon' | Stop-Process -Force

$installDirectories = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

# Discover every registered install location, including old per-user and
# 32/64-bit machine-wide Inno Setup entries.
$uninstallRoots = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)

foreach ($root in $uninstallRoots) {
    Get-ChildItem -LiteralPath $root | ForEach-Object {
        $entry = Get-ItemProperty -LiteralPath $_.PSPath
        if ($entry.DisplayName -like '*Printer Demon*') {
            if ($entry.InstallLocation) {
                [void]$installDirectories.Add($entry.InstallLocation)
            }
            Remove-Item -LiteralPath $_.PSPath -Recurse -Force
        }
    }
}

# Remove the known per-user and machine-wide locations used by the installers.
@(
    (Join-Path $env:LOCALAPPDATA 'Programs\Printer Demon'),
    (Join-Path $env:ProgramFiles 'Printer Demon'),
    (Join-Path ${env:ProgramFiles(x86)} 'Printer Demon')
) | ForEach-Object { [void]$installDirectories.Add($_) }

foreach ($directory in $installDirectories) {
    if ([string]::IsNullOrWhiteSpace($directory)) { continue }
    $resolved = [System.IO.Path]::GetFullPath($directory)
    if (Test-Path -LiteralPath $resolved) {
        Write-Output "Removing $resolved"
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

$shortcutRoots = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    (Join-Path $env:PUBLIC 'Desktop'),
    (Join-Path $env:USERPROFILE 'Desktop')
)

foreach ($root in $shortcutRoots) {
    if (Test-Path -LiteralPath $root) {
        Get-ChildItem -LiteralPath $root -Filter 'Printer Demon*.lnk' -File -Recurse |
            Remove-Item -Force
    }
}

Get-ChildItem -LiteralPath $env:TEMP -Filter 'PrinterDemon-update-*' -Force |
    Remove-Item -Force

Write-Output 'Printer Demon installations and leftovers removed.'
