// Licensed under GPL-v3.0
using Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kodo;

class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);

    [STAThread]
    public static void Main(string[] args)
    {
        AttachConsole(0xFFFFFFFF);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        if (!SingleInstance.TryAcquire())
        {
            var handoffPath = args.Length > 0 ? args[0] : null;
            SingleInstance.SendActivationRequest(handoffPath);
            return;
        }

        AptabaseClient.Initialize();

        var app = BuildAvaloniaApp();

        try
        {
            app.StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Task.Run(async () => await AptabaseClient.FlushAsync()).Wait(TimeSpan.FromSeconds(2));
            SingleInstance.Release();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

internal static class SingleInstance
{
    private static readonly string MutexName = $@"Local\Kodo_SingleInstance_Mutex_9F3E2C1A_{VersionSuffix()}";
    private static readonly string PipeName = $"Kodo_SingleInstance_Pipe_9F3E2C1A_{VersionSuffix()}";

    private static Mutex? _mutex;

    private static string VersionSuffix()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var version = string.IsNullOrEmpty(informationalVersion)
            ? "0.0.0"
            : informationalVersion.Split('+', 2)[0];

        return version.Replace('.', '_');
    }

    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew) return true;

        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
    }

    public static void StartListening(Action<string?> onActivationRequested)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    var payload = await reader.ReadToEndAsync();

                    onActivationRequested(string.IsNullOrWhiteSpace(payload) ? null : payload);
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    public static void SendActivationRequest(string? filePath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);

            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(filePath ?? string.Empty);
            writer.Flush();
        }
        catch
        {

        }
    }
}
