# zDesktop

Windows 桌面增强层 —— 在原生桌面之上叠加分区、组件与自动化规则，而不替换原生桌面的任何部分。

> **状态：早期开发中，尚不适合日常使用。** 当前代码是 v2.3 设计案的实现产物；产品方向已在 v3.0 重新收敛，代码正在按新方向重构。详见下方「当前状态」。

## 这是什么

zDesktop 面向桌面文件常年 50+ 的 Windows 重度用户，目标是让桌面从「混乱的堆叠」变成「规则维持的工作台」。三条主线：

- **桌面分区** —— 在原生图标之上做分组容器，容器可自定义名称/颜色/折叠，图标按规则自动归入
- **桌面组件** —— 时钟、日历、待办、天气、系统监控，自由拖拽 + 网格吸附
- **自动化规则** —— Windows 版 Hazel：监控文件夹，按扩展名/大小/时间等条件自动移动、重命名、归档

配套一个 `Alt+Space` 轻量启动器（应用/文件搜索、算式计算，可选接入 Everything）。

### 设计第一原则

**任何时刻杀掉 zDesktop，桌面必须与从未安装过完全一致。**

具体约束：永不隐藏或替换 `SHELLDLL_DefView`（桌面图标始终由 Explorer 渲染，回收站、多选、F2 重命名、互拖等原生能力全部保留）；所有对系统状态的修改可逆且有还原记录；全屏应用期间零存在感。

## 当前状态

| 模块 | 状态 |
|---|---|
| 桌面组件（5 个） | 可用 |
| 自动化规则引擎 | 可用（`FileSystemWatcher` + 规则匹配） |
| 覆盖层 / Z 序控制 | 可用；Z 序自愈、多屏覆盖层、PerMonitorV2、全屏让位、Explorer 重启自愈均已完成 |
| 桌面分区 | **未实现**（v3.0 主线，技术方案待 spike 验证） |
| 壁纸 | 仅静态壁纸 + 必应每日，无动态壁纸引擎 |
| 任务栏增强 / 图标着色 / 多窗格文件管理 | 已从范围内移除，见设计案 v3.0 §3.3 |

「zDesktop 图标」（托盘菜单，默认关闭）是会隐藏原生图标层的实验特性，开启后回收站/此电脑/副屏图标将不可见，且缺少框选、F2 改名等原生能力——不建议开启。该特性将随分区功能落地一并移除。

## 测试

分三层执行（设计案 v3.1 §十）：

```bash
# T1 — 纯逻辑，每次提交由 CI 跑
dotnet test tests/zDesktop.Tests/zDesktop.Tests.csproj

# T2 — 真机脚本，每次发版前跑（需真实桌面，会重启 explorer.exe）
powershell -ExecutionPolicy Bypass -File tests/T2-RealMachine.ps1
```

T3 为人工验收清单，见设计案 §十。

**还原入口**：`zDesktop.App.exe --restore` 只还原被修改过的系统状态然后退出，供卸载程序调用。

## 构建

需要 .NET 8 SDK（Windows）。

```bash
dotnet build zDesktop.sln -c Release
```

输出在 `bin/Release/`，入口 `zDesktop.App.exe`。

## 目录结构

```
src/zDesktop.App/       WPF 应用入口、主窗口、功能页
src/zDesktop.Shell/     桌面覆盖层、Win32 互操作、各功能服务
src/zDesktop.Core/      数据模型
src/zDesktop.Widgets/   桌面组件实现
pages/                  HTML 设计稿
```

## 文档

- **[zDesktop设计案-v3.md](zDesktop设计案-v3.md)** —— 当前唯一开发依据
- [zDesktop设计案.md](zDesktop设计案.md) —— v2.3，历史存档，已作废

## License

MIT，见 [LICENSE](LICENSE)。
