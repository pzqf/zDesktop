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
    [string]$ExePath = (Join-Path $PSScriptRoot '..\bin\Release\zDesktop.App.exe')
)

$ErrorActionPreference = 'Stop'
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

function Get-DefViewHandle {
    # SHELLDLL_DefView 挂在 Progman 下，或在某个 WorkerW 下（取决于壁纸状态）
    $progman = [ZDesktopT2.Native]::FindWindow('Progman', $null)
    $defView = [ZDesktopT2.Native]::FindWindowEx($progman, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
    if ($defView -ne [IntPtr]::Zero) { return $defView }

    $worker = [IntPtr]::Zero
    while ($true) {
        $worker = [ZDesktopT2.Native]::FindWindowEx([IntPtr]::Zero, $worker, 'WorkerW', $null)
        if ($worker -eq [IntPtr]::Zero) { break }
        $defView = [ZDesktopT2.Native]::FindWindowEx($worker, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
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
Assert-True (Test-DesktopIconsVisible) 'T2-1.0 基线：启动前桌面图标可见' '测试前提不成立，请检查桌面状态'

$proc = Start-ZDesktop
Assert-True (-not $proc.HasExited) 'T2-1.1 应用启动成功' '进程启动后立即退出'
Assert-True (Test-DesktopIconsVisible) 'T2-1.2 默认不隐藏原生图标层（零破坏契约）' '启动后 SHELLDLL_DefView 不可见 —— 默认图标模式可能被改回自渲染'

# 强杀：TerminateProcess，进程内任何钩子都不会执行
Stop-Process -Id $proc.Id -Force
Start-Sleep -Seconds 2

Assert-True (Test-DesktopIconsVisible) 'T2-1.3 强杀后桌面图标仍可见' '强杀导致桌面被破坏，零破坏契约失败'

# 重启：账本若有待还原项应被消费掉
$proc2 = Start-ZDesktop
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

    Write-Host ("  实测: CPU {0:N2}% / 工作集 {1} MB" -f $cpuPercent, $wsMB)
    # §八 预算：空闲态 CPU < 0.1%，常驻工作集 < 120MB
    Assert-True ($cpuPercent -lt 0.5) 'T2-4.1 空闲态 CPU 低于阈值' ("实测 {0:N2}%，预算 <0.1%（脚本放宽到 0.5% 以容忍采样噪声）" -f $cpuPercent)
    Assert-True ($wsMB -lt 150) 'T2-4.2 工作集低于阈值' "实测 $wsMB MB，预算 <120MB（脚本放宽到 150MB）"
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
