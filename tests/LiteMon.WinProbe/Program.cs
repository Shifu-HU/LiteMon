using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

[DllImport("user32.dll")] static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
[DllImport("user32.dll")] static extern bool SetForegroundWindow(int h);
[DllImport("user32.dll")] static extern bool ShowWindow(int h, int cmd);
[DllImport("user32.dll")] static extern bool IsIconic(int h);

SetProcessDPIAware();   // 物理坐标

var mode = args[0];
var pid = int.Parse(args[1]);

if (mode == "--shot")
{
    // 先经 UIA 恢复主窗口（Normal + 前台），再 PrintWindow 截真实内容（防遮挡/防隐藏）
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        try
        {
            var wp0 = w.GetCurrentPattern(WindowPattern.Pattern) as WindowPattern;
            wp0?.SetWindowVisualState(WindowVisualState.Normal);
        }
        catch { }
        Thread.Sleep(400);
        break;
    }
    int hwnd = 0; int bestArea = 0;
    W.EnumWindows((h, l) =>
    {
        uint wpid2; W.GetWindowThreadProcessId(h, out wpid2);
        if (wpid2 == (uint)pid)
        {
            var t = new StringBuilder(256); W.GetWindowText(h, t, 256);
            var rr = new RECT(); W.GetWindowRect(h, ref rr);
            int area = (rr.Right - rr.Left) * (rr.Bottom - rr.Top);
            if (t.ToString().StartsWith("LiteMon") && area > bestArea && area > 400 * 300)
            {
                bestArea = area; hwnd = (int)h;
            }
        }
        return true;
    }, IntPtr.Zero);
    if (hwnd == 0) { Console.WriteLine("hwnd not found"); return; }
    SetForegroundWindow(hwnd);
    Thread.Sleep(300);
    var r2 = new RECT(); W.GetWindowRect((IntPtr)hwnd, ref r2);
    int imgW = r2.Right - r2.Left, imgH = r2.Bottom - r2.Top;
    if (imgW <= 0 || imgH <= 0) { Console.WriteLine("bad rect"); return; }
    // 窗口已前台：直接屏幕拷贝（PrintWindow 对该 WPF 窗拿缓存旧帧，不可用）
    using var bmp = new System.Drawing.Bitmap(imgW, imgH);
    using var g2 = System.Drawing.Graphics.FromImage(bmp);
    g2.CopyFromScreen(r2.Left, r2.Top, 0, 0, new System.Drawing.Size(imgW, imgH));
    bmp.Save(args[2], System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine("shot " + imgW + "x" + imgH + " -> " + args[2]);
    return;
}

if (mode == "--combo")
{
    // --combo <pid> <automationId> expand|collapse|select [index]
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        var cb = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, args[2]));
        if (cb == null) { Console.WriteLine("combo not found: " + args[2]); return; }
        if (args[3] == "select" && args.Length > 4)
        {
            int idx = int.Parse(args[4]);
            var ec0 = (ExpandCollapsePattern)cb.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            // 先收起再展开，确保拿到的是全新的展开，然后轮询等 popup 完成渲染
            if (ec0.Current.ExpandCollapseState == ExpandCollapseState.Expanded) { ec0.Collapse(); Thread.Sleep(400); }
            ec0.Expand();
            AutomationElementCollection items = null;
            for (int t = 0; t < 25; t++)
            {
                Thread.Sleep(200);
                items = cb.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                if (items.Count > 0) break;
            }
            Console.WriteLine("state=" + ec0.Current.ExpandCollapseState);
            Console.WriteLine("items=" + (items?.Count ?? 0));
            if (idx < items.Count)
            {
                var it = items[idx];
                Console.WriteLine("target=" + it.Current.Name);
                if (it.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp2))
                {
                    ((SelectionItemPattern)sp2).Select();
                    Console.WriteLine("selected: " + it.Current.Name);
                }
            }
            Thread.Sleep(800);
            return;
        }
        if (cb.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep))
        {
            var ec = (ExpandCollapsePattern)ep;
            if (args[3] == "expand") ec.Expand();
            else if (args[3] == "collapse") ec.Collapse();
            Console.WriteLine("combo " + args[3] + " -> " + ec.Current.ExpandCollapseState);
            Thread.Sleep(900);
            var items2 = cb.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            Console.WriteLine("items=" + items2.Count);
            foreach (AutomationElement x in items2) Console.WriteLine("  [" + x.Current.Name + "]");
        }
        else Console.WriteLine("no expand pattern");
        return;
    }
    Console.WriteLine("main not found");
    return;
}

if (mode == "--rects")
{
    // 列出所有可交互元素的 名称 + 矩形（找设置齿轮/下拉框的真实坐标）
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        foreach (var ct in new[] { ControlType.RadioButton, ControlType.Button, ControlType.ComboBox, ControlType.ListItem })
        {
            foreach (AutomationElement el in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ct)))
            {
                var r = el.Current.BoundingRectangle;
                Console.WriteLine(ct.ProgrammaticName.Replace("ControlType.", "") + " | " + el.Current.Name + " | " +
                    (int)r.X + "," + (int)r.Y + " " + (int)r.Width + "x" + (int)r.Height);
            }
        }
        return;
    }
    return;
}

if (mode == "--navfirst")
{
    // 选中第一个 RadioButton（设置齿轮在导航栏最前，UIA 树序第一个）
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        var rbs = w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
        if (rbs.Count > 0)
        {
            var t = rbs[0];
            if (t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p0))
                ((SelectionItemPattern)p0).Select();
            Console.WriteLine("navfirst -> " + t.Current.Name);
        }
        else Console.WriteLine("no radio");
        return;
    }
    return;
}

if (mode == "--navlast")
{
    // 选中最后一个 RadioButton（设置齿轮，DockPanel.Dock=Right 的最后一项）
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        var rbs = w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
        AutomationElement target = null;
        foreach (AutomationElement rb in rbs) target = rb;
        if (target != null)
        {
            var sel = target.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
            sel?.Select();
            Console.WriteLine("navlast -> " + target.Current.Name);
        }
        else Console.WriteLine("no radio");
        return;
    }
    Console.WriteLine("main not found");
    return;
}

if (mode == "--radio")
{
    // 列出主窗口所有 RadioButton 的名称与选中状态（验证导航互斥）
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        var rbs = w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
        foreach (AutomationElement rb in rbs)
        {
            var sel = rb.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
            Console.WriteLine((sel?.Current.IsSelected == true ? "[*] " : "[ ] ") + rb.Current.Name);
        }
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--find")
{
    // 按名称子串查找元素并打印矩形（验证元素是否渲染/位置）
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        var name = args[2];
        foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)))
        {
            var r = e.Current.BoundingRectangle;
            Console.WriteLine($"'{e.Current.Name}' ({(int)r.X},{(int)r.Y}) {(int)r.Width}x{(int)r.Height} offscreen={e.Current.IsOffscreen}");
        }
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--tree")
{
    // 从指定名称元素向上打印父链（类型 + rect），诊断可视化树挂载
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        var target = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, args[2]));
        if (target == null) { Console.WriteLine("target not found"); return; }
        var e = target;
        for (int i = 0; i < 10 && e != null; i++)
        {
            var r = e.Current.BoundingRectangle;
            Console.WriteLine($"{i} {e.Current.ControlType.ProgrammaticName} '{e.Current.Name}' ({(int)r.X},{(int)r.Y}) {(int)r.Width}x{(int)r.Height} offscreen={e.Current.IsOffscreen}");
            e = System.Windows.Automation.TreeWalker.RawViewWalker.GetParent(e);
        }
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--pick")
{
    // 按名称选中一个 RadioButton（验证设置交互）
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        var rb = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, args[2]));
        if (rb == null) { Console.WriteLine("radio not found: " + args[2]); return; }
        var sel = rb.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
        sel!.Select();
        Thread.Sleep(700);
        Console.WriteLine("picked: " + args[2]);
        var sub = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "硬件监视 · 0.1 秒刷新"));
        Console.WriteLine("subtitle updated: " + (sub != null));
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--scroll")
{
    // 找页面级 ScrollViewer，滚动到底部，验证目标元素（args[2]=名称）随后可见
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        var sv = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane));
        // 直接找 ScrollPattern 支持的子孙
        AutomationElement? scrollable = null;
        foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            if (e.TryGetCurrentPattern(ScrollPattern.Pattern, out var _))
            {
                scrollable = e;
                break;
            }
        }
        if (scrollable == null) { Console.WriteLine("no scrollable element"); return; }
        var sp = scrollable.GetCurrentPattern(ScrollPattern.Pattern) as ScrollPattern;
        Console.WriteLine("scrollable found, v-scroll range: " + (sp!.Current.VerticalViewSize) + "% view");
        var before = scrollable.Current.BoundingRectangle;
        // 滚到底
        sp.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
        Thread.Sleep(700);
        var after = scrollable.Current.BoundingRectangle;
        // 目标元素可见性
        var target = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, args[2]));
        Console.WriteLine(args[2] + " offscreen=" + (target?.Current.IsOffscreen.ToString() ?? "not-found"));
        sp.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement);
        Thread.Sleep(500);
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--killresult")
{
    // 前置：由调用方启动一个记事本靶标并等其出现在列表里（此处只做点选/武装/执行/采样）
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        if (IsIconic(w.Current.NativeWindowHandle)) ShowWindow(w.Current.NativeWindowHandle, 9);
        SetForegroundWindow(w.Current.NativeWindowHandle);
        Thread.Sleep(600);
        var winRect = w.Current.BoundingRectangle;
        // 先导航到目标页（args[3] = 'GPU'），保证靶标行在该页列表里
        if (args.Length > 3 && args[3].Length > 0)
        {
            foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)))
            {
                if (e.Current.Name == args[3])
                {
                    var si = e.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
                    si?.Select();
                    Thread.Sleep(1200);
                    break;
                }
            }
        }
        string FabName()
        {
            foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                var n = e.Current.Name;
                if (n.StartsWith("结束进程") || n.StartsWith("再次点击确认结束") || n.StartsWith("已结束") || n.StartsWith("先在列表中点选一行"))
                    return n;
            }
            return "(无)";
        }
        // 自校准找目标行（args[2] = '名称  (pid)'；用 Contains 匹配并打印所有行辅助诊断）
        var want = args[2];
        AutomationElement? rowEl = null;
        int listed = 0;
        foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            var nn = e.Current.Name ?? "";
            if (nn.Length > 5 && nn.EndsWith(")") && nn.Contains("  ("))
            {
                listed++;
                if (listed <= 8) Console.WriteLine("  列表行: '" + nn + "'");
                // 按传入 pid 后缀匹配（名称空格差异不可靠）
                if (want.Contains("(") && nn.EndsWith(want.Substring(want.IndexOf('('))) && rowEl == null)
                { rowEl = e; Console.WriteLine("  → 选中目标行: '" + nn + "'"); }
            }
        }
        if (rowEl == null) { Console.WriteLine("target row not found: " + want + " (共 " + listed + " 行)"); return; }
        // 行可能远离视口：逐步滚动（0,10,…100%）直到 FromPoint 校准命中
        ScrollPattern? pageScroll = null;
        foreach (AutomationElement sv2 in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            if (sv2.TryGetCurrentPattern(ScrollPattern.Pattern, out var _))
            { pageScroll = sv2.GetCurrentPattern(ScrollPattern.Pattern) as ScrollPattern; break; }
        }
        double cx = 0, hitY = -1;
        for (int pct = 0; pct <= 100 && hitY < 0; pct += 12)
        {
            if (pageScroll != null) { try { pageScroll.SetScrollPercent(ScrollPattern.NoScroll, pct); } catch { } Thread.Sleep(350); }
            var rr = rowEl.Current.BoundingRectangle;
            cx = rr.X + rr.Width / 2;
            int cy = (int)(rr.Y + rr.Height / 2);
            for (int dy = 0; dy <= 600; dy += 6)
            {
                foreach (int py in new[] { cy + dy, cy - dy }.Where(v => v > winRect.Y + 80 && v < winRect.Bottom - 8))
                {
                    try
                    {
                        var at = AutomationElement.FromPoint(new System.Windows.Point(cx, py));
                        if (at.Current.ProcessId != pid) continue;
                        var an = at.Current.Name ?? "";
                        if (an.EndsWith(want.Substring(want.IndexOf('('))))
                        { hitY = py; break; }
                    }
                    catch { }
                }
                if (hitY >= 0) break;
            }
        }
        if (hitY < 0) { Console.WriteLine("calibration failed for row"); return; }
        Console.WriteLine("行校准命中 y=" + (int)hitY);
        void ClickAt(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(180);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        }
        ClickAt((int)cx, (int)hitY);
        Thread.Sleep(700);
        Console.WriteLine("选中: " + FabName());
        // 点浮岛：武装
        AutomationElement? fab = null;
        foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            var n = e.Current.Name;
            if (n.StartsWith("结束进程") || n.StartsWith("再次点击确认结束")) { fab = e; break; }
        }
        if (fab == null) { Console.WriteLine("fab not found"); return; }
        var fr = fab.Current.BoundingRectangle;
        double fx = fr.X + fr.Width / 2, fyy = fr.Y + fr.Height / 2;
        // 浮岛也需自校准
        double fHit = -1;
        for (int dy = 0; dy <= 420; dy += 6)
        {
            try
            {
                var at = AutomationElement.FromPoint(new System.Windows.Point(fx, fyy + dy));
                if (at.Current.ProcessId != pid) continue;
                var an = at.Current.Name ?? "";
                if (an.StartsWith("结束进程") || an.StartsWith("再次点击确认结束") || an.StartsWith("已结束")) { fHit = fyy + dy; break; }
            }
            catch { }
        }
        if (fHit < 0) { Console.WriteLine("fab calibration failed"); return; }
        Console.WriteLine("浮岛校准 y=" + (int)fHit);
        ClickAt((int)fx, (int)fHit);
        Thread.Sleep(700);
        Console.WriteLine("武装: " + FabName());
        ClickAt((int)fx, (int)fHit);
        Thread.Sleep(500);
        Console.WriteLine("执行后 0.5s: " + FabName());
        Thread.Sleep(1200);
        Console.WriteLine("执行后 1.7s: " + FabName());
        Thread.Sleep(1400);
        Console.WriteLine("执行后 3.1s: " + FabName());
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--deselect")
{
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        void Click(AutomationElement e)
        {
            var r = e.Current.BoundingRectangle;
            SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
            Thread.Sleep(180);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        }
        string FabName()
        {
            foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                var n = e.Current.Name;
                if (n.StartsWith("结束进程") || n.StartsWith("再次点击确认结束") || n.StartsWith("先在列表中点选一行"))
                    return n;
            }
            return "(无)";
        }

        // 1) 点选首行（最多 3 次重试：列表 2s 重排，矩形可能陈旧）
        // 最小化则先恢复（-32000 是最小化伪坐标；必须在一切矩形读取前执行）
        if (IsIconic(w.Current.NativeWindowHandle)) ShowWindow(w.Current.NativeWindowHandle, 9);
        SetForegroundWindow(w.Current.NativeWindowHandle);
        Thread.Sleep(600);
        var winRect = w.Current.BoundingRectangle;
        Console.WriteLine("winRect: " + (int)winRect.X + "," + (int)winRect.Y + " " + (int)winRect.Width + "x" + (int)winRect.Height + " iconic=" + IsIconic(w.Current.NativeWindowHandle));
        // 先滚回顶部，让首行进入视口（列表无虚拟化，长列表把窗口撑高）
        foreach (AutomationElement sv in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane)))
        {
            try
            {
                var sp = sv.GetCurrentPattern(ScrollPattern.Pattern) as ScrollPattern;
                if (sp != null && sp.Current.VerticalViewSize < 100)
                {
                    Console.WriteLine("scrollpane found, viewsz=" + sp.Current.VerticalViewSize);
                    sp.SetScrollPercent(ScrollPattern.NoScroll, 100);   // 滚到底：列表首屏在折叠线下
                    Thread.Sleep(400);
                    break;
                }
            }
            catch (Exception ex) { Console.WriteLine("scroll ex: " + ex.Message); }
        }
        AutomationElement? FreshRow()
        {
            AutomationElement? best = null; double bestY = double.MaxValue;
            foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                var n = e.Current.Name;
                if (n.Length > 4 && n.EndsWith(")") && n.Contains("(") && n.Contains("  ("))
                {
                    var r2 = e.Current.BoundingRectangle;
                    if (r2.Width > 20 && r2.Height > 5
                        && r2.Y > winRect.Y + 150 && r2.Bottom < winRect.Bottom - 10
                        && r2.Y < bestY)
                    { best = e; bestY = r2.Y; }
                }
            }
            return best;
        }
        bool selected = false;
        AutomationElement? clicked = null;
        for (int attempt = 1; attempt <= 3 && !selected; attempt++)
        {
            var rc = FreshRow();
            if (rc == null) { Console.WriteLine("attempt " + attempt + ": 视口内无行"); continue; }
            Console.WriteLine("attempt " + attempt + " 点行: '" + rc.Current.Name + "'");
            var rrr = rc.Current.BoundingRectangle;
            // UIA 布局坐标与真实渲染差 ~200px（探针 DPI 虚拟化）：以 FromPoint 为真值，
            // 在该 x 上向下扫描找名字以 "(pid)" 结尾的行文本，点那个点（自校准）
            double cx = rrr.X + rrr.Width / 2;
            double baseY = rrr.Y + rrr.Height / 2;
            double hitY = -1; string hitName = "";
            for (int dy = 0; dy <= 420; dy += 6)
            {
                try
                {
                    var at = AutomationElement.FromPoint(new System.Windows.Point(cx, baseY + dy));
                    if (at.Current.ProcessId != pid) continue;
                    var an = at.Current.Name ?? "";
                    if (an.Length > 5 && an.EndsWith(")") && an.Contains("  (") && an.IndexOf("  (") > 0)
                    { hitY = baseY + dy; hitName = an; break; }
                }
                catch { }
            }
            if (hitY < 0) { Console.WriteLine("  自校准扫描未命中行"); continue; }
            Console.WriteLine("  自校准命中 '" + hitName + "' y=" + (int)hitY);
            SetCursorPos((int)cx, (int)hitY);
            Thread.Sleep(180);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(700);
            selected = FabName().Contains("PID");
        }
        Console.WriteLine("选中后: " + FabName());
        if (!selected) return;

        // 2) 点列表末行下方的卡片底 padding（属于卡片 host、不属于任何行）→ 应放弃选中
        AutomationElement? last = null; double lastY = double.MinValue;
        foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            var n = e.Current.Name;
            if (n.Length > 4 && n.EndsWith(")") && n.Contains("  ("))
            {
                var rr = e.Current.BoundingRectangle;
                if (rr.Width > 20 && rr.Height > 5 && rr.Bottom < winRect.Bottom - 4 && rr.Y > lastY)
                { last = e; lastY = rr.Y; }
            }
        }
        if (last == null) { Console.WriteLine("末行未找到"); return; }
        var lr = last.Current.BoundingRectangle;
        int bx = (int)(lr.X + 60), by = (int)(lr.Bottom + 9);
        Console.WriteLine("末行: '" + last.Current.Name + "' 底=" + (int)lr.Bottom + " → 点空白 " + bx + "," + by);
        SetCursorPos(bx, by);
        Thread.Sleep(180);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(700);
        Console.WriteLine("点卡片空白后: " + FabName());
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--persist")
{
    // 安全验证：选中行 → 等刷新 → 武装 → 等刷新 → 回读（全程不执行结束）
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        void Click(AutomationElement e)
        {
            var r = e.Current.BoundingRectangle;
            SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
            Thread.Sleep(180);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        }
        string FabName()
        {
            foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                var n = e.Current.Name;
                if (n.StartsWith("结束进程") || n.StartsWith("再次点击确认结束") || n.StartsWith("先在列表中点选一行"))
                    return n;
            }
            return "(无)";
        }

        // 置前窗口（先恢复最小化；探针已提权，可对提权窗口操作）
        if (IsIconic(w.Current.NativeWindowHandle)) ShowWindow(w.Current.NativeWindowHandle, 9);
        SetForegroundWindow(w.Current.NativeWindowHandle);
        Thread.Sleep(600);

        // 精确找行文本 "名称  (PID)"，点其中心
        AutomationElement? rowCell = null;
        foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            var n = e.Current.Name;
            if (n.Length > 4 && n.EndsWith(")") && n.Contains("(") && n.Contains("  ("))
            {
                var r2 = e.Current.BoundingRectangle;
                if (r2.Width > 20 && r2.Height > 5) { rowCell = e; break; }
            }
        }
        if (rowCell == null) { Console.WriteLine("row cell not found"); return; }
        var hr = rowCell.Current.BoundingRectangle;
        Console.WriteLine("行文本: '" + rowCell.Current.Name + "' @ " + (int)hr.X + "," + (int)hr.Y + " " + (int)hr.Width + "x" + (int)hr.Height);
        SetCursorPos((int)(hr.X + hr.Width / 2), (int)(hr.Y + hr.Height / 2));
        Thread.Sleep(150);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(700);
        Console.WriteLine("选中后: " + FabName());

        Thread.Sleep(3500);   // 跨过至少一次列表刷新（2s 节流）
        Console.WriteLine("刷新后: " + FabName());

        // 武装，再跨刷新
        foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            if (e.Current.Name.StartsWith("结束进程"))
            {
                var fr = e.Current.BoundingRectangle;
                int fcx = (int)(fr.X + fr.Width / 2), fcy = (int)(fr.Y + fr.Height / 2);
                // 该屏幕点上的元素是谁？（判断是否被其它窗口遮挡）
                try
                {
                    var at = AutomationElement.FromPoint(new System.Windows.Point(fcx, fcy));
                    Console.WriteLine("浮岛中心点上的元素: '" + at.Current.Name + "' pid=" + at.Current.ProcessId);
                }
                catch (Exception ex) { Console.WriteLine("FromPoint 失败: " + ex.Message); }
                Click(e);
                break;
            }
        }
        Thread.Sleep(600);
        Console.WriteLine("武装后: " + FabName());
        Thread.Sleep(3500);
        Console.WriteLine("武装+刷新后: " + FabName());

        // 再次点列表行取消选中 → 浮岛复位（不执行结束）
        SetCursorPos((int)(hr.X + hr.Width / 2), (int)(hr.Y + hr.Height / 2));
        Thread.Sleep(150);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(700);
        Console.WriteLine("取消选中后: " + FabName());
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--killflow")
{
    // 完整链路：点击列表第一行（选中）→ 点浮岛（武装）→ 再点（执行）→ 回读浮岛文本
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        void Click(AutomationElement e)
        {
            var r = e.Current.BoundingRectangle;
            int cx = (int)(r.X + r.Width / 2), cy = (int)(r.Y + r.Height / 2);
            SetCursorPos(cx, cy);
            Thread.Sleep(200);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        }
        // 列表第一行 = "运行中的应用" 表体第一行的应用名（name 形如 "xxx (pid)"）——用 Content 表找
        var nameCol = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "应用"));
        if (nameCol == null) { Console.WriteLine("header not found"); return; }
        var hr = nameCol.Current.BoundingRectangle;
        int rowY = (int)(hr.Y + hr.Height + 28);   // 表头下 ~28px（行高 27）
        int rowX = (int)(hr.X + 60);
        SetCursorPos(rowX, rowY);
        Thread.Sleep(150);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(600);

        // 找浮岛（此时名字应为 结束进程 (PID n) 或保持提示）
        var fab = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "结束进程"))
            ?? w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "结束进程 (PID"));
        if (fab == null) { Console.WriteLine("fab not found after select"); return; }
        Console.WriteLine("选中后浮岛: " + fab.Current.Name);
        Click(fab);
        Thread.Sleep(600);
        var fab2 = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "再次点击确认结束"));
        if (fab2 == null)
        {
            // 也许仍是无选中态（点到表头？）——回读当前名字
            var cur = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "结束进程"));
            Console.WriteLine("武装失败，当前: " + cur?.Current.Name);
            return;
        }
        Console.WriteLine("武装态: " + fab2.Current.Name);
        Click(fab2);
        Thread.Sleep(900);
        var after = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "已结束"));
        Console.WriteLine(after != null ? "执行结果: " + after.Current.Name : "执行后浮岛名: (回读)");
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--fab")
{
    // 点击浮岛中心并回读浮岛文本序列：click → 先在列表中点选一行/或确认流程
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        AutomationElement Fab() => w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "结束进程"))
            ?? w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "结束进程 (PID"));
        var fab = Fab();
        if (fab == null) { Console.WriteLine("fab not found"); return; }
        void Click(AutomationElement e)
        {
            var r = e.Current.BoundingRectangle;
            int cx = (int)(r.X + r.Width / 2), cy = (int)(r.Y + r.Height / 2);
            SetCursorPos(cx, cy);
            Thread.Sleep(150);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);   // down
            Thread.Sleep(40);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);   // up
        }
        // 1) 无选中态点击
        Click(fab);
        Thread.Sleep(600);
        var f1 = Fab();
        Console.WriteLine("click1: " + f1?.Current.Name);
        // 2) 再次点击（若第一次提示"先在列表中点选一行"，点第二次应同样提示）
        Click(f1!);
        Thread.Sleep(600);
        var f2 = Fab();
        Console.WriteLine("click2: " + f2?.Current.Name);
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--click")
{
    int x = int.Parse(args[2]), y = int.Parse(args[3]);
    W.SetCursorPos(x, y);
    Thread.Sleep(150);
    W.mouse_event(W.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    Thread.Sleep(30);
    W.mouse_event(W.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    Thread.Sleep(50);
    W.mouse_event(W.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    Thread.Sleep(30);
    W.mouse_event(W.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    Console.WriteLine("double-clicked " + x + "," + y);
    return;
}
if (mode == "--minimize")
{
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.Name.Contains("硬件监视"))
        {
            var wp = w.GetCurrentPattern(WindowPattern.Pattern) as WindowPattern;
            wp?.SetWindowVisualState(WindowVisualState.Minimized);
            Console.WriteLine("minimized");
            Thread.Sleep(2000);
            var still = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
            foreach (AutomationElement x in still)
                Console.WriteLine("window still listed: '" + x.Current.Name + "' offscreen=" + x.Current.IsOffscreen);
            return;
        }
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--align")
{
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        var texts = w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        var hits = new List<(string name, double x, double y)>();
        foreach (AutomationElement t in texts)
        {
            var r = t.Current.BoundingRectangle;
            if (r.Width > 0 && !string.IsNullOrEmpty(t.Current.Name))
                hits.Add((t.Current.Name, r.X, r.Y));
        }
        // 表头行 = 含"应用（按 CPU" 的 y 之后的第一个含"应用"短文本行
        var titleKey = args.Length > 3 ? args[3] : "按 CPU 占用排序";
        var tableTitle = hits.FirstOrDefault(h => h.name.Contains(titleKey));
        if (tableTitle == default) { Console.WriteLine("table title not found"); return; }
        double yTitle = tableTitle.y;
        var band = hits.Where(h => h.y > yTitle && h.y < yTitle + 400).OrderBy(h => h.y).ThenBy(h => h.x).ToList();
        // 分组打印前 4 行带（每 20px 一带）
        double lastY = -99; int row = 0;
        foreach (var h in band)
        {
            if (h.y - lastY > 15) { row++; if (row > 4) break; Console.WriteLine($"-- row {row} (y={h.y:0}) --"); lastY = h.y; }
            Console.WriteLine($"   '{h.name}' x={h.x:0}");
        }
        return;
    }
    Console.WriteLine("main not found");
    return;
}
if (mode == "--restore")
{
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.Name.Contains("硬件监视"))
        {
            var wp = w.GetCurrentPattern(WindowPattern.Pattern) as WindowPattern;
            wp?.SetWindowVisualState(WindowVisualState.Normal);
            Console.WriteLine("restored");
            return;
        }
    }
    return;
}
if (mode == "--invoke")
{
    var btnName = args[2];
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        var btn = w.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, btnName)));
        if (btn != null)
        {
            if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out var pat))
            {
                ((InvokePattern)pat).Invoke();
                Console.WriteLine("invoked " + btnName);
                return;
            }
            Console.WriteLine("no invoke pattern on " + btnName);
            return;
        }
    }
    Console.WriteLine("button not found: " + btnName);
    return;
}
if (mode == "--nav")
{
    var tabName = args[2];
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.NativeWindowHandle == IntPtr.Zero) continue;
        var tab = w.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
            new PropertyCondition(AutomationElement.NameProperty, tabName)));
        if (tab != null)
        {
            (tab.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern)?.Select();
            Console.WriteLine("nav-> " + tabName);
            return;
        }
    }
    Console.WriteLine("tab not found: " + tabName);
    return;
}
if (mode == "--rects")
{
    foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
    {
        if (w.Current.Name.Contains("悬浮窗"))
        {
            // 悬浮窗内的所有按钮位置
            var btns = w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement b in btns)
            {
                var br = b.Current.BoundingRectangle;
                Console.WriteLine($"button '{b.Current.Name}' ({(int)br.X},{(int)br.Y}) {(int)br.Width}x{(int)br.Height}");
            }
        }
        if (!w.Current.IsOffscreen || w.Current.Name.Length > 0)
        {
            var r = w.Current.BoundingRectangle;
            Console.WriteLine($"'{w.Current.Name}' rect=({(int)r.X},{(int)r.Y}) {(int)r.Width}x{(int)r.Height} offscreen={w.Current.IsOffscreen}");
        }
    }
    return;
}
// dump
foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
{
    Console.WriteLine("== " + w.Current.Name + " ==");
    Dump(w, 1);
}
return;

static void Dump(AutomationElement el, int depth)
{
    if (depth > 6) return;
    AutomationElementCollection children;
    try { children = el.FindAll(TreeScope.Children, Condition.TrueCondition); } catch { return; }
    foreach (AutomationElement c in children)
    {
        string name = ""; try { name = c.Current.Name; } catch { }
        if (!string.IsNullOrEmpty(name)) Console.WriteLine(new string(' ', depth * 2) + name);
        Dump(c, depth + 1);
    }
}

static class W
{
    [DllImport("user32")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    public const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04;
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder t, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, ref RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}

struct RECT { public int Left, Top, Right, Bottom; }