// Licensed under the GNU GPL-v3.0
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Kodo;

internal sealed class PosixPtyStream : Stream
{
    private const short POLLIN = 0x0001;
    private const int PollSliceMs = 200;

    private const int EINTR = 4;

    private readonly int _fd;
    private int _disposed;

    public PosixPtyStream(int fd)
    {
        if (fd < 0) throw new ArgumentOutOfRangeException(nameof(fd));
        _fd = fd;
    }

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => CanRead;

    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);
        if (count == 0) return 0;
        ThrowIfDisposed();

        var native = Marshal.AllocHGlobal(count);
        try
        {
            while (true)
            {
                var n = read(_fd, native, (nuint)count);
                if (n >= 0)
                {
                    if (n == 0) return 0;
                    Marshal.Copy(native, buffer, offset, (int)n);
                    return (int)n;
                }

                var err = errno_location() == IntPtr.Zero ? 0 : Marshal.ReadInt32(errno_location());
                if (err == EINTR) continue;
                return 0;
            }
        }
        finally { Marshal.FreeHGlobal(native); }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);
        if (count == 0) return;
        ThrowIfDisposed();

        var native = Marshal.AllocHGlobal(count);
        try
        {
            Marshal.Copy(buffer, offset, native, count);
            var written = 0;
            while (written < count)
            {
                var n = write(_fd, native + written, (nuint)(count - written));
                if (n > 0) { written += (int)n; continue; }

                var err = errno_location() == IntPtr.Zero ? 0 : Marshal.ReadInt32(errno_location());
                if (err == EINTR) continue;
                throw new IOException($"Writing to the terminal failed: {UnixPty.DescribeErrno(err)}.");
            }
        }
        finally { Marshal.FreeHGlobal(native); }

        Flush();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArgs(buffer, offset, count);
        cancellationToken.ThrowIfCancellationRequested();
        if (count == 0) return Task.FromResult(0);

        return Task.FromResult(ReadWaitingForData(buffer, offset, count, cancellationToken));
    }

    private int ReadWaitingForData(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Volatile.Read(ref _disposed) != 0) return 0;
            if (!WaitReadable(_fd, PollSliceMs)) continue;

            cancellationToken.ThrowIfCancellationRequested();
            return Read(buffer, offset, count);
        }
    }

    private static bool WaitReadable(int fd, int timeoutMs)
    {
        var pfd = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(pfd, 0, fd);
            Marshal.WriteInt16(pfd, 4, POLLIN);
            Marshal.WriteInt16(pfd, 6, 0);
            return poll(pfd, 1, timeoutMs) > 0;
        }
        finally { Marshal.FreeHGlobal(pfd); }
    }

    private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
            throw new ArgumentException("Offset and length were out of bounds for the buffer.", nameof(count));
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PosixPtyStream));
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { close(_fd); } catch { }
        }
        base.Dispose(disposing);
    }

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint read(int fd, IntPtr buffer, nuint count);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint write(int fd, IntPtr buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(IntPtr fds, nuint nfds, int timeout);

    [DllImport("libc", EntryPoint = "__errno_location", SetLastError = true)]
    private static extern IntPtr errno_location();
}
