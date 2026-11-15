// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kodo;

internal static class InsightWorker
{
    public static bool IsWorkerRequest(string[] args)
        => args is { Length: > 0 } && Array.IndexOf(args, "--insight-worker") >= 0;

    public static int Run(string[] args)
    {
        try
        {
            var engine = new InsightEngine();
            var registry = new List<LoadedExtension>();
            using var stdin = Console.OpenStandardInput();
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            using var requests = new BlockingCollection<WorkerRequest>();
            var activeRequests = new ConcurrentDictionary<int, CancellationTokenSource>();
            var outputLock = new object();
            var processor = Task.Run(() => ProcessRequests(engine, registry, requests, activeRequests, stdout, outputLock));

            var buffer = new byte[8192];
            int bufPos = 0, bufLen = 0;
            var headerBuilder = new StringBuilder();

            try
            {
                while (ReadHeaderLine(stdin, buffer, ref bufPos, ref bufLen, headerBuilder) is { } line)
                {
                    if (line.Length == 0) continue;
                    WorkerRequest? request = null;
                    try
                    {
                        request = JsonSerializer.Deserialize<WorkerRequest>(line, WorkerExtensionSnapshot.Options);
                    }
                    catch (Exception ex)
                    {
                        lock (outputLock) Write(stdout, new WorkerResponse { Id = 0, Ok = false, Message = $"bad request payload: {ex.Message}" });
                        continue;
                    }
                    if (request is null) continue;
                    if (request.TextByteLength > 0)
                        request.Text = ReadUtf8Text(stdin, buffer, ref bufPos, ref bufLen, request.TextByteLength);

                    if (request.Op.Equals("cancel", StringComparison.Ordinal))
                    {
                        if (activeRequests.TryGetValue(request.CancelRequestId, out var cancellation))
                        {
                            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
                        }
                        continue;
                    }

                    requests.Add(request);
                }
            }
            finally { requests.CompleteAdding(); }
            processor.GetAwaiter().GetResult();
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static void ProcessRequests(
        InsightEngine engine,
        List<LoadedExtension> registry,
        BlockingCollection<WorkerRequest> requests,
        ConcurrentDictionary<int, CancellationTokenSource> activeRequests,
        StreamWriter stdout,
        object outputLock)
    {
        foreach (var request in requests.GetConsumingEnumerable())
        {
            var response = new WorkerResponse { Id = request.Id, Ok = true };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            activeRequests[request.Id] = cts;
            try
            {
                var ct = cts.Token;
                if (request.Registry is not null)
                {
                    registry.Clear();
                    foreach (var snapshot in request.Registry)
                        registry.Add(snapshot.ToLoadedExtension());
                }
                switch (request.Op)
                {
                    case "ping":
                    case "sync":
                        break;
                    case "scan":
                        {
                            var ext = request.Extension?.ToLoadedExtension();
                            var key = RequireKey(request);
                            engine.ScanDocument(key, request.Text ?? string.Empty, ext, ct);
                            response.Variables = [.. engine.GetVariables(key)];
                            break;
                        }
                    case "dead":
                        {
                            var ext = request.Extension?.ToLoadedExtension();
                            var key = RequireKey(request);
                            var spans = engine.FindDeadCode(request.Text ?? string.Empty, ext, request.FolderPath, key, ct, key);
                            response.Dead = new List<WorkerSpan>(spans.Count);
                            foreach (var span in spans)
                                response.Dead.Add(new WorkerSpan { Start = span.StartOffset, Length = span.Length, Reason = span.Reason });
                            break;
                        }
                    case "errs":
                        {
                            var ext = request.Extension?.ToLoadedExtension();
                            var spans = engine.FindErrors(request.Text ?? string.Empty, ext, fence => ResolveFence(registry, fence), ct, request.DocumentContext, request.WorkspaceContext);
                            response.Errors = new List<WorkerErrorSpan>(spans.Count);
                            foreach (var span in spans)
                                response.Errors.Add(new WorkerErrorSpan
                                {
                                    Start = span.StartOffset,
                                    Length = span.Length,
                                    Severity = span.Severity,
                                    Code = span.Code,
                                    Message = span.Message,
                                    Source = span.Source,
                                    Details = span.Details
                                });
                            break;
                        }
                    case "forget":
                        if (!string.IsNullOrEmpty(request.Key)) engine.ForgetFile(request.Key!);
                        break;
                    default:
                        response.Ok = false;
                        response.Message = $"unknown op '{request.Op}'";
                        break;
                }
            }
            catch (Exception ex)
            {
                response.Ok = false;
                response.Message = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally { activeRequests.TryRemove(request.Id, out _); }
            lock (outputLock) Write(stdout, response);
        }
    }

    private static string RequireKey(WorkerRequest request)
        => string.IsNullOrEmpty(request.Key) ? "untitled" : request.Key!;

    private static LoadedExtension? ResolveFence(List<LoadedExtension> registry, string fenceLanguage)
        => FenceLanguageResolver.Resolve(registry, fenceLanguage);

    private static void Write(StreamWriter stdout, WorkerResponse response)
        => stdout.WriteLine(JsonSerializer.Serialize(response, WorkerExtensionSnapshot.Options));

    private static int ReadByte(Stream stdin, byte[] buffer, ref int pos, ref int len)
    {
        if (pos >= len)
        {
            len = stdin.Read(buffer, 0, buffer.Length);
            pos = 0;
            if (len <= 0) return -1;
        }
        return buffer[pos++];
    }

    private static bool ReadBytesExact(Stream stdin, byte[] buffer, ref int pos, ref int len, byte[] destination, int destinationOffset, int destinationCount)
    {
        var offset = destinationOffset;
        while (offset < destinationOffset + destinationCount)
        {
            if (pos < len)
            {
                var take = Math.Min(len - pos, destinationOffset + destinationCount - offset);
                Buffer.BlockCopy(buffer, pos, destination, offset, take);
                pos += take;
                offset += take;
                continue;
            }
            var read = stdin.Read(destination, offset, destinationOffset + destinationCount - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    private static string? ReadHeaderLine(Stream stdin, byte[] buffer, ref int pos, ref int len, StringBuilder builder)
    {
        builder.Clear();
        while (true)
        {
            var b = ReadByte(stdin, buffer, ref pos, ref len);
            if (b < 0) return builder.Length == 0 ? null : builder.ToString();
            if (b == '\n') return builder.ToString();
            if (b != '\r') builder.Append((char)b);
        }
    }

    private static string ReadUtf8Text(Stream stdin, byte[] buffer, ref int pos, ref int len, long byteLength)
    {
        if (byteLength > int.MaxValue) throw new IOException("insight worker text frame too large");
        var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent((int)byteLength);
        try
        {
            if (!ReadBytesExact(stdin, buffer, ref pos, ref len, bytes, 0, (int)byteLength))
                throw new IOException("unexpected end of insight worker stdin");
            return Encoding.UTF8.GetString(bytes, 0, (int)byteLength);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}
