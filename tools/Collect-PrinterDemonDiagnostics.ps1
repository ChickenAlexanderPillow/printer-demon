$ErrorActionPreference = 'SilentlyContinue'

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path ([Environment]::GetFolderPath('Desktop')) "PrinterDemon-Diagnostics-$stamp"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$report = Join-Path $outDir 'report.txt'

function Section([string] $title) {
    Add-Content -LiteralPath $report -Value "`r`n===== $title ====="
}

function Write-Report([object] $value) {
    $value | Out-String -Width 240 | Add-Content -LiteralPath $report
}

"Printer Demon diagnostics" | Set-Content -LiteralPath $report
"Created: $(Get-Date -Format o)" | Add-Content -LiteralPath $report
"Computer: $env:COMPUTERNAME" | Add-Content -LiteralPath $report

Section 'Windows'
Write-Report (Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime)

Section 'Printer queues'
$queues = Get-Printer | Where-Object {
    $_.Name -match 'Xerox|VersaLink|C600' -or $_.DriverName -match 'Xerox|VersaLink|C600'
}
Write-Report ($queues | Select-Object Name, DriverName, PortName, PrinterStatus, WorkOffline, Shared, Published, ComputerName)

foreach ($queue in $queues) {
    $safeName = ($queue.Name -replace '[^A-Za-z0-9._-]', '_')
    $queueFile = Join-Path $outDir "$safeName.txt"
    "Queue: $($queue.Name)" | Set-Content -LiteralPath $queueFile

    '--- Print configuration ---' | Add-Content -LiteralPath $queueFile
    Get-PrintConfiguration -PrinterName $queue.Name | Format-List * | Out-String -Width 240 | Add-Content -LiteralPath $queueFile

    '--- Driver ---' | Add-Content -LiteralPath $queueFile
    Get-PrinterDriver -Name $queue.DriverName | Format-List * | Out-String -Width 240 | Add-Content -LiteralPath $queueFile

    '--- Printer properties ---' | Add-Content -LiteralPath $queueFile
    Get-PrinterProperty -PrinterName $queue.Name | Format-List * | Out-String -Width 240 | Add-Content -LiteralPath $queueFile

    try {
        Add-Type -AssemblyName ReachFramework
        $server = New-Object System.Printing.LocalPrintServer
        $printQueue = $server.GetPrintQueue($queue.Name)
        '--- WPF queue state ---' | Add-Content -LiteralPath $queueFile
        [pscustomobject]@{
            FullName = $printQueue.FullName
            IsOffline = $printQueue.IsOffline
            QueueStatus = $printQueue.QueueStatus
            DriverName = $printQueue.QueueDriver.Name
            DefaultTicketXml = $null
        } | Format-List * | Out-String -Width 240 | Add-Content -LiteralPath $queueFile

        $stream = $printQueue.DefaultPrintTicket.GetXmlStream()
        $stream.Position = 0
        $ticketXml = New-Object System.IO.StreamReader($stream)
        $ticketXml.ReadToEnd() | Set-Content -LiteralPath (Join-Path $outDir "$safeName-print-ticket.xml")
        $ticketXml.Dispose()
        $stream.Dispose()
        $printQueue.Dispose()
        $server.Dispose()
    } catch {
        "WPF queue inspection failed: $($_.Exception.Message)" | Add-Content -LiteralPath $queueFile
    }
}

Section 'Ghostscript'
$gsCandidates = @(
    (Join-Path $PSScriptRoot 'ghostscript\installed\bin\gswin64c.exe'),
    (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\ghostscript\installed\bin\gswin64c.exe'),
    'C:\Program Files\gs\gs*\bin\gswin64c.exe'
)
foreach ($candidate in $gsCandidates) {
    Get-ChildItem -Path $candidate -File | ForEach-Object {
        Write-Report $_.FullName
        & $_.FullName -version 2>&1 | Select-Object -First 3 | Add-Content -LiteralPath $report
    }
}

Section 'Printer spooler'
Write-Report (Get-Service Spooler | Select-Object Name, Status, StartType)

Section 'Recent print errors'
$events = Get-WinEvent -LogName 'Microsoft-Windows-PrintService/Admin' -MaxEvents 100 |
    Where-Object { $_.LevelDisplayName -in @('Error', 'Warning') } |
    Select-Object TimeCreated, Id, LevelDisplayName, ProviderName, Message
Write-Report $events

Section 'Recent system print errors'
$systemEvents = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'PrintService'; StartTime = (Get-Date).AddDays(-7) } -MaxEvents 50 |
    Select-Object TimeCreated, Id, LevelDisplayName, ProviderName, Message
Write-Report $systemEvents

Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath "$outDir.zip" -Force
Write-Host "Diagnostics written to: $outDir.zip"
