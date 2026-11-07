# Idle CPU measurement, clean + real profile
. "C:\Users\shrey\Projects\Kodo-IDE\Kodo\benchmarks\scripts\bench-common.ps1"

function Measure-Idle([string]$label) {
    Confirm-KodoStopped
    $l = Start-Kodo
    Start-Sleep -Seconds 15
    $p = $l.Process
    $p.Refresh()
    $start = $p.TotalProcessorTime.TotalSeconds
    $ws = @()
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 1
        $p.Refresh()
        $ws += $p.WorkingSet64 / 1MB
    }
    $cpu = ($p.TotalProcessorTime.TotalSeconds - $start) / 40
    $wsMedian = ($ws | Sort-Object)[20]
    "$label : cpu $([math]::Round($cpu * 100, 1))% of one core | WS median $([math]::Round($wsMedian,1)) MB"
    Close-KodoGracefully -Process $p
}

"--- clean profile ---"
$stamp = New-CleanProfile
try { Measure-Idle 'clean' } finally { Restore-Profile $stamp }

"--- real profile ---"
Measure-Idle 'profile'
