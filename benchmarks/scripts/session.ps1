# External benchmarks: startup, RAM, large-project.
# Usage: pwsh -File benchmarks/scripts/session.ps1 -Stages startup,ram,largeproject
param([string[]]$Stages = @('startup','ram','largeproject'))
$Stages = $Stages | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() }

. "$PSScriptRoot\bench-common.ps1"

$resultsDir = Join-Path $PSScriptRoot '..\results'
New-Item -ItemType Directory -Force $resultsDir | Out-Null

function Measure-Startup([int]$runs = 6) {
    $times = @()
    for ($i = 0; $i -lt $runs; $i++) {
        Confirm-KodoStopped
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $l = Start-Kodo
        $sw.Stop()
        $times += $sw.ElapsedMilliseconds
        Write-Host "startup run $($i+1): $($sw.ElapsedMilliseconds) ms"
        Close-KodoGracefully -Process $l.Process
        Start-Sleep -Seconds 1
    }
    $warm = ($times | Select-Object -Skip 1 | Sort-Object)[[int](($times.Count - 2) / 2)]
    Write-Host "warm median: $warm ms (all: $($times -join ', '))"
    return $times
}

function Measure-Ram {
    Confirm-KodoStopped
    $l = Start-Kodo
    Start-Sleep -Seconds 8
    $ws = @(); $priv = @()
    for ($i = 0; $i -lt 30; $i++) {
        $l.Process.Refresh()
        $ws += $l.Process.WorkingSet64 / 1MB
        $priv += $l.Process.PrivateMemorySize64 / 1MB
        Start-Sleep -Seconds 1
    }
    $wsMedian = ($ws | Sort-Object)[[int]($ws.Count / 2)]
    $privMedian = ($priv | Sort-Object)[[int]($priv.Count / 2)]
    Write-Host "welcome idle: WS median $wsMedian MB, private median $privMedian MB"
    Close-KodoGracefully -Process $l.Process
    return @{ Ws = $wsMedian; Private = $privMedian }
}

function New-LargeProject([string]$dir) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    $rng = [System.Random]::new(42)
    for ($f = 0; $f -lt 2500; $f++) {
        $sub = Join-Path $dir "src\d$($f % 11)"
        New-Item -ItemType Directory -Force $sub | Out-Null
        $lines = foreach ($i in 1..20) { "line $i of file $f " + ('x' * 40) }
        Set-Content -Path (Join-Path $sub "file$f.cs") -Value ($lines -join "`n") -Encoding UTF8
    }
    $module = Join-Path $dir "module.bin"
    $bytes = New-Object byte[] (48 * 1024 * 1024)
    $rng.NextBytes($bytes)
    [System.IO.File]::WriteAllBytes($module, $bytes)
    $size = (Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host "large-project: 2501 files, $([math]::Round($size,1)) MB at $dir"
}

function Measure-LargeProject {
    $dir = Join-Path $PSScriptRoot '..\assets\large-project'
    New-LargeProject $dir
    $times = @()
    for ($i = 0; $i -lt 3; $i++) {
        Confirm-KodoStopped
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $l = Start-Kodo -Target $dir
        $sw.Stop()
        $times += $sw.ElapsedMilliseconds
        Start-Sleep -Seconds 5
        $l.Process.Refresh()
        $ws = [math]::Round($l.Process.WorkingSet64 / 1MB, 1)
        Write-Host "large-project run $($i+1): $($sw.ElapsedMilliseconds) ms, WS $ws MB"
        Close-KodoGracefully -Process $l.Process
        Start-Sleep -Seconds 1
    }
    $median = ($times | Sort-Object)[1]
    Write-Host "large-project median: $median ms"
    return $times
}

$stamp = New-CleanProfile
try {
    if ($Stages -contains 'startup') { Measure-Startup | Out-Null }
    if ($Stages -contains 'ram') { Measure-Ram | Out-Null }
    if ($Stages -contains 'largeproject') { Measure-LargeProject | Out-Null }
} finally {
    Restore-Profile $stamp
}
"session complete"
