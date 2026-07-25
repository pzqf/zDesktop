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

    private readonly List<FenceLayer> _layers = new();
    private DispatcherTimer? _pollTimer;

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

        IsAvailable = true;
        Console.WriteLine($"[Fence] 已就绪：{_config.Fences.Count} 个分区，{_assignments.Count} 条归属");

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

    /// <summary>写回图标坐标并重新合成分区背景</summary>
    public void SyncAndCompose()
    {
        if (!IsAvailable) return;

        try
        {
            _sync.SyncToExplorer(_config, _assignments, _space);
            Compose();
            BackgroundInvalidated?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fence] 同步失败: {ex.Message}");
        }
    }

    /// <summary>逐屏合成分区背景</summary>
    private void Compose()
    {
        if (!_compositor.IsAvailable) return;

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
        _focus.Dispose();
        Save();

        // 还原壁纸 —— 零破坏契约要求退出后桌面与未安装时一致
        _compositor.RestoreAll();
        _compositor.Dispose();
        _icons.Dispose();
    }
}
