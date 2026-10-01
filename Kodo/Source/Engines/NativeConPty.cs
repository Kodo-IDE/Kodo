// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Kodo.Models;

namespace Kodo;

internal static class NativeConPty
{
    [StructLayout(LayoutKind.Sequential)]
    public struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int STARTF_USESTDHANDLES = 0x100;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out IntPtr hRead, out IntPtr hWrite, IntPtr lpAttr, int nSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr hPcon);

    [DllImport("kernel32.dll")]
    public static extern void ClosePseudoConsole(IntPtr hPcon);

    [DllImport("kernel32.dll")]
    public static extern int ResizePseudoConsole(IntPtr hPcon, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr val, IntPtr size, IntPtr prev, IntPtr retSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? app, StringBuilder cmd,
        IntPtr pAttr, IntPtr tAttr,
        bool inherit, uint flags,
        IntPtr env, string? dir,
        ref STARTUPINFOEXW si, out PROCESS_INFORMATION pi);

    public static void LaunchProcess(StringBuilder cmdLine, string workingDir, IntPtr hPcon,
        out IntPtr hProcess, out IntPtr hThread)
    {
        var attrListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        var attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                throw new InvalidOperationException($"InitializeProcThreadAttributeList: {Marshal.GetLastWin32Error()}");

            var hPconLocal = hPcon;
            var hPconPtr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(hPconPtr, hPconLocal);
            try
            {
                if (!UpdateProcThreadAttribute(attrList, 0,
                        (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        hPconLocal, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new InvalidOperationException($"UpdateProcThreadAttribute: {Marshal.GetLastWin32Error()}");

                var si = new STARTUPINFOEXW
                {
                    StartupInfo = new STARTUPINFOW
                    {
                        cb = Marshal.SizeOf<STARTUPINFOEXW>(),
                        dwFlags = 0
                    },
                    lpAttributeList = attrList
                };

                if (!CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDir, ref si, out var pi))
                    throw new InvalidOperationException($"CreateProcessW: {Marshal.GetLastWin32Error()}");

                hProcess = pi.hProcess;
                hThread = pi.hThread;
            }
            finally { Marshal.FreeHGlobal(hPconPtr); }
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }
}
