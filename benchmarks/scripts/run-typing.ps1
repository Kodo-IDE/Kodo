# Typing latency benchmark (needs instrumented build in worktree bench-typing-latency).
# Usage: pwsh -File benchmarks/scripts/run-typing.ps1
. "$PSScriptRoot\bench-common.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Text.RegularExpressions

$worktreeExe = "C:\Users\shrey\.local\share\opencode\worktree\6cfe0d\bench-typing-latency\Kodo\Source\bin\Release\net10.0\win-x64\Kodo.exe"
if (-not (Test-Path $worktreeExe)) {
    Write-Host "instrumented build not found at $worktreeExe - building worktree first"
    dotnet publish "C:\Users\shrey\.local\share\opencode\worktree\6cfe0d\bench-typing-latency\Kodo\Source\Kodo.csproj" -c Release -r win-x64 --self-contained true -o "$env:TEMP\opencode\kodo-bench\typing-publish" --nologo
    $worktreeExe = "$env:TEMP\opencode\kodo-bench\typing-publish\Kodo.exe"
}

function Test-Typing([string]$file, [string]$label, [string]$scenario) {
    Confirm-KodoStopped
    $env:KODO_BENCH_SCENARIO = $scenario
    $p = Start-Process -FilePath $worktreeExe -ArgumentList $file -PassThru
    Remove-Item Env:KODO_BENCH_SCENARIO -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero -and [BenchW]::IsWindowVisible($p.MainWindowHandle)) { break }
    }
    Start-Sleep -Seconds 10
    if (-not (Take-KodoForeground $p.MainWindowHandle)) { Write-Host "typing ($label): focus failed, aborting run"; return }
    Start-Sleep -Milliseconds 600
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class BenchMouse2 { [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo); }
"@
    $cx = [uint32](700 * 65535 / [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width)
    $cy = [uint32](400 * 65535 / [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
    [BenchMouse2]::mouse_event(0x8001, $cx, $cy, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [BenchMouse2]::mouse_event(0x8002, $cx, $cy, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [BenchMouse2]::mouse_event(0x8004, $cx, $cy, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 600
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
    Start-Sleep -Milliseconds 600
    $batch = 'the quick brown fox jumps over the lazy dog; '
    for ($b = 0; $b -lt 14; $b++) {
        $foreground = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
        if (-not $foreground -or $foreground.MainWindowTitle -notmatch 'Kodo') { break }
        [System.Windows.Forms.SendKeys]::SendWait($batch)
        Start-Sleep -Milliseconds 200
    }
    Start-Sleep -Seconds 3
    Close-KodoGracefully -Process $p
    $report = Get-ChildItem "$env:APPDATA\Kodo\editor-performance-*-typing.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($report) {
        $j = Get-Content $report.FullName -Raw | ConvertFrom-Json
        Copy-Item $report.FullName (Join-Path $PSScriptRoot "..\results\$($report.Name)") -Force
        $observed = $j.session.keystrokesObserved
        $lat = $j.measurements.typingLatency.stats
        $hb = $j.measurements.textChangedHandlerBody.stats
        $dl = $j.measurements.dispatcherLateness.stats
        "typing ($label): keystrokes=$observed, keyToHandler mean=$($lat.MeanMilliseconds) p50=$($lat.P50Milliseconds) p95=$($lat.P95Milliseconds) max=$($lat.MaximumMilliseconds) ms; handler p95=$($hb.P95Milliseconds) ms; dispatcher p95=$($dl.P95Milliseconds) max=$($dl.MaximumMilliseconds) ms"
    } else {
        "typing ($label): no report found in %APPDATA%\Kodo"
    }
}

$small = "$env:TEMP\opencode\kodo-bench\typing-small.txt"
Set-Content -Path $small -Value ((1..800 | ForEach-Object { "line $_ the quick brown fox" }) -join "`n") -Encoding UTF8
Test-Typing $small 'small file' 'smallfile'
Remove-Item $small -ErrorAction SilentlyContinue
