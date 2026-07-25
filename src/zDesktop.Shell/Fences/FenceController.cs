using System.Windows.Threading;
using zDesktop.Core.Fences;
using zDesktop.Shell.Desktop;
using zDesktop.Shell.Interop;

namespace zDesktop.Shell.Fences;

/// <summary>
/// 分区功能总控 —— App 层唯一需要打交道的门面。
///
/// <para>把 M3 的七个组件编排起来：配置存储、归属模型、原生控制器、
/// 显示名解析、同步引擎、背景合成、快照与整理。</para>
///
/// <para><b>生命周期</b>：<see cref="Initialize"/> → 每屏 <see cref="AttachLayer"/>
/// → <see cref="Start"/>。退出时 <see cref="Dispose"/> 会还原壁纸。</para>
/// </summary>
public sealed class FenceController : IDisposable
{
    private readonly FenceStore _store;
    private readonly NativeIconController _icons;
    private readonly DesktopItemResolver _resolver;
    private readonly FenceSyncEngine _sync;
    private readonly FenceCompositor _compositor;
    private readonly FenceSnapshotStore _snapshots;
    private readonly FenceOrganizer _organizer;
    private readonly DesktopFocusWatcher _focus;

    /// <summary>
    /// 候选 B 的实时背景窗口（寄生在壁纸层 WorkerW 内，位于图标之下）。
    /// 可用时优先走它 —— 实时重绘、零延迟；不可用时退回候选 A 合成进壁纸。
    /// </summary>
    private FenceBackgroundLayer? _background;

    /// <summary>
    /// 是否启用候选 B（壁纸层实时渲染）。
    /// Win11 实测不可行（见 <see cref="Initialize"/> 中的说明），默认关闭。
    /// </summary>
    public static bool EnableWallpaperLayerRendering { get; set; }

    /// <summary>当前背景渲染走的是哪条路（诊断用）</summary>
    public string BackgroundMode =>
        _background is { IsAttached: true } ? "候选B/实时图层" : "候选A/合成壁纸";

    private readonly List<FenceLayer> _layers = new();
    private DispatcherTimer? _pollTimer;

    /// <summary>
    /// 合成去抖。
    ///
    /// <para>换壁纸会触发 Windows 自带的淡入过渡，每次都是一次可感知的闪烁。
    /// 连续操作（拖完接着缩放、改完名再折叠）若每步都合成，就会连闪好几下。
    /// 合并到最后一次再做。</para>
    /// </summary>
    private DispatcherTimer? _composeDebounce;

    /// <summary>
    /// 上次合成时的分区布局指纹。
    /// 内容没变就不重设壁纸 —— 没有变化的重设纯粹是白闪一下。
    /// </summary>
    private string _lastComposedSignature = string.Empty;

    private FenceConfig _config = new();
    private FenceAssignmentModel _assignments = new();
    private FenceCoordinateSpace _space = null!;

    /// <summary>分区是否可用。原生控制器连不上时整体降级为不可用（§七）。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>不可用原因</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>「自动排列图标」是否挡住了分区（硬前置条件，§4.2 决策 2）</summary>
    public bool IsBlockedByAutoArrange => _icons.IsAutoArrange;

    /// <summary>编辑模式 —— 开启后覆盖层整层接管鼠标，可拖拽新建与缩放</summary>
    public bool EditMode
    {
        get => _layers.Count > 0 && _layers[0].EditMode;
        set
        {
            foreach (var layer in _layers) layer.EditMode = value;
            Console.WriteLine($"[Fence] 编辑模式: {(value ? "开启（整层接管鼠标）" : "关闭（恢复透传）")}");
        }
    }

    /// <summary>分区数量</summary>
    public int FenceCount => _config.Fences.Count;

    /// <summary>需要重绘背景时触发（供 App 决定何时合成，避免高频重复合成）</summary>
    public event Action? BackgroundInvalidated;

    public FenceController(RestoreJournal journal, string? dataDir = null)
    {
        _store = new FenceStore(dataDir);
        _icons = new NativeIconController();
        _resolver = new DesktopItemResolver();
        _sync = new FenceSyncEngine(_icons, _resolver);
        _compositor = new FenceCompositor(journal);
        _snapshots = new FenceSnapshotStore(dataDir == null ? null : System.IO.Path.Combine(dataDir, "snapshots"));
        _organizer = new FenceOrganizer(_sync, _icons, _resolver, _snapshots);
        _focus = new DesktopFocusWatcher();
    }

    /// <summary>加载配置并连接桌面。返回是否可用。</summary>
    public bool Initialize()
    {
        _config = _store.Load();
        _assignments = new FenceAssignmentModel(_config.Assignments);
        _space = FenceCoordinateSpace.Current();

        if (!_icons.Connect())
        {
            IsAvailable = false;
            UnavailableReason = _icons.LastError;
            Console.WriteLine($"[Fence] 分区功能不可用：{UnavailableReason}");
            return false;
        }

        _resolver.Refresh();
        _assignments.PruneOrphans(_resolver.AllPaths);

        // 候选 B（寄生壁纸层实时渲染）在 Windows 11 上实测不可行，默认关闭。
        //
        // 前置条件全部满足：0x052C 后能拿到壁纸层 WorkerW、Z 序确认在图标层之下、
        // 未被第三方占用、HwndSource 也成功寄生（日志确认）。但画面始终不出现。
        // 四种渲染方式全试过：Window+AllowsTransparency / WS_EX_LAYERED+色键
        // （SetLayeredWindowAttributes 返回 87）/ 不透明+WS_CHILD / HwndSource+WS_CHILD。
        //
        // 结论：Win11 的壁纸不再由该 WorkerW 绘制，而是 DWM 直接合成在它**之上**，
        // 放进去的内容会被壁纸盖住。经典 WorkerW 技巧在新版 Win11 上已失效。
        //
        // 代码保留：Win10 与旧版 Win11 仍适用该拓扑，将来可按系统版本条件启用；
        // 详见 spikes/M3-WorkerWProbe 的拓扑探测结果。
        if (EnableWallpaperLayerRendering)
        {
            _background = new FenceBackgroundLayer();
            if (!_background.Attach())
            {
                _background.Dispose();
                _background = null;
            }
        }

        if (_background == null)
            Console.WriteLine("[Fence] 背景走候选 A（合成进壁纸）");

        IsAvailable = true;
        Console.WriteLine($"[Fence] 已就绪：{_config.Fences.Count} 个分区，{_assignments.Count} 条归属，" +
                          $"背景渲染={BackgroundMode}");

        if (IsBlockedByAutoArrange)
            Console.WriteLine("[Fence] ⚠ 「自动排列图标」已开启，分区无法生效 —— 需引导用户关闭");

        return true;
    }

    /// <summary>把一个显示器的分区层挂上来</summary>
    public void AttachLayer(FenceLayer layer, string monitorKey)
    {
        layer.MonitorKey = monitorKey;
        layer.Rebuild(_config.Fences);

        layer.FenceCreateRequested += OnFenceCreateRequested;
        layer.FencesChanged += OnFencesChanged;
        layer.FenceDeleteRequested += DeleteFence;
        layer.FenceRenameRequested += f => RenameRequested?.Invoke(f);

        _layers.Add(layer);
    }

    /// <summary>需要弹出重命名输入时触发（UI 由 App 层提供）</summary>
    public event Action<Fence>? RenameRequested;

    /// <summary>
    /// 摘掉全部分区层（覆盖层重建前调用）。
    ///
    /// 不摘的话 <see cref="_layers"/> 会累积已销毁的旧层，
    /// <see cref="EditMode"/> 读到的是过期层的状态，切换编辑模式随之错乱。
    /// </summary>
    public void DetachLayers()
    {
        foreach (var layer in _layers)
        {
            layer.ResetInteraction();
            layer.FenceCreateRequested -= OnFenceCreateRequested;
            layer.FencesChanged -= OnFencesChanged;
            layer.FenceDeleteRequested -= DeleteFence;
        }
        _layers.Clear();
    }

    /// <summary>开始工作：焦点驱动轮询 + 首次归位与合成</summary>
    public void Start()
    {
        if (!IsAvailable) return;

        _focus.FocusChanged += OnFocusChanged;
        _focus.Start();

        SyncAndCompose();
    }

    private void OnFocusChanged(bool focused)
    {
        if (focused) StartPolling();
        else StopPolling();
    }

    private void StartPolling()
    {
        if (_pollTimer != null) return;

        // 2Hz —— 决策 4。只在桌面聚焦期间存在，失焦即销毁
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _pollTimer.Tick += (_, _) => PollOnce();
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        if (_pollTimer == null) return;

        _pollTimer.Stop();
        _pollTimer = null;

        // 失焦时补最后一次读取并落盘，避免漏掉最后一次拖动
        PollOnce();
        Save();
    }

    private void PollOnce()
    {
        if (!IsAvailable) return;

        try
        {
            var changes = _sync.PollFromExplorer(_config, _assignments, _space);
            if (changes > 0)
            {
                // 归属变了就重排该分区，让图标立刻对齐到槽位
                _sync.SyncToExplorer(_config, _assignments, _space);
                Save();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fence] 轮询异常: {ex.Message}");
        }
    }

    // ===== 分区增删改 =====

    /// <summary>在指定显示器上新建分区</summary>
    public Fence CreateFence(string monitorKey, FenceRect rect, string? name = null)
    {
        var fence = new Fence
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            MonitorKey = monitorKey,
            Name = name ?? $"分区 {_config.Fences.Count + 1}",
            Rect = rect,
        };

        _config.Fences.Add(fence);
        RebuildLayers();
        Save();
        SyncAndCompose();

        Console.WriteLine($"[Fence] 已新建「{fence.Name}」于 {monitorKey} {rect}");
        return fence;
    }

    /// <summary>删除分区。组内图标解除归属但**不移动**——用户没要求动文件位置。</summary>
    public void DeleteFence(Fence fence)
    {
        _config.Fences.RemoveAll(f => f.Id == fence.Id);
        _assignments.PruneMissingFences(_config.Fences.Select(f => f.Id));

        RebuildLayers();
        Save();
        SyncAndCompose();

        Console.WriteLine($"[Fence] 已删除「{fence.Name}」");
    }

    /// <summary>重命名分区</summary>
    public void RenameFence(Fence fence, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;

        fence.Name = newName.Trim();
        foreach (var layer in _layers) layer.RefreshGeometry();
        Save();
        SyncAndCompose();
    }

    private void OnFenceCreateRequested(FenceLayer layer, FenceRect rect)
        => CreateFence(layer.MonitorKey, rect);

    private void OnFencesChanged()
    {
        Save();
        SyncAndCompose();
    }

    private void RebuildLayers()
    {
        foreach (var layer in _layers) layer.Rebuild(_config.Fences);
    }

    // ===== 整理与撤销 =====

    /// <summary>干跑预览：将要归入各分区的文件</summary>
    public Dictionary<string, List<string>> PreviewOrganize()
        => IsAvailable ? _organizer.Preview(_config, _assignments) : new();

    /// <summary>执行一键整理</summary>
    public OrganizeResult Organize()
    {
        if (!IsAvailable) return new OrganizeResult(null, 0, 0);

        var result = _organizer.Organize(_config, _assignments, _space);
        if (result.Succeeded) Save();
        return result;
    }

    /// <summary>撤销最近一次整理</summary>
    public int UndoLatest()
    {
        if (!IsAvailable) return -1;

        var restored = _organizer.UndoLatest(_assignments);
        if (restored >= 0) Save();
        return restored;
    }

    /// <summary>快照列表（供撤销 UI）</summary>
    public List<FenceSnapshot> ListSnapshots() => _snapshots.List();

    // ===== 同步与合成 =====

    /// <summary>
    /// 写回图标坐标并重新合成分区背景。
    /// 图标归位立即执行，壁纸合成走去抖以避免连续闪烁。
    /// </summary>
    public void SyncAndCompose()
    {
        if (!IsAvailable) return;

        try
        {
            _sync.SyncToExplorer(_config, _assignments, _space);

            if (_background != null)
            {
                // 候选 B：实时重绘，无需去抖也无需碰壁纸
                RenderBackgroundLayer();
            }
            else
            {
                ScheduleCompose();
            }

            BackgroundInvalidated?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fence] 同步失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 候选 B：把分区矩形交给实时背景层重绘。
    /// 立即生效，无延迟、不改壁纸文件。
    /// </summary>
    private void RenderBackgroundLayer()
    {
        if (_background == null) return;

        // Explorer 重启会销毁壁纸层，需重新寄生
        if (!_background.ReattachIfNeeded())
        {
            Console.WriteLine("[Fence] 壁纸层已失效且无法重新寄生，回退候选 A");
            _background.Dispose();
            _background = null;
            ScheduleCompose();
            return;
        }

        _background.FitToLayer();

        // 底图：逐屏取当前壁纸。我们的窗口不透明，必须自己把壁纸画出来
        var backdrops = new List<(IconRect, string?)>();
        using (var surface = new WallpaperSurface())
        {
            foreach (var mw in surface.Enumerate())
            {
                var topLeft = _space.ScreenToClient(mw.Rect.Left, mw.Rect.Top);
                backdrops.Add((
                    new IconRect(topLeft.X, topLeft.Y, mw.Rect.Width, mw.Rect.Height),
                    string.IsNullOrEmpty(mw.WallpaperPath) ? null : mw.WallpaperPath));
            }
        }
        _background.SetBackdrops(backdrops);

        var rects = new List<(IconRect, string, bool)>();
        foreach (var fence in _config.Fences)
        {
            var rect = _space.FenceToIconSpace(fence);
            if (rect == null) continue; // 显示器已拔掉
            rects.Add((rect.Value, fence.Color, fence.Collapsed));
        }

        _background.Render(rects);
    }

    /// <summary>把合成推迟到操作停止之后，连续编辑只合成最后一次</summary>
    private void ScheduleCompose()
    {
        _composeDebounce ??= CreateComposeDebounce();
        _composeDebounce.Stop();
        _composeDebounce.Start();
    }

    private DispatcherTimer CreateComposeDebounce()
    {
        // 120ms：足以把「拖完接着缩放」这类连续操作合并成一次，
        // 又不至于让松手到底色跟上的延迟变得可感知。
        // 实测 350ms 时松手后要等约 0.6 秒底色才跳到新位置，读起来就像闪了一下。
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Compose();
        };
        return timer;
    }

    /// <summary>分区布局指纹 —— 只要它没变，重新合成出来的图就是一样的</summary>
    private string BuildSignature()
    {
        var parts = _config.Fences
            .OrderBy(f => f.Id, StringComparer.Ordinal)
            .Select(f => $"{f.Id}|{f.MonitorKey}|{f.Rect.X:F1},{f.Rect.Y:F1},{f.Rect.Width:F1},{f.Rect.Height:F1}|{f.Color}|{f.Collapsed}");

        return string.Join(";", parts);
    }

    /// <summary>逐屏合成分区背景</summary>
    private void Compose()
    {
        if (!_compositor.IsAvailable) return;

        // 布局没变就别重设壁纸 —— 那只会白闪一下
        var signature = BuildSignature();
        if (signature == _lastComposedSignature) return;
        _lastComposedSignature = signature;

        using var surface = new WallpaperSurface();
        foreach (var mw in surface.Enumerate())
        {
            // 壁纸接口与 MonitorSet 是两套显示器标识，按矩形匹配
            var monitor = _space.Monitors.FirstOrDefault(m =>
                m.Bounds.Left == mw.Rect.Left && m.Bounds.Top == mw.Rect.Top &&
                m.Bounds.Right == mw.Rect.Right && m.Bounds.Bottom == mw.Rect.Bottom);

            if (monitor == null) continue;

            var rects = new List<(IconRect, string, bool)>();
            foreach (var fence in _config.Fences.Where(f =>
                         string.Equals(f.MonitorKey, monitor.Key, StringComparison.OrdinalIgnoreCase)))
            {
                // 合成用的是「相对该屏左上角」的物理像素，与图标空间差一个屏原点
                var dpi = monitor.Dpi;
                var x = (int)Math.Round(DpiHelper.ToPhysical(fence.Rect.X, dpi))
                        + (monitor.WorkArea.Left - monitor.Bounds.Left);
                var y = (int)Math.Round(DpiHelper.ToPhysical(fence.Rect.Y, dpi))
                        + (monitor.WorkArea.Top - monitor.Bounds.Top);
                var w = (int)Math.Round(DpiHelper.ToPhysical(fence.Rect.Width, dpi));
                var h = (int)Math.Round(DpiHelper.ToPhysical(fence.Rect.Height, dpi));

                rects.Add((new IconRect(x, y, w, h), fence.Color, fence.Collapsed));
            }

            if (rects.Count == 0)
            {
                // 该屏没有分区 —— 绝不碰壁纸。
                // 没有这条判断，装上 zDesktop 就会把用户壁纸重新编码一遍设回去，
                // 哪怕一个分区都没建，直接违反零破坏契约。
                _compositor.RestoreMonitor(mw.MonitorId);
                continue;
            }

            _compositor.ComposeAndApply(mw.MonitorId, mw.Rect, rects);
        }
    }

    /// <summary>显示器配置变化后重建坐标空间</summary>
    public void OnDisplayChanged()
    {
        _space = FenceCoordinateSpace.Current();
        _sync.ForgetWrittenPositions();
        RebuildLayers();
        SyncAndCompose();
    }

    /// <summary>命中测试聚合 —— 供覆盖层调用</summary>
    public bool HitTest(FenceLayer layer, System.Windows.Point point) => layer.HitTest(point);

    /// <summary>落盘配置</summary>
    public void Save()
    {
        _config.Assignments = _assignments.ToList();
        _store.Save(_config);
    }

    public void Dispose()
    {
        StopPolling();
        _composeDebounce?.Stop();
        DetachLayers();
        _focus.Dispose();
        Save();

        // 候选 B 的背景层随进程销毁即消失，无需还原壁纸
        _background?.Dispose();
        _background = null;

        // 还原壁纸 —— 零破坏契约要求退出后桌面与未安装时一致
        _compositor.RestoreAll();
        _compositor.Dispose();
        _icons.Dispose();
    }
}
