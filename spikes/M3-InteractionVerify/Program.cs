using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ZDesktop.Spikes.M3Interaction;

/// <summary>
/// M3-D 交互回归：客观验证用户报告的两个缺陷是否修好。
///
/// <list type="number">
/// <item><b>拖动跟手</b>：用 SendInput 模拟一次标题栏拖拽，检查分区最终位移是否等于
/// 鼠标位移。覆盖层靠 HTTRANSPARENT 透传，光标移出标题栏后若丢消息，位移就会明显偏小。</item>
/// <item><b>桌面点击透传</b>：用 <c>WindowFromPoint</c> 判定。该 API 会跳过
/// 命中测试透明的窗口 —— 空白桌面处若返回我们的覆盖层，就说明图标点不动了。</item>
/// </list>
///
/// <para>本工具会移动鼠标并拖动分区，结束时把分区位置改回原值。</para>
/// </summary>
internal static class Program
{
    private static int _pass;
    private static readonly List<string> _fail = new();

    private static void Check(bool ok, string name, string detail = "")
    {
        if (ok) { Ok($"[PASS] {name}"); _pass++; }
        else { Err($"[FAIL] {name}{(detail == "" ? "" : " -- " + detail)}"); _fail.Add($"{name}: {detail}"); }
    }
    private static void Ok(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("  " + s); Console.ResetColor(); }
    private static void Err(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  " + s); Console.ResetColor(); }
    private static void Info(string s) => Console.WriteLine("  " + s);
    private static void Section(string s) { Console.WriteLine(); Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(s); Console.ResetColor(); }

    private static string FencesPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zDesktop", "fences.json");

    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== M3-D 交互回归（拖动跟手 / 桌面点击透传）===");

        if (!File.Exists(FencesPath))
        {
            Err("[ABORT] 未找到 fences.json，请先种一个分区再运行");
            return 2;
        }

        var (startX, startY, w, h) = ReadFirstFenceRect();
        Info($"分区初始矩形 ({startX},{startY}) {w}x{h}");

        // 标题栏中部（分区坐标是相对主屏工作区的 DIP；本机主屏工作区原点为 (0,0)、100% 缩放，
        // 故可直接当屏幕坐标用。换机器需按 DPI 换算。）
        var grabX = (int)(startX + w / 2);
        var grabY = (int)(startY + 16);

        Section("1. 默认态：空白桌面必须透传给原生桌面");

        // 取一块远离分区与组件的空白区域
        var emptyX = 700; var emptyY = 800;
        var atEmpty = N.WindowFromPoint(new N.POINT { X = emptyX, Y = emptyY });
        var emptyClass = ClassOf(atEmpty);
        Info($"({emptyX},{emptyY}) 命中窗口类名 = {emptyClass}");
        Check(!IsOurOverlay(atEmpty), "空白桌面处未被覆盖层拦截",
            $"命中 {emptyClass} —— 桌面图标将无法点击");

        Section("2. 分区标题栏必须命中覆盖层");

        var atTitle = N.WindowFromPoint(new N.POINT { X = grabX, Y = grabY });
        Info($"({grabX},{grabY}) 命中窗口类名 = {ClassOf(atTitle)}");
        Check(IsOurOverlay(atTitle), "标题栏处命中覆盖层", "标题栏点不中，分区无法拖动");

        Section("3. 拖动跟手");

        const int dx = 220, dy = 160;
        Info($"模拟拖拽：从 ({grabX},{grabY}) 位移 ({dx},{dy})，分 24 步");

        // 基准取**光标实际位移**而非「我要求的位移」：
        // SendInput 的绝对坐标要归一化到 0..65535 再由系统换回像素，两次取整会带来数像素误差。
        // 要验的是「分区是否 1:1 跟随光标」，拿实际光标位移做基准才测得准。
        var (cursorDx, cursorDy) = DragMouse(grabX, grabY, dx, dy, steps: 24);
        Thread.Sleep(900); // 等去抖落盘

        var (endX, endY, _, _) = ReadFirstFenceRect();
        var movedX = endX - startX;
        var movedY = endY - startY;
        Info($"光标实际位移 ({cursorDx},{cursorDy})");
        Info($"分区实际位移 ({movedX:F0},{movedY:F0})");

        // 允许 3px 误差（末步取整）。丢消息不会导致误差 —— 位移是累加的，
        // 只要最后一个 MouseMove 收到，总位移就等于光标总位移；
        // 差得多才说明拖拽被中断。
        Check(Math.Abs(movedX - cursorDx) <= 3 && Math.Abs(movedY - cursorDy) <= 3,
            "分区位移与光标位移 1:1 一致（拖动跟手）",
            $"分区 ({movedX:F0},{movedY:F0}) vs 光标 ({cursorDx},{cursorDy}) —— 拖拽中途被打断");

        Section("4. 拖动结束后必须恢复透传");

        Thread.Sleep(300);
        var afterDrag = N.WindowFromPoint(new N.POINT { X = emptyX, Y = emptyY });
        Info($"({emptyX},{emptyY}) 命中窗口类名 = {ClassOf(afterDrag)}");
        Check(!IsOurOverlay(afterDrag), "拖动结束后空白桌面恢复透传",
            "交互状态未复位，整窗仍在拦截鼠标 —— 桌面图标点不动");

        Section("5. 闪烁量化（松手后重设壁纸的过渡）");

        // 再拖一次，松手后立刻连拍，量化淡入过渡
        Info("再拖一次并连拍采样…");
        var probeX = (int)(endX + 420 / 2);
        var probeY = (int)(endY + 16);
        MoveTo(probeX, probeY);
        Thread.Sleep(150);
        SendMouse(N.MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(120);
        for (var i = 1; i <= 12; i++) { MoveTo(probeX - 10 * i, probeY - 6 * i); Thread.Sleep(16); }
        Thread.Sleep(150);
        SendMouse(N.MOUSEEVENTF_LEFTUP);

        // 采样区域取分区所在的一片桌面，避开任务栏与组件
        FlickerMeter.Measure(new System.Drawing.Rectangle(300, 150, 640, 400), durationMs: 2200);

        Section("6. 还原分区位置");

        WriteFirstFenceOrigin(startX, startY);
        Info($"已把分区位置写回 ({startX},{startY})（重启 zDesktop 后生效）");

        Section("结果");
        Console.WriteLine($"通过 {_pass} / 失败 {_fail.Count}");
        if (_fail.Count > 0) { foreach (var f in _fail) Err("- " + f); return 1; }
        Ok("M3-D 交互回归全绿");
        return 0;
    }

    // ===== 分区配置读写 =====

    private static (double X, double Y, double W, double H) ReadFirstFenceRect()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FencesPath));
        var rect = doc.RootElement.GetProperty("fences")[0].GetProperty("rect");
        return (rect.GetProperty("x").GetDouble(), rect.GetProperty("y").GetDouble(),
                rect.GetProperty("width").GetDouble(), rect.GetProperty("height").GetDouble());
    }

    private static void WriteFirstFenceOrigin(double x, double y)
    {
        var text = File.ReadAllText(FencesPath);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name != "fences") { prop.WriteTo(writer); continue; }

                writer.WritePropertyName("fences");
                writer.WriteStartArray();
                var first = true;
                foreach (var fence in prop.Value.EnumerateArray())
                {
                    writer.WriteStartObject();
                    foreach (var fp in fence.EnumerateObject())
                    {
                        if (first && fp.Name == "rect")
                        {
                            writer.WritePropertyName("rect");
                            writer.WriteStartObject();
                            writer.WriteNumber("x", x);
                            writer.WriteNumber("y", y);
                            writer.WriteNumber("width", fp.Value.GetProperty("width").GetDouble());
                            writer.WriteNumber("height", fp.Value.GetProperty("height").GetDouble());
                            writer.WriteEndObject();
                        }
                        else fp.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                    first = false;
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        File.WriteAllBytes(FencesPath, stream.ToArray());
    }

    // ===== 窗口判定 =====

    private static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "<null>";
        var sb = new StringBuilder(128);
        N.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>该窗口是否属于 zDesktop 进程</summary>
    private static bool IsOurOverlay(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        N.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName
                .Equals("zDesktop.App", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ===== 鼠标模拟 =====

    /// <summary>模拟一次拖拽，返回光标按下到抬起之间的**实际**位移</summary>
    private static (int Dx, int Dy) DragMouse(int x, int y, int dx, int dy, int steps)
    {
        MoveTo(x, y);
        Thread.Sleep(150);

        // 按下瞬间的真实光标位置才是拖拽基准
        N.GetCursorPos(out var down);
        SendMouse(N.MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(150);

        for (var i = 1; i <= steps; i++)
        {
            MoveTo(x + dx * i / steps, y + dy * i / steps);
            Thread.Sleep(16); // 约 60fps，模拟真实拖动速度
        }

        Thread.Sleep(200); // 让最后一个 MouseMove 被处理完再抬起
        N.GetCursorPos(out var up);
        SendMouse(N.MOUSEEVENTF_LEFTUP);

        return (up.X - down.X, up.Y - down.Y);
    }

    private static void MoveTo(int x, int y)
    {
        // SendInput 的绝对坐标是 0..65535 归一化到主屏
        var sw = N.GetSystemMetrics(0);
        var sh = N.GetSystemMetrics(1);
        var input = new N.INPUT
        {
            type = 0,
            u = new N.InputUnion
            {
                mi = new N.MOUSEINPUT
                {
                    dx = x * 65535 / sw,
                    dy = y * 65535 / sh,
                    dwFlags = N.MOUSEEVENTF_MOVE | N.MOUSEEVENTF_ABSOLUTE,
                }
            }
        };
        N.SendInput(1, new[] { input }, Marshal.SizeOf<N.INPUT>());
    }

    private static void SendMouse(uint flags)
    {
        var input = new N.INPUT { type = 0, u = new N.InputUnion { mi = new N.MOUSEINPUT { dwFlags = flags } } };
        N.SendInput(1, new[] { input }, Marshal.SizeOf<N.INPUT>());
    }
}

internal static class N
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);
}
