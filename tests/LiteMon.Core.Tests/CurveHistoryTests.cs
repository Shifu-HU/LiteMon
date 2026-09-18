using LiteMon.Core.Scheduling;
using Xunit;

namespace LiteMon.Core.Tests;

public class CurveHistoryTests
{
    private static double Ema(double prev, double raw, double a = 0.30) => prev + a * (raw - prev);

    [Fact]
    public void Push_then_Copy_returns_EMA_values_in_order()
    {
        var h = new CurveHistory();
        // 输入 1..5；EMA 后：1, 1.3, 1.79, 2.553, 3.4871 → Round(2)
        h.Push(1, 0, 0); h.Push(2, 0, 0); h.Push(3, 0, 0); h.Push(4, 0, 0); h.Push(5, 0, 0);

        var cpu = new double[CurveHistory.Capacity];
        int n = h.CopyCpu(cpu);
        Assert.Equal(5, n);

        double expected = 1;
        for (int i = 2; i <= 5; i++) expected = Ema(expected, i);
        Assert.Equal(expected, h.LatestCpu, 1);
        // 单调：EMA 跟随上升输入
        Assert.True(cpu[0] < cpu[1] && cpu[1] < cpu[2]);
    }

    [Fact]
    public void First_frame_primes_EMA_without_ramp()
    {
        var h = new CurveHistory();
        h.Push(42, 7, 99);

        var cpu = new double[16];
        var n = h.CopyCpu(cpu);
        Assert.Equal(1, n);
        Assert.Equal(42, cpu[0], 2);
        Assert.Equal(42, h.LatestCpu, 2);
        Assert.Equal(7, h.LatestGpu, 2);
        Assert.Equal(99, h.LatestRam, 2);
    }

    [Fact]
    public void EMA_smooths_toward_input()
    {
        var h = new CurveHistory();
        h.Push(0, 0, 0);          // 锚定 0
        h.Push(100, 100, 100);    // 0 + 0.3*100 = 30
        h.Push(100, 100, 100);    // 30 + 0.3*70 = 51

        Assert.Equal(51, h.LatestCpu, 1);
    }

    [Fact]
    public void Copy_clips_to_latest_tail_when_dest_smaller()
    {
        var h = new CurveHistory();
        // EMA 单调升序列；拷出尾部 4 点 = 第 7..10 帧的平滑值
        for (int i = 1; i <= 10; i++) h.Push(i, 0, 0);

        var dest = new double[4];
        int n = h.CopyCpu(dest);
        Assert.Equal(4, n);
        // 尾部应等于完整序列的后 4 个
        var full = new double[CurveHistory.Capacity];
        int fn = h.CopyCpu(full);
        Assert.Equal(full[(fn - 4)..fn], dest);
    }

    [Fact]
    public void Ring_wraps_after_capacity_keeps_last_600()
    {
        var h = new CurveHistory();
        // 锚定 1 后输入线性上升：EMA 平滑值也单调上升，最旧=第 51 帧附近、最新=第 650 帧
        for (int i = 1; i <= CurveHistory.Capacity + 50; i++) h.Push(i, 0, 0);

        Assert.Equal(CurveHistory.Capacity, h.Count);
        var dest = new double[CurveHistory.Capacity];
        int n = h.CopyCpu(dest);
        Assert.Equal(CurveHistory.Capacity, n);
        // 环绕后开头是最早保留帧（输入 51 的 EMA 值），结尾是最新帧（输入 650 的 EMA 值）
        Assert.True(dest[0] > 45 && dest[0] < 60, "oldest ≈ 51's EMA, got " + dest[0]);
        Assert.True(dest[^1] > 640, "newest ≈ 650's EMA, got " + dest[^1]);
        // 全程单调
        for (int i = 1; i < n; i++) Assert.True(dest[i] >= dest[i - 1] - 0.01, "monotonic at " + i);
    }

    [Fact]
    public void Reset_clears_all()
    {
        var h = new CurveHistory();
        h.Push(10, 10, 10);
        h.Reset();
        Assert.Equal(0, h.Count);
        Assert.Equal(0, h.LatestCpu, 0);
    }

    [Fact]
    public void Invalid_values_clamped_to_zero()
    {
        var h = new CurveHistory();
        h.Push(double.NaN, -5, double.PositiveInfinity);
        Assert.Equal(0, h.LatestCpu, 0);
        Assert.Equal(0, h.LatestGpu, 0);
        Assert.True(h.LatestRam >= 0);
    }
}
