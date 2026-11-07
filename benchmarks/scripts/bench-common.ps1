# Shared harness for Kodo benchmark scripts.
# Publish the app first:
#   dotnet publish Kodo\Source\Kodo.csproj -c Release -r win-x64 --self-contained true -o "$env:TEMP\opencode\kodo-bench\publish"
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class BenchW {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
}
"@

function Get-KodoPublishExe { if ($env:KODO_BENCH_EXE) { return $env:KODO_BENCH_EXE } "$env:TEMP\opencode\kodo-bench\publish\Kodo.exe" }

function Confirm-KodoStopped {
    Get-Process Kodo -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

function Start-Kodo {
    param([string]$Target)
    $exe = Get-KodoPublishExe
    $argList = @()
    if ($Target) { $argList += $Target }
    $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero -and [BenchW]::IsWindowVisible($p.MainWindowHandle)) { break }
    }
    [pscustomobject]@{ Process = $p }
}

function Take-KodoForeground($hwnd) {
    Add-Type -AssemblyName Microsoft.VisualBasic
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class BenchMouse { [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo); }
"@
    for ($i = 0; $i -lt 15; $i++) {
        [BenchW]::ShowWindow($hwnd, 9) | Out-Null
        [BenchW]::BringWindowToTop($hwnd) | Out-Null
        try {
            $proc = Get-Process Kodo -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($proc) { [Microsoft.VisualBasic.Interaction]::AppActivate($proc.Id) | Out-Null }
        } catch {}
        Start-Sleep -Milliseconds 300
        if ([BenchW]::SetForegroundWindow($hwnd)) { return $true }
        [BenchMouse]::mouse_event(0x0001, 800, 600, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 50
        [BenchMouse]::mouse_event(0x0002, 800, 600, 0, [UIntPtr]::Zero)
        $targetThread = [BenchW]::GetWindowThreadProcessId($hwnd, [ref]0)
        $currentThread = [BenchW]::GetCurrentThreadId()
        [BenchW]::AttachThreadInput($currentThread, $targetThread, $true) | Out-Null
        try {
            if ([BenchW]::SetForegroundWindow($hwnd)) { return $true }
        } finally {
            [BenchW]::AttachThreadInput($currentThread, $targetThread, $false) | Out-Null
        }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function Close-KodoGracefully {
    param($Process)
    try { $Process.CloseMainWindow() | Out-Null } catch {}
    if (-not $Process.WaitForExit(5000)) {
        try { Stop-Process -Id $Process.Id -Force } catch {}
    }
}

function Get-CpuSample($processId, [int]$seconds = 30) {
    $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if (-not $proc) { return 0 }
    $start = $proc.TotalProcessorTime.TotalSeconds
    Start-Sleep -Seconds $seconds
    $proc.Refresh()
    if ($proc.HasExited) { return 0 }
    return ($proc.TotalProcessorTime.TotalSeconds - $start) / $seconds
}

function New-CleanProfile {
    $local = "$env:LOCALAPPDATA\Kodo"
    $roam = "$env:APPDATA\Kodo"
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    foreach ($p in @($local, $roam)) {
        if (Test-Path $p) { Move-Item $p "$p.bak-$stamp" -Force }
    }
    $seed = '{"HasCompletedTutorial":true,"HasRespondedToDataTrackingPrompt":true,"HasAcceptedPrivacyPolicy":true}'
    New-Item -ItemType Directory -Force $local | Out-Null
    Set-Content -Path "$local\kodosettings.json" -Value $seed -Encoding UTF8
    return $stamp
}

function Restore-Profile($stamp) {
    $local = "$env:LOCALAPPDATA\Kodo"
    $roam = "$env:APPDATA\Kodo"
    Get-Process Kodo -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    if (Test-Path $local) { Remove-Item $local -Recurse -Force }
    if (Test-Path $roam) { Remove-Item $roam -Recurse -Force }
    foreach ($p in @($local, $roam)) {
        $bak = Get-ChildItem -Path (Split-Path $p) -Directory -Filter "Kodo.bak-$stamp" -ErrorAction SilentlyContinue
        if ($bak) { Move-Item $bak.FullName $p -Force }
    }
}
