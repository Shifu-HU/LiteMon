using System.Runtime.InteropServices;

var statuses = new[]
{
    "\\Processor Information(*)\\% Processor Time",
    "\\Processor(*)\\% Processor Time",
    "\\Processor Information(_Total)\\% Processor Time",
    "\\Processor(_Total)\\% Processor Time",
    "\\Processor Information(_Total)\\% Processor Performance",
};

foreach (var p in statuses)
{
    var st = Pdh.PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out var q);
    var add = Pdh.PdhAddEnglishCounterW(q, p, IntPtr.Zero, out var h);
    Console.WriteLine($"{p,-65} open={st:X} add={add:X}");
    if (add == 0)
    {
        var c1 = Pdh.PdhCollectQueryData(q);
        Thread.Sleep(700);
        var c2 = Pdh.PdhCollectQueryData(q);
        long v = 0;
        var fmt = Pdh.PdhGetFormattedCounterValue(h, 0x400, out _, out v);
        Console.WriteLine($"    collect1={c1:X} collect2={c2:X} fmt={fmt:X} value={v}");
    }
}

uint need = 0;
Pdh.PdhExpandWildCardPathW(null, "\\Processor Information(*)\\% Processor Time", null, ref need, 0);
Console.WriteLine("expand need = " + need);
if (need > 0)
{
    var buf = new char[need];
    var ex = Pdh.PdhExpandWildCardPathW(null, "\\Processor Information(*)\\% Processor Time", buf, ref need, 0);
    Console.WriteLine("expand ret = " + ex.ToString("X"));
    int i = 0;
    while (i < buf.Length)
    {
        int s2 = i;
        while (i < buf.Length && buf[i] != '\0') i++;
        if (i > s2) Console.WriteLine("    " + new string(buf, s2, i - s2));
        i++;
    }
}

static class Pdh
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int PdhOpenQueryW(IntPtr s, IntPtr f, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int PdhAddEnglishCounterW(IntPtr q, string p, IntPtr f, out IntPtr h);
    [DllImport("pdh.dll", ExactSpelling = true)] public static extern int PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int PdhExpandWildCardPathW(string? s, string? w, char[]? b, ref uint n, uint f);

    [DllImport("pdh.dll", ExactSpelling = true)] public static extern int PdhGetFormattedCounterValue(IntPtr h, double fmt, out uint t, out long v);
}
