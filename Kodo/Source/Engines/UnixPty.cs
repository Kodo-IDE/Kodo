// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Kodo;

internal static class UnixPty
{
    private const int O_RDWR = 2;
    private const int O_NOCTTY = 0x100;
    private const ulong TIOCSWINSZ = 0x5414;
    private const int SIGWINCH = 28;
    private const int SIGTERM = 15;
    private const int SIGKILL = 9;
    private const int EPERM = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr ptsname(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, IntPtr arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);

    private static readonly string[] SetsidCandidates = ["/usr/bin/setsid", "/bin/setsid"];

    public static bool TrySpawn(
        string shellPath,
        string arguments,
        string workingDirectory,
        int cols,
        int rows,
        out int masterFd,
        out Process? process,
        out string? error)
    {
        masterFd = -1;
        process = null;
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(shellPath))
            {
                error = "No shell path was provided.";
                return false;
            }
            if (!File.Exists(shellPath))
            {
                error = $"Shell not found: {shellPath}";
                return false;
            }

            cols = Math.Clamp(cols, 2, 1000);
            rows = Math.Clamp(rows, 2, 1000);

            var master = posix_openpt(O_RDWR | O_NOCTTY);
            if (master < 0) { error = ErrnoText("posix_openpt"); return false; }

            var keepMasterOpen = true;
            try
            {
                if (grantpt(master) != 0) { error = ErrnoText("grantpt"); return false; }
                if (unlockpt(master) != 0) { error = ErrnoText("unlockpt"); return false; }

                var slavePtr = ptsname(master);
                if (slavePtr == IntPtr.Zero) { error = ErrnoText("ptsname"); return false; }
                var slavePath = Marshal.PtrToStringAnsi(slavePtr);
                if (string.IsNullOrEmpty(slavePath)) { error = "ptsname returned an empty path."; return false; }

                SetWinsize(master, cols, rows);

                var inner = BuildInnerCommand(shellPath, arguments, workingDirectory, slavePath);
                var setsid = FindSetsid();

                var psi = new ProcessStartInfo
                {
                    FileName = setsid ?? "/bin/sh",
                    UseShellExecute = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                if (setsid is null)
                {
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(inner);
                    KodoDiagnostics.LogDebug("[PTY] setsid not found; shell will run without job control.");
                }
                else
                {
                    psi.ArgumentList.Add("/bin/sh");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(inner);
                }

                psi.Environment["TERM"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERM"))
                    ? Environment.GetEnvironmentVariable("TERM")!
                    : "xterm-256color";

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.Start();

                masterFd = master;
                keepMasterOpen = false;
                process = proc;

                _ = Task.Run(() => DrainLauncherOutputAsync(proc, inner));

                KodoDiagnostics.LogDebug(
                    $"Terminal PTY started (pid {proc.Id}, shell '{shellPath} {arguments}', " +
                    $"cwd '{workingDirectory}', slave {slavePath}, {cols}x{rows}, setsid={setsid is not null}).");

                return true;
            }
            finally
            {
                if (keepMasterOpen)
                {
                    try { close(master); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string BuildInnerCommand(string shellPath, string arguments, string workingDirectory, string slavePath)
    {
        var slave = Quote(slavePath);
        var cd = string.Empty;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            cd = $"cd {Quote(workingDirectory)} 2>/dev/null; ";

        return $"{cd}exec {Quote(shellPath)} {arguments} < {slave} > {slave} 2>&1";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string? FindSetsid()
    {
        foreach (var candidate in SetsidCandidates)
        {
            try { if (File.Exists(candidate)) return candidate; } catch { }
        }
        return null;
    }

    private static async Task DrainLauncherOutputAsync(Process proc, string innerCommand)
    {
        try
        {
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            var noise = string.Join(' ', new[] { await stdoutTask, await stderrTask }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim()))
                .Trim();

            if (noise.Length == 0) return;

            KodoDiagnostics.LogWarning(
                "UnixPty.TrySpawn",
                new InvalidOperationException(
                    $"The terminal launcher reported: {noise} (command: {innerCommand})"),
                operation: "Linux terminal spawn");
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("[PTY] Launcher output drain failed.", ex);
        }
    }

    public static void SetWinsize(int fd, int cols, int rows)
    {
        if (fd < 0) return;
        try
        {
            var ws = new Winsize
            {
                ws_row = (ushort)Math.Clamp(rows, 1, 1000),
                ws_col = (ushort)Math.Clamp(cols, 1, 1000),
                ws_xpixel = 0,
                ws_ypixel = 0
            };
            ioctl(fd, TIOCSWINSZ, ref ws);
        }
        catch { }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref Winsize ws);

    public static void SendSigwinch(int pid)
    {
        if (pid <= 0) return;
        try { kill(pid, SIGWINCH); } catch { }
    }

    public static void Resize(int masterFd, int childPid, int cols, int rows)
    {
        SetWinsize(masterFd, cols, rows);
        SendSigwinch(childPid);
    }

    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            if (kill(pid, 0) == 0) return true;
            return Marshal.GetLastWin32Error() == EPERM;
        }
        catch { return false; }
    }

    public static void Kill(int pid)
    {
        if (pid <= 0) return;
        try { kill(pid, SIGTERM); } catch { }
    }

    public static void KillGroup(int pid, bool force = false)
    {
        if (pid <= 0) return;
        var sig = force ? SIGKILL : SIGTERM;

        var ownsGroup = false;
        try { ownsGroup = getpgid(pid) == pid; } catch { }
        if (ownsGroup)
        {
            try { kill(-pid, sig); } catch { }
        }

        try { kill(pid, sig); } catch { }
    }

    private static string ErrnoText(string call)
    {
        var errno = Marshal.GetLastWin32Error();
        return $"{call} failed: errno {errno} ({DescribeErrno(errno)})";
    }

    internal static string DescribeErrno(int errno) => errno switch
    {
        1 => "operation not permitted",
        2 => "no such file or directory",
        3 => "no such process",
        7 => "argument list too long",
        8 => "exec format error",
        12 => "out of memory",
        13 => "permission denied",
        20 => "not a directory",
        21 => "is a directory",
        26 => "text file busy",
        36 => "file name too long",
        40 => "too many levels of symbolic links",
        _ => "unknown error"
    };
}
