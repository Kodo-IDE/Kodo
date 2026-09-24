// Licensed under GPL v3.0
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kodo;

internal static class UnixPty
{
    private const int O_RDWR = 2;
    private const int O_NOCTTY = 0x100;
    private const ulong TIOCSWINSZ = 0x5414;
    private const ulong TIOCSCTTY = 0x540E;
    private const int SIGWINCH = 28;
    private const int SIGTERM = 15;
    private const int SIGKILL = 9;
    private const int WNOHANG = 1;

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

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref Winsize ws);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, IntPtr arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int fork();

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int execvp(string file, IntPtr argv);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int chdir(string path);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);

    [DllImport("libc")]
    private static extern void _exit(int status);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    public static bool TrySpawn(string shellPath, string arguments, string workingDirectory, int cols, int rows, out int masterFd, out int childPid)
    {
        masterFd = -1;
        childPid = -1;
        try
        {
            if (string.IsNullOrWhiteSpace(shellPath) || !System.IO.File.Exists(shellPath))
                return false;

            cols = Math.Clamp(cols, 2, 1000);
            rows = Math.Clamp(rows, 2, 1000);

            var master = posix_openpt(O_RDWR | O_NOCTTY);
            if (master < 0) return false;
            try
            {
                if (grantpt(master) != 0) { close(master); return false; }
                if (unlockpt(master) != 0) { close(master); return false; }
                var slaveNamePtr = ptsname(master);
                if (slaveNamePtr == IntPtr.Zero) { close(master); return false; }
                var slavePath = Marshal.PtrToStringAnsi(slaveNamePtr);
                if (string.IsNullOrEmpty(slavePath)) { close(master); return false; }

                SetWinsize(master, cols, rows);

                var argvList = BuildArgv(shellPath, arguments);
                var argvPtrs = new List<IntPtr>(argvList.Count + 1);
                try
                {
                    foreach (var a in argvList)
                        argvPtrs.Add(Marshal.StringToHGlobalAnsi(a));
                    argvPtrs.Add(IntPtr.Zero);
                    var argvArray = Marshal.AllocHGlobal(IntPtr.Size * argvPtrs.Count);
                    try
                    {
                        for (var i = 0; i < argvPtrs.Count; i++)
                            Marshal.WriteIntPtr(argvArray, i * IntPtr.Size, argvPtrs[i]);

                        var pid = fork();
                        if (pid < 0)
                        {
                            close(master);
                            return false;
                        }

                        if (pid == 0)
                        {
                            try
                            {
                                setsid();
                                var slave = open(slavePath, O_RDWR);
                                if (slave < 0) _exit(1);
                                ioctl(slave, TIOCSCTTY, IntPtr.Zero);
                                SetWinsize(slave, cols, rows);
                                dup2(slave, 0);
                                dup2(slave, 1);
                                dup2(slave, 2);
                                if (slave > 2) close(slave);
                                close(master);
                                if (!string.IsNullOrWhiteSpace(workingDirectory))
                                {
                                    try { chdir(workingDirectory); } catch { }
                                }
                                try { setenv("TERM", "xterm-256color", 0); } catch { }
                                execvp(shellPath, argvArray);
                                _exit(127);
                            }
                            catch { _exit(127); }
                            _exit(127);
                            return false;
                        }

                        masterFd = master;
                        childPid = pid;
                        return true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(argvArray);
                    }
                }
                finally
                {
                    foreach (var p in argvPtrs)
                        if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
                }
            }
            catch
            {
                try { close(master); } catch { }
                return false;
            }
        }
        catch
        {
            return false;
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
            var r = kill(pid, 0);
            if (r == 0) return true;
            var err = Marshal.GetLastWin32Error();
            return err == 1;
        }
        catch { return false; }
    }

    public static bool TryReap(int pid)
    {
        if (pid <= 0) return true;
        try
        {
            var r = waitpid(pid, out _, WNOHANG);
            return r == pid || !IsAlive(pid);
        }
        catch { return !IsAlive(pid); }
    }

    public static void Kill(int pid)
    {
        if (pid <= 0) return;
        try { kill(pid, SIGTERM); } catch { }
    }

    public static void KillGroup(int pid, bool force = false)
    {
        if (pid <= 0) return;
        try { kill(-pid, force ? SIGKILL : SIGTERM); } catch { }
        try { kill(pid, force ? SIGKILL : SIGTERM); } catch { }
    }

    private static List<string> BuildArgv(string shellPath, string arguments)
    {
        var argv = new List<string> { shellPath };
        foreach (var a in SplitArguments(arguments))
            argv.Add(a);
        return argv;
    }

    internal static IEnumerable<string> SplitArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) yield break;
        var sb = new System.Text.StringBuilder();
        char? quote = null;
        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else if (c == '\\' && i + 1 < arguments.Length) { i++; sb.Append(arguments[i]); }
                else sb.Append(c);
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else if (c == '\\' && i + 1 < arguments.Length)
            {
                i++; sb.Append(arguments[i]);
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
