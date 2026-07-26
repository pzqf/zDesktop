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

    // ===== 首次运行引导（M4，设计案 v3.1 §六）=====

    private readonly FirstRunStore _firstRun = new();

    /// <summary>是否应展示引导卡片（尚未展示过，且桌面上确实有可整理的文件）</summary>
    public bool ShouldShowOnboarding => IsAvailable && _firstRun.ShouldShowOnboarding;

    /// <summary>桌面上的文件数（引导文案用）</summary>
    public int DesktopFileCount => _resolver.AllPaths.Count;

    /// <summary>标记引导已展示 —— 「以后再说」之后不再弹第二次</summary>
    public void MarkOnboardingShown() => _firstRun.MarkOnboardingShown();

    /// <summary>
    /// 生成首次运行的整理建议。**纯计算，不写任何东西。**
    /// </summary>
    public OrganizeProposal BuildProposal()
    {
        _resolver.Refresh();

        var primary = MonitorSet.Primary(_space.Monitors.ToList());
        var (_, _, width, height) = primary.WorkAreaDip;

        // 网格间距必须取真实值：分区尺寸按它算，写死会让分区装不下自己的图标
        var (cx, cy) = _icons.ItemSpacing;

        return FenceProposal.Build(_resolver.Snapshots.Values.ToList(), width, height, cx, cy);
    }

    /// <summary>在桌面上预演建议方案（只画不写）</summary>
    public void ShowProposalPreview(OrganizeProposal proposal)
    {
        var primary = MonitorSet.Primary(_space.Monitors.ToList());
        var items = proposal.Fences
            .Select(f => (f.Rect, f.Name, f.Color, f.Files.Count))
            .ToList();

        foreach (var layer in _layers)
        {
            // 建议方案目前只落在主屏
            if (string.Equals(layer.MonitorKey, primary.Key, StringComparison.OrdinalIgnoreCase))
                layer.ShowProposalPreview(items);
        }
    }

    /// <summary>清除方案预演</summary>
    public void ClearProposalPreview()
    {
        foreach (var layer in _layers) layer.ClearProposalPreview();
    }

    /// <summary>
    /// 应用建议方案：创建分区 → 落盘快照 → 归属入区 → 写回坐标。
    /// </summary>
    /// <returns>整理结果；快照失败时不执行任何修改</returns>
    public OrganizeResult ApplyProposal(OrganizeProposal proposal)
    {
        if (!IsAvailable || proposal.Fences.Count == 0)
            return new OrganizeResult(null, 0, 0);

        ClearProposalPreview();

        var primary = MonitorSet.Primary(_space.Monitors.ToList());

        // 先建分区（此时还没动任何图标）
        var created = new List<(Fence Fence, IReadOnlyList<string> Files)>();
        foreach (var p in proposal.Fences)
        {
            var fence = new Fence
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                MonitorKey = primary.Key,
                Name = p.Name,
                Color = p.Color,
                Rect = p.Rect.Clone(),
                Rules = p.Rules.ToList(),
                SortMode = FenceSortMode.Name,
            };
            _config.Fences.Add(fence);
            created.Add((fence, p.Files));
        }

        RebuildLayers();

        // 快照必须在真正动图标之前落盘。失败即回滚刚建的分区，绝不执行无法撤销的操作
        var result = _organizer.Organize(_config, _assignments, _space, "首次整理");
        if (!result.Succeeded)
        {
            foreach (var (fence, _) in created) _config.Fences.RemoveAll(f => f.Id == fence.Id);
            RebuildLayers();
            Console.WriteLine("[Fence] 快照失败，已回滚新建的分区，未执行任何整理");
            return result;
        }

        // 规则已经把匹配文件收进去了；这里补齐规则没覆盖到的（如扩展名被隐藏的边角情况）
        foreach (var (fence, files) in created)
        {
            var order = _assignments.InFence(fence.Id).Count;
            foreach (var path in files)
            {
                if (_assignments.Find(path)?.FenceId == fence.Id) continue;
                _assignments.Assign(path, fence.Id, order++, manual: false);
            }
        }

        _sync.SyncToExplorer(_config, _assignments, _space);
        Save();
        SyncAndCompose();

        _firstRun.MarkOnboardingShown();
        _firstRun.MarkOrganized();

        return result;
    }

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
            EnsureFencesFitContents();
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

    /// <summary>
    /// 保证每个分区都装得下自己的图标，装不下就撑大。
    ///
    /// <para><b>为什么只能撑大</b>：原生图标由 Explorer 渲染，我们只能改它们的坐标、
    /// 不能裁剪也不能滚动。设计案 §3.1 写的「超出容器高度时容器内滚动」
    /// 在 Plan A 下不可实现 —— 溢出的图标会直接画到框外面去，
    /// 看起来就是「分区坏了」。</para>
    ///
    /// <para>只加大不缩小：用户手动调小过的分区若被自动缩回去会很恼人，
    /// 而变大是为了兜住本来就该在框内的图标。</para>
    /// </summary>
    private void EnsureFencesFitContents()
    {
        var (cx, cy) = _icons.ItemSpacing;
        if (cx <= 0 || cy <= 0) return;

        // **必须用与写坐标时同一套网格**（含真实原点）。
        // 曾经这里用 GridSpec(0,0,...) 估算、SyncToExplorer 用 ReadGrid() 的真实原点 (34,2)，
        // 两边算出的列数不同 → 行数对不上 → 分区撑大了图标仍超框 54 像素。
        var grid = _sync.ReadGrid();
        if (!grid.IsValid) return;

        var changed = false;

        foreach (var fence in _config.Fences)
        {
            if (fence.Collapsed) continue; // 折叠时不摆图标，无所谓装不装得下

            var count = _assignments.InFence(fence.Id).Count;
            if (count == 0) continue;

            var monitor = _space.MonitorByKey(fence.MonitorKey);
            if (monitor == null) continue;

            var iconRect = _space.FenceToIconSpace(fence);
            if (iconRect == null) continue;

            var (_, _, workW, workH) = monitor.WorkAreaDip;
            var maxW = Math.Max(cx * 2.0, workW - fence.Rect.X);
            var maxH = Math.Max(cy * 2.0, workH - fence.Rect.Y);

            // 按真实网格算出最后一个槽位，直接看它是否落在框内 ——
            // 比「容量 >= 数量」更准：容量是估算，槽位是实际会写下去的坐标
            var newW = fence.Rect.Width;
            var newH = fence.Rect.Height;

            // 最多扩几轮：每轮按当前尺寸重算，加宽或加高直到装下或撞上工作区边界
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var probe = new IconRect(iconRect.Value.X, iconRect.Value.Y, (int)newW, (int)newH);
                var content = FenceGeometry.ContentAreaOf(probe, _sync.TitleHeight, _sync.Padding);
                var last = FenceGeometry.SlotPosition(content, grid, count - 1);

                // 标签余量：LVM_GETITEMSPACING 报的格高只够单行文件名，
                // 「Visual Studio Code」这类会折成两行、超出格子往下溢。
                // 逻辑上没超框，视觉上压线，末行留一点富余。
                const int labelSlack = 24;

                var overRight = last.X + grid.Cx - content.Right;
                var overBottom = last.Y + grid.Cy + labelSlack - content.Bottom;

                if (overRight <= 0 && overBottom <= 0) break;

                // 优先加高（分区偏窄好看些）；高度已到顶就加宽
                if (overBottom > 0 && newH + grid.Cy <= maxH) newH += grid.Cy;
                else if (newW + grid.Cx <= maxW) newW += grid.Cx;
                else break; // 工作区放不下了，只能就此打住
            }

            // 只增不减：用户手动调小过的分区不该被自动缩回去
            newW = Math.Max(fence.Rect.Width, newW);
            newH = Math.Max(fence.Rect.Height, newH);
            if (Math.Abs(newW - fence.Rect.Width) < 0.5 && Math.Abs(newH - fence.Rect.Height) < 0.5) continue;

            Console.WriteLine($"[Fence] 「{fence.Name}」装不下 {count} 个图标，" +
                              $"尺寸 {fence.Rect.Width:F0}x{fence.Rect.Height:F0} → {newW:F0}x{newH:F0}");

            fence.Rect.Width = newW;
            fence.Rect.Height = newH;
            changed = true;
        }

        if (changed)
        {
            foreach (var layer in _layers) layer.RefreshGeometry();
            Save();
        }
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
        _sync.ResetGridCache(); // 分辨率/缩放变化会改变图标间距与格点相位
        RebuildLayers();
        SyncAndCompose();
    }

    /// <summary>
    /// 取指定显示器上的分区矩形（DIP，相对该屏工作区），供组件避让使用。
    /// 折叠的分区只占标题栏高度，组件可以贴在它下面。
    /// </summary>
    public IReadOnlyList<Core.Layout.LayoutBox> FenceBoxesOn(string monitorKey)
    {
        var result = new List<Core.Layout.LayoutBox>();

        foreach (var fence in _config.Fences)
        {
            if (!string.Equals(fence.MonitorKey, monitorKey, StringComparison.OrdinalIgnoreCase)) continue;

            var height = fence.Collapsed ? FenceVisual.TitleHeight : fence.Rect.Height;
            result.Add(new Core.Layout.LayoutBox(fence.Rect.X, fence.Rect.Y, fence.Rect.Width, height));
        }

        return result;
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
