# T3-3 首次运行全流程验收：引导 → 预览 → 应用 → 撤销（设计案 v3.1 §十）
#
# 用 UI Automation 点按钮，而不是 SendInput：部分机器上安全软件会拦截合成输入
# （实测注入的相对移动完全不生效，SetCursorPos 却有效，症状是「点了没反应」，
# 极易被误判成产品缺陷）。UIA 的 InvokePattern 走的是真实 WPF 命令路径，
# 验的仍是产品自己的按钮回调，且不受该拦截影响。
#
# 每步用「日志 + 状态文件」核对，不靠截图目测。
#
# 注意：要验「首次运行」就必须先把本机装成全新状态。脚本会先把现有的
# fences.json / first-run.json / snapshots 挪到备份目录，跑完原样搬回来。
[CmdletBinding()]
param(
    [string]$ExePath = ''
)

$ErrorActionPreference = 'Continue'

# PS 5.1 里 param 默认值求值时 $PSScriptRoot 还是空的，只能在函数体里补
if (-not $ExePath) {
    $ExePath = Join-Path (Split-Path $PSCommandPath -Parent | Split-Path -Parent) 'bin\Release\zDesktop.App.exe'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$appData = Join-Path $env:APPDATA 'zDesktop'
$log = "$env:TEMP\zd-flow.txt"
$fencesPath = Join-Path $appData 'fences.json'
$pass = 0
$fail = @()

function Check($ok, $name, $detail = '') {
    if ($ok) { Write-Host "  [PASS] $name" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "  [FAIL] $name -- $detail" -ForegroundColor Red; $script:fail += "$name : $detail" }
}
function Section($s) { Write-Host ''; Write-Host $s -ForegroundColor Cyan }
function LogText { if (Test-Path $log) { Get-Content $log -Raw } else { '' } }

$AE = [System.Windows.Automation.AutomationElement]

# 找到当前的提示卡片（本进程唯一 380 宽的顶层窗口）
function Get-Card($procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
    $wins = $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $wins) {
        if ([int]$w.Current.BoundingRectangle.Width -eq 380) { return $w }
    }
    return $null
}

# 等某张卡片弹出（以日志里的标题为准），返回它的 UIA 元素
function Wait-Card($procId, $title, $timeoutSec = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((LogText) -match [regex]::Escape("[Toast] 弹出「$title")) {
            $c = Get-Card $procId
            if ($null -ne $c) { return $c }
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Invoke-CardButton($card, $name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        $AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $btns = $card.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $target = $btns | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if (-not $target) {
        Write-Host "  按钮「$name」不存在，卡片上有：$(($btns | ForEach-Object { $_.Current.Name }) -join '、')" -ForegroundColor Red
        return $false
    }
    $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "  已点「$name」"
    return $true
}

function Read-Fences($path) {
    if (-not (Test-Path $path)) { return [pscustomobject]@{ fences = @(); assignments = @() } }
    $j = Get-Content $path -Raw | ConvertFrom-Json
    if ($null -eq $j.fences) { $j | Add-Member fences @() -Force }
    if ($null -eq $j.assignments) { $j | Add-Member assignments @() -Force }
    return $j
}

$stateItems = @('fences.json', 'first-run.json', 'snapshots')
$backup = Join-Path $env:TEMP ("zd-t3-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))

# 把现有状态挪走，跑完搬回来 —— 验收不该拿用户已有的分区当代价
function Save-State {
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach ($n in $stateItems) {
        $p = Join-Path $appData $n
        if (Test-Path -LiteralPath $p) { Move-Item -LiteralPath $p -Destination $backup -Force }
    }
}
function Restore-State {
    Get-Process zDesktop.App -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    # 强杀跳过了正常退出的收尾，壁纸等系统状态得靠还原账本自己走一遍
    Start-Process -FilePath $ExePath -ArgumentList '--restore' -Wait -NoNewWindow -ErrorAction SilentlyContinue

    foreach ($n in $stateItems) {
        $p = Join-Path $appData $n
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
        $b = Join-Path $backup $n
        if (Test-Path -LiteralPath $b) { Move-Item -LiteralPath $b -Destination $appData -Force }
    }
    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "已还原验收前的分区状态" -ForegroundColor DarkGray
}

Write-Host '=== T3-3 首次运行全流程验收 ===' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Host "找不到 $ExePath，先 dotnet build -c Release" -ForegroundColor Red
    exit 2
}

# --- 准备：全新安装状态 ---
Get-Process zDesktop.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Save-State

Remove-Item $log -ErrorAction SilentlyContinue
$proc = Start-Process -FilePath $ExePath -PassThru `
    -RedirectStandardOutput $log -RedirectStandardError "$env:TEMP\zd-flow-err.txt"

Section '1. 引导卡片'
$card = Wait-Card $proc.Id 'zDesktop 已在后台运行' 30
Check ($null -ne $card) '引导卡片已出现'
if ($null -eq $card) {
    LogText
    Restore-State
    exit 1
}
Check ((Test-Path $fencesPath) -eq $false) '引导态零改动（还没有 fences.json）'

Section '2. 点「预览效果」'
[void](Invoke-CardButton $card '预览效果')
$preview = Wait-Card $proc.Id '整理预览' 20
Check ((LogText) -match '\[Toast\] 点击「预览效果」') '预览按钮已响应'
Check ($null -ne $preview) '预览确认卡片已出现'
Check ((Read-Fences $fencesPath).assignments.Count -eq 0) '预览态未产生归属（零改动）'
$snapPre = (Get-ChildItem (Join-Path $appData 'snapshots') -Filter *.json -ErrorAction SilentlyContinue).Count
Check ($snapPre -eq 0) '预览态未落快照（确实没动过桌面）' "已有 $snapPre 份"

Section '3. 点「应用」'
if ($null -ne $preview) { [void](Invoke-CardButton $preview '应用') }
Start-Sleep -Seconds 5
Check ((LogText) -match '\[Toast\] 点击「应用」') '应用按钮已响应'
$cfg = Read-Fences $fencesPath
Check ($cfg.fences.Count -gt 0) '应用后已创建分区' "分区数 $($cfg.fences.Count)"
Check ($cfg.assignments.Count -gt 0) '应用后已产生归属' "归属数 $($cfg.assignments.Count)"
$snapCount = (Get-ChildItem (Join-Path $appData 'snapshots') -Filter *.json -ErrorAction SilentlyContinue).Count
Check ($snapCount -ge 1) '已落盘快照（先备份再动手）' "快照数 $snapCount"
Write-Host "  分区 $($cfg.fences.Count) 个，归属 $($cfg.assignments.Count) 条，快照 $snapCount 份"

Section '4. 点「撤销」'
$undo = Wait-Card $proc.Id '已整理' 20
Check ($null -ne $undo) '撤销提示卡片已出现'
if ($null -ne $undo) { [void](Invoke-CardButton $undo '撤销'); Start-Sleep -Seconds 5 }
Check ((LogText) -match '\[Toast\] 点击「撤销」') '撤销按钮已响应'
$cfgAfter = Read-Fences $fencesPath
Check ($cfgAfter.assignments.Count -eq 0) '撤销后归属已清空' "仍有 $($cfgAfter.assignments.Count) 条"
Check ($cfgAfter.fences.Count -eq 0) '撤销后本次新建的分区已移除' "仍留下 $($cfgAfter.fences.Count) 个空分区"

Section '5. 日志核对'
$logText = LogText
Check ($logText -match '\[Snapshot\] 已落盘') '日志记录了快照落盘'
Check ($logText -match '\[Organizer\] 整理完成') '日志记录了整理完成'
Check ($logText -match '\[Organizer\] 已撤销到') '日志记录了撤销'
$restore = [regex]::Match($logText, '已撤销到 \S+：还原 (\d+)/(\d+) 个图标坐标')
Check ($restore.Success -and $restore.Groups[1].Value -eq $restore.Groups[2].Value) `
    '撤销把图标全部还原' $(if ($restore.Success) { "$($restore.Groups[1].Value)/$($restore.Groups[2].Value)" } else { '日志没有还原记录' })

Restore-State

Section '时间线'
$logText -split "`r?`n" | Where-Object { $_ -match '\[Toast\]|\[Organizer\]|\[Snapshot\]' } | ForEach-Object { Write-Host "  $_" }

Section '结果'
Write-Host "通过 $pass / 失败 $($fail.Count)"
if ($fail.Count -gt 0) { $fail | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }; exit 1 }
Write-Host 'T3-3 全流程验收通过' -ForegroundColor Green
exit 0
