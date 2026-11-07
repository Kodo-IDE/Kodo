# Idle CPU trace on a clean profile; converts to speedscope.
. "$PSScriptRoot\bench-common.ps1"
Confirm-KodoStopped
$stamp = New-CleanProfile
try {
    $l = Start-Kodo
    $p = $l.Process
    Start-Sleep -Seconds 45
    $traceOut = Join-Path $PSScriptRoot '..\results\idle-cpu-trace.nettrace'
    dotnet-trace collect -p $p.Id -o $traceOut --duration 00:00:10 | Out-Host
    Close-KodoGracefully -Process $p
    dotnet-trace convert $traceOut --format speedscope 2>&1 | Select-Object -Last 2
    dotnet-trace report $traceOut topN -n 14 2>&1 | Select-Object -First 20
} finally {
    Restore-Profile $stamp
}
