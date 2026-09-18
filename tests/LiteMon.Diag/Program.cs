using LiteMon.Core.Collectors;

Console.WriteLine("[Full collector test]");
var cpu = new CpuCollector();
cpu.Initialize();
Console.WriteLine($"    init: {cpu.CpuName}, cores={cpu.LogicalCores}, base={cpu.BaseClockMHz}MHz");
Thread.Sleep(1100);
for (int i = 0; i < 3; i++)
{
    if (cpu.TrySample(out var total, out var perCore, out var freq))
    {
        Console.WriteLine($"    CPU: total={total:0.#}% perCore=[{string.Join(",", perCore.Select(v => v.ToString("0")))}] freq={freq:0}MHz");
        break;
    }
    Console.WriteLine($"    cpu retry {i}");
    Thread.Sleep(1100);
}

var net = new NetworkCollector();
net.Sample(); Thread.Sleep(1100);
var n2 = net.Sample();
Console.WriteLine($"    NET: ↓{n2.DownloadBytesPerSec:0.#}B/s ↑{n2.UploadBytesPerSec:0.#}B/s");

var gpu = new GpuCollector();
gpu.Initialize();
var g = gpu.Sample();
Console.WriteLine($"    GPU: {g.Name} usage={g.Usage:0}% vram={g.VramUsedBytes / 1024 / 1024}/{g.VramTotalBytes / 1024 / 1024}MB ({g.VramPercent}%) temp={g.TemperatureC}C power={g.PowerWatts}W coreMHz={g.CoreFrequencyMHz}");

var proc = new ProcessCollector();
proc.Sample(); Thread.Sleep(1100);
var list = proc.Sample();
Console.WriteLine($"    PROC: {list.Count} procs, top CPU: {string.Join(", ", list.OrderByDescending(p => p.CpuPercent).Take(3).Select(p => $"{p.Name}={p.CpuPercent}%"))}");
var gpuProcs = list.Where(p => p.GpuPercent > 0.05 || p.GpuDedicatedBytes > 1024*1024).OrderByDescending(p => p.GpuDedicatedBytes).Take(3);
foreach (var p in gpuProcs) Console.WriteLine($"       GPU proc: {p.Name} gpu={p.GpuPercent}% vram={p.GpuDedicatedBytes/1024/1024}MB");
Console.WriteLine("DONE");
