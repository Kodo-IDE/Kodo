# VS Code equivalent benchmarks: startup, RAM, idle CPU, large-project
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class VSW {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
"@
$codeExe = "C:\Users\shrey\AppData\Local\Programs\Microsoft VS Code\Code.exe"

function Stop-Code { Get-Process Code -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1 }

function Start-Code([string]$Target) {
    Stop-Code
    $argList = @()
    if ($Target) { $argList += $Target }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $codeExe -ArgumentList $argList -PassThru
    for ($i = 0; $i -lt 120; $i++) {
        Start-Sleep -Milliseconds 250
        try { $p.Refresh() } catch {}
        if ($p.HasExited) { break }
        # find any Code window titled like VS Code
        $windows = Get-Process Code -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }
        foreach ($w in $windows) { if ([VSW]::IsWindowVisible($w.MainWindowHandle)) { break } }
        if ($windows | Where-Object { [VSW]::IsWindowVisible($_.MainWindowHandle) }) { break }
    }
    $sw.Stop()
    return @{ Process = $p; ElapsedMs = $sw.ElapsedMilliseconds }
}

"=== Startup (6 runs) ==="
$times = @()
for ($i = 0; $i -lt 6; $i++) {
    $r = Start-Code
    $times += $r.ElapsedMs
    "startup run $($i+1): $($r.ElapsedMs) ms"
    Stop-Code
    Start-Sleep -Seconds 1
}
$warm = ($times | Select-Object -Skip 1 | Sort-Object)[2]
"warm median: $warm ms (all: $($times -join ', '))"

"=== RAM welcome idle ==="
$r = Start-Code
Start-Sleep -Seconds 10
$ws = @(); $priv = @()
for ($i = 0; $i -lt 30; $i++) {
    $codeProcs = Get-Process Code -ErrorAction SilentlyContinue
    $ws += ($codeProcs | Measure-Object WorkingSet64 -Sum).Sum / 1MB
    $priv += ($codeProcs | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB
    Start-Sleep -Seconds 1
}
"welcome idle: total WS median $([math]::Round(($ws | Sort-Object)[15],1)) MB, total private median $([math]::Round(($priv | Sort-Object)[15],1)) MB across $($codeProcs.Count) processes"
Stop-Code

"=== Idle CPU (30s, all Code processes) ==="
$r = Start-Code
Start-Sleep -Seconds 10
$before = (Get-Process Code -ErrorAction SilentlyContinue | ForEach-Object { $_.TotalProcessorTime.TotalSeconds } | Measure-Object -Sum).Sum
Start-Sleep -Seconds 30
$after = (Get-Process Code -ErrorAction SilentlyContinue | ForEach-Object { $_.TotalProcessorTime.TotalSeconds } | Measure-Object -Sum).Sum
$procs = (Get-Process Code -ErrorAction SilentlyContinue).Count
$perCore = ($after - $before) / 30 * 100
"cpu $([math]::Round($perCore, 2))% of one core per process avg x$procs procs; total core-time $([math]::Round($after-$before,1))s/30s"
Stop-Code

"=== Large project (3 runs) ==="
$dir = "C:\Users\shrey\Projects\Kodo-IDE\Kodo\benchmarks\assets\large-project"
$times = @()
for ($i = 0; $i -lt 3; $i++) {
    $r = Start-Code -Target $dir
    $times += $r.ElapsedMs
    Start-Sleep -Seconds 5
    $ws = [math]::Round(((Get-Process Code -ErrorAction SilentlyContinue | Measure-Object WorkingSet64 -Sum).Sum / 1MB), 1)
    "large-project run $($i+1): $($r.ElapsedMs) ms, total WS $ws MB"
    Stop-Code
    Start-Sleep -Seconds 1
}
"large-project median: $(($times | Sort-Object)[1]) ms"
