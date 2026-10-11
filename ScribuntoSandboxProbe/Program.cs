using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

var probe = args.FirstOrDefault() ?? throw new ArgumentException("A probe name is required.");
var result = probe switch
{
    "identity" => Probe("identity", () => new { uid = GetUid(), gid = GetGid(), root = GetUid() == 0 }),
    "filesystem-read" => Probe("filesystem-read", () => File.ReadAllText("/etc/passwd")),
    "filesystem-write" => Probe("filesystem-write", () => { File.WriteAllText("/worker/sandbox-write-probe", "blocked"); return "write-succeeded"; }),
    "network" => Probe("network", () =>
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SendTo([1], new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
        return "send-succeeded";
    }),
    "environment" => Probe("environment", () => Environment.GetEnvironmentVariable("CW_SECRET_PROBE") ?? string.Empty),
    "process" => Probe("process", () =>
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c true") { UseShellExecute = false });
        process?.WaitForExit();
        return "process-succeeded";
    }),
    "library" => Probe("library", () => NativeLibrary.Load("/tmp/host-only-probe.so").ToString()),
    "memory" => Probe("memory", () =>
    {
        var blocks = new List<byte[]>();
        while (true)
        {
            var block = GC.AllocateUninitializedArray<byte>(16 * 1024 * 1024);
            for (var index = 0; index < block.Length; index += 4096) block[index] = 1;
            blocks.Add(block);
        }
    }),
    "tasks" => Probe("tasks", () =>
    {
        var threads = new List<Thread>();
        for (var index = 0; index < 128; index++)
        {
            var thread = new Thread(() => Thread.Sleep(10_000));
            thread.Start();
            threads.Add(thread);
        }
        return "task-creation-succeeded";
    }),
    "cpu" => Probe("cpu", () =>
    {
        while (true) _ = Math.Sqrt(Random.Shared.NextDouble());
    }),
    _ => throw new ArgumentException($"Unknown probe '{probe}'.")
};
Console.WriteLine(JsonSerializer.Serialize(result));
return result.Blocked ? 0 : 1;

static ProbeResult Probe(string name, Func<object> operation)
{
    try
    {
        var value = operation();
        return new(name, false, value?.ToString(), null);
    }
    catch (Exception exception)
    {
        return new(name, true, null, exception.GetType().Name);
    }
}

static int GetUid()
{
    if (!OperatingSystem.IsLinux()) return -1;
    return int.TryParse(File.ReadLines("/proc/self/status").First(line => line.StartsWith("Uid:", StringComparison.Ordinal)).Split('\t', StringSplitOptions.RemoveEmptyEntries)[1], out var value) ? value : -1;
}

static int GetGid()
{
    if (!OperatingSystem.IsLinux()) return -1;
    return int.TryParse(File.ReadLines("/proc/self/status").First(line => line.StartsWith("Gid:", StringComparison.Ordinal)).Split('\t', StringSplitOptions.RemoveEmptyEntries)[1], out var value) ? value : -1;
}

record ProbeResult(string Name, bool Blocked, string? Value, string? Error);
