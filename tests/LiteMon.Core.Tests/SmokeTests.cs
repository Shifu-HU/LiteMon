using System.Threading;
using System.Linq;
using LiteMon.Core.Collectors;
using LiteMon.Core.Models;
using LiteMon.Core.Scheduling;
using Xunit;

namespace LiteMon.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void DiskCollector_FindsFixedDisk()
    {
        using var disk = new DiskCollector();
        var d1 = disk.Sample();
        Thread.Sleep(1100);
        var d2 = disk.Sample();
        Assert.NotEmpty(d2);
        Assert.Contains(d2, x => x.Kind == LiteMon.Core.Models.DiskKind.Fixed);
        // C 盘必须有容量数据
        var c = d2.FirstOrDefault(x => x.Letter.TrimEnd(':').Equals("C", System.StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(c);
        Assert.True(c!.TotalBytes > 1024L * 1024 * 1024, $"C total={c.TotalBytes}");
        Assert.InRange(c.UsagePercent, 0, 100);
    }

    [Fact]
    public void MemoryCollector_ReturnsPlausibleData()
    {
        var m = new MemoryCollector().Sample();
        Assert.True(m.TotalBytes > 1024 * 1024 * 512, $"total={m.TotalBytes}");
        Assert.True(m.UsedBytes > 0);
        Assert.True(m.UsedBytes < m.TotalBytes);
        Assert.InRange(m.UsagePercent, 1, 100);
    }

    [Fact]
    public void CpuCollector_SamplesTotalUsage()
    {
        var cpu = new CpuCollector();
        cpu.Initialize();
        // 前两帧丢弃，第三帧应有有效值
        cpu.TrySample(out _, out _, out _);
        Thread.Sleep(1100);
        Assert.True(cpu.TrySample(out var total, out var perCore, out _));
        Assert.InRange(total, 0, 100);
        Assert.True(perCore.Length > 0, "no per-core data");
        foreach (var v in perCore)
            Assert.InRange(v, 0, 100);
        Assert.NotEmpty(cpu.CpuName);
        Assert.True(cpu.LogicalCores > 0);
    }

    [Fact]
    public void GpuCollector_NeverThrows_HasGracefulFallback()
    {
        var gpu = new GpuCollector();
        gpu.Initialize();
        var m = gpu.Sample();
        // 无 N 卡机器：可能全部不可用，但绝不抛异常；字段安全
        Assert.NotNull(m);
        Assert.True(m.VramPercent >= 0 && m.VramPercent <= 100);
    }

    [Fact]
    public void NetworkCollector_SumsInterfaces()
    {
        var n = new NetworkCollector();
        n.Sample();
        Thread.Sleep(1100);
        var m = n.Sample();
        Assert.True(m.DownloadBytesPerSec >= 0);
        Assert.True(m.UploadBytesPerSec >= 0);
    }

    [Fact]
    public void WifiCollector_ReturnsInterfaceInfo()
    {
        // NetworkCollector 现在带 Wi-Fi 接口明细（NativeWifi）
        var n = new NetworkCollector();
        n.Sample();
        Thread.Sleep(1100);
        var m = n.Sample();
        Assert.NotNull(m);
        Assert.True(m.DownloadBytesPerSec >= 0);
        Assert.True(m.UploadBytesPerSec >= 0);
        foreach (var i in m.Interfaces)
        {
            Assert.False(string.IsNullOrEmpty(i.Name));
            if (i.Kind == InterfaceKind.Wifi && i.IsConnected)
            {
                Assert.False(string.IsNullOrEmpty(i.Ssid));
                Assert.InRange((double)i.SignalPercent, 0, 100);
            }
        }
    }

    [Fact]
    public void DiskProcessCollector_ReturnsIoRates()
    {
        using var dp = new DiskProcessCollector();
        dp.Sample();                 // 首帧预热
        Thread.Sleep(1100);
        var list = dp.Sample();
        Assert.NotNull(list);
        foreach (var p in list)
        {
            Assert.False(string.IsNullOrEmpty(p.Name));
            Assert.True(p.ReadBytesPerSec >= 0);
            Assert.True(p.WriteBytesPerSec >= 0);
        }
    }

    [Fact]
    public void ProcessCollector_CollectsAndNormalizes()
    {
        var p = new ProcessCollector();
        var first = p.Sample();
        Thread.Sleep(1100);
        var second = p.Sample();
        Assert.True(second.Count > 10, $"process count = {second.Count}");
        foreach (var pm in second)
        {
            Assert.InRange(pm.CpuPercent, 0, 100);
            Assert.True(pm.WorkingSetBytes >= 0);
        }
        // 自身进程应在列表里
        Assert.Contains(second, x => x.Pid == Environment.ProcessId);
    }

    [Fact]
    public void MonitorService_ProducesSnapshots_EveryInterval()
    {
        var svc = new MonitorService();
        int frames = 0;
        svc.SnapshotProduced += _ => Interlocked.Increment(ref frames);
        svc.Start();
        Thread.Sleep(3500);
        Assert.True(frames >= 2, $"frames={frames}");
        var snap = svc.Latest;
        Assert.NotNull(snap);
        Assert.InRange(snap.Cpu.TotalUsage, 0, 100);
        Assert.True(snap.Memory.TotalBytes > 0);
        Assert.NotNull(snap.Processes);
        svc.Dispose();
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "litemon-test-settings.json");
        try
        {
            var s = new LiteMon.Core.Settings.AppSettings
            {
                RefreshSeconds = 2.0,
                FloatingOpacity = 0.5,
                Theme = LiteMon.Core.Settings.ThemeMode.Dark,
                Scheme = LiteMon.Core.Settings.ColorSchemes.All[2].Id,
            };
            s.Save(path);
            var loaded = LiteMon.Core.Settings.AppSettings.Load(path);
            Assert.Equal(2.0, loaded.RefreshSeconds);
            Assert.Equal(0.5, loaded.FloatingOpacity);
            Assert.Equal(LiteMon.Core.Settings.ThemeMode.Dark, loaded.Theme);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    // ---- 内置盘：不得误判为可移动 ----
    [InlineData(@"SCSI\DISK&VEN_NVME&PROD_YMTC_YMSS2ED08D2\5&FF809F8&0&000000", "SCSI", "Fixed hard disk media", false)]
    [InlineData(@"IDE\DISK&VEN_SAMSUNG&PROD_SSD_850_EVO\5&1A2B3C4D&0&0.0.0", "IDE", "Fixed hard disk media", false)]
    [InlineData(@"SCSI\DISK&VEN_VBOX&PROD_HARDDISK\4&2B3C4D5E&0&000000", "SCSI", "Fixed hard disk media", false)]
    // ---- 普通优盘 / 移动硬盘：PNPDeviceID 以 USBSTOR 开头 ----
    [InlineData(@"USBSTOR\DISK&VEN_AIGO&PROD_U330&REV_PMAP\44E3E1B00308FB8D&0", "USB", "Removable Media", true)]
    [InlineData(@"USBSTOR\DISK&VEN_SANDISK&PROD_CRUZER_BLADE\000000000000&0", "USB", "Removable Media", true)]
    // ---- USB 桥接硬盘盒：走 SCSI 通道，InterfaceType=SCSI，唯一线索是 MediaType=External ----
    // 真实案例：各类 USB 硬盘盒（RTL9210 / JMicron / ASMedia 等桥接芯片）都属于这一类，
    // 它们会被 Windows 报成 DriveType.Fixed，只看 PNPDeviceID 会漏判。
    [InlineData(@"SCSI\DISK&VEN_VLOGGER&PROD_VLOGGER_LUNCHBOX\6&16437366&0&000000", "SCSI", "External hard disk media", true)]
    [InlineData(@"SCSI\DISK&VEN_&PROD_RTL9210C\6&275CD811&0&000000", "SCSI", "External hard disk media", true)]
    [InlineData(@"SCSI\DISK&VEN_JMICRON&PROD_TECHNOLOGY\6&1F2E3D4C&0&000000", "SCSI", "External hard disk media", true)]
    // ---- 读卡器槽位：Removable Media ----
    [InlineData(@"USBSTOR\DISK&VEN_SANDISK&PROD_SDDR-A631&REV_0021\000000000021&1", "USB", "Removable Media", true)]
    // ---- 字段缺失/异常：不应崩溃，按固定盘处理 ----
    [InlineData("", "", "", false)]
    [InlineData("UNKNOWN", "SCSI", "Fixed hard disk media", false)]
    public void IsExternalBus_ClassifiesUsbBridgedEnclosures(string pnp, string iface, string media, bool expected)
    {
        Assert.Equal(expected, LiteMon.Core.Collectors.DiskCollector.IsExternalBus(pnp, iface, media));
    }

    [Fact]
    public void FormattingHelpers_FormatBytes()
    {
        Assert.Equal("1.00 GB", LiteMon.Core.Formatting.FormatBytes(1024L * 1024 * 1024));
        Assert.Equal("1.50 MB", LiteMon.Core.Formatting.FormatBytes(1024L * 1024 * 1500 / 1000));
        Assert.Equal("512 B", LiteMon.Core.Formatting.FormatBytes(512));
    }

    [Fact]
    public void ColorScheme_IdIsStable_AndLegacyChineseNamesStillResolve()
    {
        // Id 必须稳定且唯一（它是持久化键，改名/翻译不影响已存设置）
        var ids = LiteMon.Core.Settings.ColorSchemes.All.Select(s => s.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));

        // 按 Id 精确命中
        Assert.Equal("matcha", LiteMon.Core.Settings.ColorSchemes.ByName("matcha").Id);

        // 旧版本存的是中文显示名 → 必须仍能解析到对应方案（否则用户升级后配色被重置）
        var legacy = LiteMon.Core.Settings.ColorSchemes.ByName("抹茶绿·米白");
        Assert.Equal("matcha", legacy.Id);
        Assert.Equal(LiteMon.Core.Settings.ColorSchemes.All[4].Id, legacy.Id);

        // 完全无法识别的值回退到默认方案
        Assert.Equal("navy", LiteMon.Core.Settings.ColorSchemes.ByName("不存在").Id);
        Assert.Equal("navy", LiteMon.Core.Settings.ColorSchemes.ByName("").Id);
    }

    /// <summary>
    /// 外接盘识别的纯函数测试。真实环境里常见三类：
    /// USB 桥接硬盘盒（走 SCSI，只能靠 MediaType=External 识别）、优盘、读卡器。
    /// </summary>
}
