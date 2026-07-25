using System.Text;

namespace ZDesktop.Spikes.M2;

/// <summary>
/// M2 spike：验证能否跨进程控制 Explorer 桌面图标坐标（设计案 v3.1 §四、§十一 M2）。
///
/// 这是分区功能的存亡判定：
///   成功 → Plan A（图标留在桌面根目录，文件路径不变）
///   失败 → Plan B（文件夹门户，文件被真实移动进子文件夹）
///
/// **安全**：本工具会真实移动桌面图标。执行前快照全部坐标，
/// finally 中无条件还原，异常路径也不例外。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static readonly List<string> _fail = new();

    private static void Check(bool ok, string name, string detail = "")
    {
        if (ok)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [PASS] {name}");
            _pass++;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [FAIL] {name}{(detail == "" ? "" : " -- " + detail)}");
            _fail.Add($"{name}: {detail}");
        }
        Console.ResetColor();
    }

    private static void Info(string s) => Console.WriteLine($"  {s}");

    private static void Section(string s)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(s);
        Console.ResetColor();
    }

    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== M2 spike：原生桌面图标坐标控制（设计案 v3.1 §四）===");

        var lv = DesktopListView.Open(out var error);
        if (lv == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ABORT] 无法访问桌面 ListView：{error}");
            Console.ResetColor();
            Console.WriteLine("\n结论：Plan A 在本机不可行，需转 Plan B（文件夹门户）。");
            return 2;
        }

        using (lv)
        {
            Section("1. 环境");
            Info($"SysListView32 = 0x{lv.Handle.ToInt64():X}  (explorer pid={lv.ProcessId})");
            Info($"窗口样式 = 0x{lv.Style:X8}");
            Info($"扩展样式 = 0x{lv.ExtendedStyle:X8}");
            Info($"「自动排列图标」= {(lv.IsAutoArrange ? "开启 ⚠" : "关闭")}");
            Info($"「与网格对齐」  = {(lv.IsSnapToGrid ? "开启" : "关闭")}");
            var spacing = lv.ItemSpacing;
            Info($"图标网格间距 = {spacing.Cx} x {spacing.Cy} 物理像素");
            Check(true, "已打开 explorer 进程内存并分配远端缓冲区");

            var count = lv.Count;
            Info($"桌面图标数 = {count}");
            Check(count > 0, "读取到图标数量", count <= 0 ? "LVM_GETITEMCOUNT 返回 0，桌面可能没有图标" : "");

            if (count <= 0)
            {
                Console.WriteLine("\n桌面无图标，无法继续验证坐标读写。请在桌面放几个文件后重跑。");
                return 2;
            }

            Section("2. 读取坐标与名称（LVM_GETITEMPOSITION / LVM_GETITEMTEXTW）");

            var snapshot = new Native.POINT[count];
            var names = new string[count];
            var readOk = true;

            for (var i = 0; i < count; i++)
            {
                snapshot[i] = lv.GetPosition(i);
                names[i] = lv.GetItemText(i);
                if (names[i].StartsWith("<")) readOk = false;
            }

            var show = Math.Min(count, 8);
            for (var i = 0; i < show; i++)
                Info($"[{i,2}] {snapshot[i],-12} {names[i]}");
            if (count > show) Info($"... 其余 {count - show} 项省略");

            var allZero = snapshot.All(p => p.X == 0 && p.Y == 0);
            Check(!allZero, "坐标读取有效", allZero ? "全部为 (0,0)，跨进程读取很可能失败了" : "");
            Check(readOk && names.Any(n => !string.IsNullOrEmpty(n)),
                "名称读取有效（索引→文件的映射可建立）",
                readOk ? "" : "LVITEMW 布局或远端读写有问题");

            var snapWasOn = lv.IsSnapToGrid;
            var target = 0;
            var orig = snapshot[target];

            try
            {
                Section("3. 写入坐标 —— 任意坐标（不对齐网格）");

                var probe = new Native.POINT { X = orig.X + 96, Y = orig.Y + 96 };
                var wrote = lv.SetPosition(target, probe.X, probe.Y);
                Check(wrote, "SetPosition 调用成功", wrote ? "" : "WriteProcessMemory 失败");

                Thread.Sleep(300);
                var after = lv.GetPosition(target);
                Info($"[{target}] {names[target]}：{orig} → 请求 {probe} → 实测 {after}");
                Check(after.X != orig.X || after.Y != orig.Y, "图标确实被移动了",
                    "坐标完全没变，跨进程写入未生效");

                var exact = after.X == probe.X && after.Y == probe.Y;
                if (!exact)
                {
                    Info($"未落在请求坐标 —— 偏移 ({after.X - probe.X},{after.Y - probe.Y})，" +
                         (snapWasOn ? "与「与网格对齐」开启一致（被吸附到最近格点）" : "原因待查"));
                }

                Section("4. 写入坐标 —— 对齐到网格格点");

                // 以原始坐标为格点基准，按实测间距整格偏移，理论上不会被吸附改写
                var gp = new Native.POINT { X = orig.X + spacing.Cx, Y = orig.Y + spacing.Cy };
                lv.SetPosition(target, gp.X, gp.Y);
                Thread.Sleep(300);
                var gAfter = lv.GetPosition(target);
                Info($"[{target}]：请求 {gp} → 实测 {gAfter}");
                Check(gAfter.X == gp.X && gAfter.Y == gp.Y, "格点坐标可精确写入",
                    $"实测 {gAfter}，与请求 {gp} 不符");

                Section("5. 关闭「与网格对齐」后写入任意坐标");

                if (snapWasOn) lv.SetSnapToGrid(false);
                Thread.Sleep(200);
                Info($"「与网格对齐」现为 {(lv.IsSnapToGrid ? "开启" : "关闭")}");

                var free = new Native.POINT { X = orig.X + 37, Y = orig.Y + 53 };
                lv.SetPosition(target, free.X, free.Y);
                Thread.Sleep(300);
                var fAfter = lv.GetPosition(target);
                Info($"[{target}]：请求 {free} → 实测 {fAfter}");
                Check(fAfter.X == free.X && fAfter.Y == free.Y, "关闭吸附后任意坐标可精确写入",
                    $"实测 {fAfter}，与请求 {free} 不符");

                Section("6. 批量写入（模拟一次分区归位，按网格排布）");

                var batchCount = Math.Min(count, 5);
                var batchOk = true;
                for (var i = 0; i < batchCount; i++)
                {
                    if (!lv.SetPosition(i, orig.X + i * spacing.Cx, orig.Y + spacing.Cy * 2)) { batchOk = false; break; }
                }
                Thread.Sleep(400);

                var verified = 0;
                for (var i = 0; i < batchCount; i++)
                {
                    var p = lv.GetPosition(i);
                    if (p.X == orig.X + i * spacing.Cx && p.Y == orig.Y + spacing.Cy * 2) verified++;
                }

                Info($"批量写入 {batchCount} 项，回读一致 {verified} 项");
                Check(batchOk && verified == batchCount, "批量写入全部生效",
                    $"仅 {verified}/{batchCount} 项生效");
            }
            finally
            {
                Section("7. 还原（坐标 + 「与网格对齐」原值）");

                // 先还原吸附设置，否则还原坐标时又会被吸附走
                if (snapWasOn && !lv.IsSnapToGrid)
                {
                    lv.SetSnapToGrid(true);
                    Thread.Sleep(200);
                }
                Info($"「与网格对齐」已还原为 {(lv.IsSnapToGrid ? "开启" : "关闭")}（原值 {(snapWasOn ? "开启" : "关闭")}）");

                var restoreOk = true;
                for (var i = 0; i < count; i++)
                {
                    if (!lv.SetPosition(i, snapshot[i].X, snapshot[i].Y)) restoreOk = false;
                }
                Thread.Sleep(400);

                var mismatched = 0;
                for (var i = 0; i < count; i++)
                {
                    var p = lv.GetPosition(i);
                    if (p.X != snapshot[i].X || p.Y != snapshot[i].Y) mismatched++;
                }

                Check(restoreOk && mismatched == 0, "全部图标已还原到原始位置",
                    $"{mismatched} 项未还原 —— 请手动右键桌面→刷新，或按名称排序后重排");
            }
        }

        Section("结果");
        Console.WriteLine($"通过 {_pass} / 失败 {_fail.Count}");

        if (_fail.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            foreach (var f in _fail) Console.WriteLine($"  - {f}");
            Console.ResetColor();
            Console.WriteLine("\n结论：Plan A 存在阻碍，详见上方失败项。");
            return 1;
        }

        WallpaperCompose.Run(Path.Combine(Path.GetTempPath(), "zdesktop-m2-spike"));

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n结论：Plan A 可行 —— 可跨进程读写原生桌面图标坐标，");
        Console.WriteLine("      分区功能可以在不接管图标层的前提下实现。");
        Console.ResetColor();
        return 0;
    }
}
