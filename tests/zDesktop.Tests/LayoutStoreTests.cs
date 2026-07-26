using System.IO;
using Xunit;
using zDesktop.Core.Layout;
using zDesktop.Shell.Layout;

namespace zDesktop.Tests;

/// <summary>
/// T1-5（设计案 v3.1 §十）：配置读写 —— 版本迁移、损坏文件降级、往返一致。
/// </summary>
public class LayoutStoreTests : IDisposable
{
    private readonly string _dir;

    public LayoutStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zdesktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响测试结论 */ }
    }

    private string LayoutPath => Path.Combine(_dir, "layout.json");

    // ===== 往返一致 =====

    [Fact]
    public void 保存后加载应当还原全部字段()
    {
        var store = new LayoutStore(_dir);
        var config = new LayoutConfig();
        config.Widgets.Add(new WidgetLayoutEntry
        {
            WidgetId = "clock",
            MonitorKey = @"\\.\DISPLAY2",
            X = 120, Y = 340, Width = 280, Height = 150,
            IsVisible = true,
            Config = new Dictionary<string, object?> { ["format"] = "24h" },
        });

        store.Save(config);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        var entry = Assert.Single(loaded!.Widgets);
        Assert.Equal("clock", entry.WidgetId);
        Assert.Equal(@"\\.\DISPLAY2", entry.MonitorKey);
        Assert.Equal(120, entry.X);
        Assert.Equal(340, entry.Y);
        Assert.Equal(280, entry.Width);
        Assert.Equal(150, entry.Height);
        Assert.True(entry.IsVisible);
    }

    [Fact]
    public void 折叠状态应当往返保持()
    {
        // 真机回归：Collapsed 字段加进了模型，却漏了 App.RestoreLayout 里的映射，
        // 结果折叠的组件重启后全部变回展开。
        var store = new LayoutStore(_dir);
        var config = new LayoutConfig();
        config.Widgets.Add(new WidgetLayoutEntry
        {
            WidgetId = "clock", X = 100, Y = 100, Width = 280, Height = 150, Collapsed = true,
        });

        store.Save(config);

        var entry = Assert.Single(store.Load()!.Widgets);
        Assert.True(entry.Collapsed);
        // 折叠中也必须记展开高度，否则重启后是一条 36px 的空壳、再也展不开
        Assert.Equal(150, entry.Height);
    }

    [Fact]
    public void 文件不存在时应当返回空而不是抛异常()
    {
        var store = new LayoutStore(_dir);
        Assert.Null(store.Load());
    }

    [Fact]
    public void 文件损坏时应当降级为空而不是抛异常()
    {
        File.WriteAllText(LayoutPath, "{ 这不是合法 JSON ][");

        var store = new LayoutStore(_dir);

        // 损坏配置必须降级为「无布局」，由 App 走默认布局，不能让启动崩掉
        Assert.Null(store.Load());
    }

    // ===== 版本迁移 =====

    [Fact]
    public void v1迁移到v2应当重置被错误隐藏的组件()
    {
        // v1 的 IsVisible=false 是「托盘临时隐藏污染持久化」这个 bug 的产物，不可信
        var config = new LayoutConfig { Version = 1 };
        config.Widgets.Add(new WidgetLayoutEntry { WidgetId = "clock", IsVisible = false });

        var migrated = LayoutStore.Migrate(config);

        // v1 会连带走到 v2→v3 的废弃分支
        Assert.Null(migrated);
    }

    [Fact]
    public void v2迁移应当废弃旧布局()
    {
        // v2→v3 是统一宽度改造，旧布局宽度不一致，设计上直接废弃重载默认布局
        var config = new LayoutConfig { Version = 2 };
        config.Widgets.Add(new WidgetLayoutEntry { WidgetId = "clock", Width = 311 });

        Assert.Null(LayoutStore.Migrate(config));
    }

    [Fact]
    public void v3迁移到v4应当保留布局并归属主屏()
    {
        // v3 只有主屏一个覆盖层，坐标本就相对主屏工作区，语义兼容 —— 不得废弃
        var config = new LayoutConfig { Version = 3 };
        config.Widgets.Add(new WidgetLayoutEntry { WidgetId = "clock", X = 80, Y = 80 });
        config.Widgets.Add(new WidgetLayoutEntry { WidgetId = "todo", X = 400, Y = 80 });

        var migrated = LayoutStore.Migrate(config);

        Assert.NotNull(migrated);
        Assert.Equal(4, migrated!.Version);
        Assert.Equal(2, migrated.Widgets.Count);
        // MonitorKey 留空即代表主屏
        Assert.All(migrated.Widgets, w => Assert.Equal(string.Empty, w.MonitorKey));
        // 坐标不得被改动
        Assert.Equal(80, migrated.Widgets[0].X);
        Assert.Equal(400, migrated.Widgets[1].X);
    }

    [Fact]
    public void v4配置应当原样通过迁移()
    {
        var config = new LayoutConfig { Version = 4 };
        config.Widgets.Add(new WidgetLayoutEntry { WidgetId = "clock", MonitorKey = @"\\.\DISPLAY1", X = 10 });

        var migrated = LayoutStore.Migrate(config);

        Assert.NotNull(migrated);
        Assert.Equal(4, migrated!.Version);
        Assert.Equal(@"\\.\DISPLAY1", migrated.Widgets[0].MonitorKey);
    }

    [Fact]
    public void 磁盘上的v3配置应当被加载而非丢弃()
    {
        // 端到端：模拟老用户的 layout.json 落在磁盘上，升级后布局必须还在
        var legacy = """
        {
          "version": 3,
          "widgets": [
            { "widgetId": "clock", "x": 80, "y": 80, "width": 280, "height": 150, "isVisible": true }
          ]
        }
        """;
        File.WriteAllText(LayoutPath, legacy);

        var loaded = new LayoutStore(_dir).Load();

        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Version);
        var entry = Assert.Single(loaded.Widgets);
        Assert.Equal("clock", entry.WidgetId);
        Assert.Equal(80, entry.X);
        Assert.Equal(string.Empty, entry.MonitorKey);
    }

    [Fact]
    public void 新建配置的版本号应当是当前版本()
    {
        Assert.Equal(LayoutConfig.CurrentVersion, new LayoutConfig().Version);
        Assert.Equal(4, LayoutConfig.CurrentVersion);
    }
}
