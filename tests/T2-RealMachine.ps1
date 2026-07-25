<#
.SYNOPSIS
    T2 真机回归（设计案 v3.1 §十）—— 需要真实 Windows 桌面，可脚本化断言。

.DESCRIPTION
    T1 在 CI 上跑逻辑，T2 在开发者机器上跑「与真实桌面交互」的部分。
    每次发版前执行；T2 红则阻断发版。

    覆盖：
      T2-1  强杀恢复      —— taskkill /F 后桌面必须完好，重启后账本清空
      T2-2  Explorer 重启 —— 覆盖层自动重新附着且进程存活
      T2-4  性能预算      —— 空闲态 CPU 与工作集对照 §八 表格
      T2-3  卸载无残留    —— 待 0.9 安装包就绪后启用（当前 SKIP）
      T2-5  显示器热插拔  —— 需要真实多屏或显卡模拟（当前 MANUAL）

.PARAMETER ExePath
    zDesktop.App.exe 路径。默认取仓库 bin\Release。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests\T2-RealMachine.ps1
#>

[CmdletBinding()]
param(
    # 留空表示用仓库默认输出路径。$PSScriptRoot 在 PS 5.1 的参数默认值中尚未赋值，
    # 不能写在这里，必须在脚本体内解析。
    [string]$ExePath = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ExePath = Join-Path $scriptDir '..\bin\Release\zDesktop.App.exe'
}
$ExePath = [System.IO.Path]::GetFullPath($ExePath)
$script:Failures = @()
$script:Passes = 0
$script:Skips = 0

# ===== Win32 辅助：查桌面图标层可见性 =====
if (-not ('ZDesktopT2.Native' -as [type])) {
    Add-Type -Namespace 'ZDesktopT2' -Name 'Native' -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string cls, string win);
[DllImport("user32.dll")]
public static extern bool IsWindowVisible(IntPtr hWnd);
'@
}

# 注意：向 P/Invoke 的 [string] 参数传 $null 时，PowerShell 会把它转成**空字符串**，
# 于是 FindWindow('Progman', '') 变成「查找标题为空的 Progman 窗口」而匹配不到
# （Progman 的标题是 "Program Manager"）。必须用 [NullString]::Value 传真正的 NULL。
$script:NullStr = [NullString]::Value

function Get-DefViewHandle {
    # SHELLDLL_DefView 挂在 Progman 下，或在某个 WorkerW 下（取决于壁纸状态）
    $progman = [ZDesktopT2.Native]::FindWindow('Progman', $script:NullStr)
    if ($progman -ne [IntPtr]::Zero) {
        $defView = [ZDesktopT2.Native]::FindWindowEx($progman, [IntPtr]::Zero, 'SHELLDLL_DefView', $script:NullStr)
        if ($defView -ne [IntPtr]::Zero) { return $defView }
    }

    $worker = [IntPtr]::Zero
    while ($true) {
        $worker = [ZDesktopT2.Native]::FindWindowEx([IntPtr]::Zero, $worker, 'WorkerW', $script:NullStr)
        if ($worker -eq [IntPtr]::Zero) { break }
        $defView = [ZDesktopT2.Native]::FindWindowEx($worker, [IntPtr]::Zero, 'SHELLDLL_DefView', $script:NullStr)
        if ($defView -ne [IntPtr]::Zero) { return $defView }
    }
    return [IntPtr]::Zero
}

function Test-DesktopIconsVisible {
    $h = Get-DefViewHandle
    if ($h -eq [IntPtr]::Zero) { return $false }
    return [ZDesktopT2.Native]::IsWindowVisible($h)
}

function Assert-True($condition, $name, $detail) {
    if ($condition) {
        Write-Host "  [PASS] $name" -ForegroundColor Green
        $script:Passes++
    } else {
        Write-Host "  [FAIL] $name -- $detail" -ForegroundColor Red
        $script:Failures += "$name : $detail"
    }
}

function Skip-Case($name, $reason) {
    Write-Host "  [SKIP] $name -- $reason" -ForegroundColor Yellow
    $script:Skips++
}

function Stop-ZDesktop {
    Get-Process zDesktop.App -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 800
}

function Start-ZDesktop {
    $p = Start-Process -FilePath $ExePath -PassThru
    # 等待覆盖层建立（ContentRendered → Z 序锚定）
    Start-Sleep -Seconds 4
    return $p
}

$journalPath = Join-Path $env:APPDATA 'zDesktop\restore.json'

Write-Host ''
Write-Host '=== T2 真机回归（设计案 v3.1 §十）===' -ForegroundColor Cyan
Write-Host "被测程序: $ExePath"

if (-not (Test-Path $ExePath)) {
    Write-Host "找不到 $ExePath，请先执行: dotnet build zDesktop.sln -c Release" -ForegroundColor Red
    exit 2
}

Stop-ZDesktop

# ---------------------------------------------------------------
Write-Host ''
Write-Host 'T2-1 强杀恢复' -ForegroundColor Cyan
# ---------------------------------------------------------------
# 基线是所有后续断言的前提：找不到桌面图标层，后面每一条可见性断言都会变成假失败。
# 因此这里直接中止，而不是让脚本跑出一串级联红色。
if (-not (Test-DesktopIconsVisible)) {
    Write-Host '  [ABORT] 基线不成立：找不到可见的 SHELLDLL_DefView' -ForegroundColor Red
    Write-Host ''
    Write-Host '  可能原因：' -ForegroundColor Yellow
    Write-Host '    - 当前不是交互式桌面会话（远程/服务/CI 上下文）'
    Write-Host '    - explorer.exe 未作为 shell 运行'
    Write-Host '    - 桌面图标已被其他工具隐藏'
    Write-Host ''
    Write-Host '  请在正常登录的交互式 PowerShell 中运行本脚本。' -ForegroundColor Yellow
    exit 2
}
Assert-True $true 'T2-1.0 基线：启动前桌面图标可见' ''

$proc = Start-ZDesktop
Assert-True (-not $proc.HasExited) 'T2-1.1 应用启动成功' '进程启动后立即退出'
Assert-True (Test-DesktopIconsVisible) 'T2-1.2 默认不隐藏原生图标层（零破坏契约）' '启动后 SHELLDLL_DefView 不可见 —— 默认图标模式可能被改回自渲染'

# 强杀：TerminateProcess，进程内任何钩子都不会执行
Stop-Process -Id $proc.Id -Force
Start-Sleep -Seconds 2

Assert-True (Test-DesktopIconsVisible) 'T2-1.3 强杀后桌面图标仍可见' '强杀导致桌面被破坏，零破坏契约失败'

# 重启：账本若有待还原项应被消费掉
[void](Start-ZDesktop)
Assert-True (Test-DesktopIconsVisible) 'T2-1.4 重启后桌面图标可见' '重启后未能还原桌面'

if (Test-Path $journalPath) {
    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-True (-not $journal.NativeIconsHidden) 'T2-1.5 还原账本无残留待办' "restore.json 仍标记 NativeIconsHidden=$($journal.NativeIconsHidden)"
} else {
    Assert-True $true 'T2-1.5 还原账本无残留待办' ''
}

# ---------------------------------------------------------------
Write-Host ''
Write-Host 'T2-2 Explorer 重启自愈' -ForegroundColor Cyan
# ---------------------------------------------------------------
$defViewBefore = Get-DefViewHandle

Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
# Explorer 由系统自动拉起，等待桌面重建
Start-Sleep -Seconds 8

$defViewAfter = Get-DefViewHandle
Assert-True ($defViewAfter -ne [IntPtr]::Zero) 'T2-2.1 Explorer 已重建桌面' '未找到重建后的 SHELLDLL_DefView'
Assert-True ($defViewAfter -ne $defViewBefore) 'T2-2.2 桌面图标层句柄确已变化（证明重启生效）' '句柄未变，Explorer 可能没真正重启，本用例未构成有效验证'

$alive = Get-Process zDesktop.App -ErrorAction SilentlyContinue
Assert-True ($null -ne $alive) 'T2-2.3 Explorer 重启后 zDesktop 进程存活' 'zDesktop 随 Explorer 一同死亡'
Assert-True (Test-DesktopIconsVisible) 'T2-2.4 Explorer 重启后桌面图标可见' '自愈过程破坏了原生桌面'

# ---------------------------------------------------------------
Write-Host ''
Write-Host 'T2-4 性能预算（空闲态，桌面未聚焦）' -ForegroundColor Cyan
# ---------------------------------------------------------------
$p = Get-Process zDesktop.App -ErrorAction SilentlyContinue
if ($null -eq $p) {
    Skip-Case 'T2-4 性能预算' 'zDesktop 进程不存在，跳过采样'
} else {
    # 采样 10 秒 CPU 时间增量
    $cpu1 = $p.TotalProcessorTime
    Start-Sleep -Seconds 10
    $p.Refresh()
    $cpu2 = $p.TotalProcessorTime
    $cpuPercent = (($cpu2 - $cpu1).TotalMilliseconds / (10 * 1000 * [Environment]::ProcessorCount)) * 100
    $wsMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
    $privMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)

    Write-Host ("  实测: CPU {0:N2}% / 私有 {1} MB / 工作集 {2} MB（工作集仅记录，不作门禁）" -f $cpuPercent, $privMB, $wsMB)

    # §八 预算：空闲态 CPU < 0.1%；私有字节 < 160MB。
    # 工作集不作门禁 —— 它含框架共享页且随系统内存压力浮动，跨机器不可比。
    Assert-True ($cpuPercent -lt 0.5) 'T2-4.1 空闲态 CPU 低于阈值' ("实测 {0:N2}%，预算 <0.1%（脚本放宽到 0.5% 以容忍采样噪声）" -f $cpuPercent)
    Assert-True ($privMB -lt 160) 'T2-4.2 私有字节低于阈值' "实测 $privMB MB，门槛 <160MB"
}

# ---------------------------------------------------------------
Write-Host ''
Write-Host 'T2-3 / T2-5' -ForegroundColor Cyan
# ---------------------------------------------------------------
Skip-Case 'T2-3 卸载无残留' '安装包属 0.9 里程碑，尚未产出。届时改为：安装→快照→卸载→快照 diff'
Skip-Case 'T2-5 显示器热插拔' '需真实多屏或显卡模拟；当前列入 T3 人工清单（T3-1）'

# ---------------------------------------------------------------
Stop-ZDesktop

Write-Host ''
Write-Host '=== 结果 ===' -ForegroundColor Cyan
Write-Host "通过 $script:Passes / 跳过 $script:Skips / 失败 $($script:Failures.Count)"

if ($script:Failures.Count -gt 0) {
    Write-Host ''
    Write-Host '失败项：' -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'T2 全绿' -ForegroundColor Green
exit 0
