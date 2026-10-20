// Licensed under the GNU GPL-v3.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Kodo.Models;

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
            List<LoadedExtension>? registry = [];
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

            while (stdin.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                WorkerRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<WorkerRequest>(line, WorkerExtensionSnapshot.Options);
                }
                catch (Exception ex)
                {
                    Write(stdout, new WorkerResponse { Id = 0, Ok = false, Message = $"bad request payload: {ex.Message}" });
                    continue;
                }
                if (request is null) continue;

                var response = new WorkerResponse { Id = request.Id, Ok = true };
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    var ct = cts.Token;
                    if (request.Registry is not null) registry ??= [];
                    if (request.Registry is not null)
                    {
                        registry!.Clear();
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
                                foreach (var s in spans)
                                    response.Dead.Add(new WorkerSpan { Start = s.StartOffset, Length = s.Length, Reason = s.Reason });
                                break;
                            }

                        case "errs":
                            {
                                var ext = request.Extension?.ToLoadedExtension();
                                var spans = engine.FindErrors(request.Text ?? string.Empty, ext, fence => ResolveFence(registry, fence), ct);
                                response.Errors = new List<WorkerErrorSpan>(spans.Count);
                                foreach (var s in spans)
                                    response.Errors.Add(new WorkerErrorSpan
                                    {
                                        Start = s.StartOffset,
                                        Length = s.Length,
                                        Severity = s.Severity,
                                        Code = s.Code,
                                        Message = s.Message,
                                        Source = s.Source
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
                Write(stdout, response);
            }
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static string RequireKey(WorkerRequest request)
        => string.IsNullOrEmpty(request.Key) ? "untitled" : request.Key!;

    private static LoadedExtension? ResolveFence(List<LoadedExtension> registry, string fenceLanguage)
        => FenceLanguageResolver.Resolve(registry, fenceLanguage);

    private static void Write(StreamWriter stdout, WorkerResponse response)
        => stdout.WriteLine(JsonSerializer.Serialize(response, WorkerExtensionSnapshot.Options));
}
