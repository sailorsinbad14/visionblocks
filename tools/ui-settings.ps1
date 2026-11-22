# 设置窗口测试：只用 UIA 控件模式和给系统确认框发 BM_CLICK，只操作我自己启动的编辑器（StudioPid）。
# 会弹系统确认框的那一下（打开随机化）放到后台作业里做，因为 Toggle 要等确认框关掉才返回。
param([int]$StudioPid, [string]$Step = "all", [string]$Theme = "dark")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ("B2.Native" -as [type])) { Add-Type -Namespace B2 -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);' }
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
function C($p, $v) { New-Object System.Windows.Automation.PropertyCondition($p, $v) }
function Main { $AE::FromHandle((Get-Process -Id $StudioPid).MainWindowHandle) }
function ById($root, $id) { $root.FindFirst($TS::Descendants, (C $AE::AutomationIdProperty $id)) }
function Has($root, $ids) { foreach ($id in $ids) { "{0}={1}" -f $id, [bool](ById $root $id) } }
# 设置窗口、确认框都由主窗口拥有，在 UIA 树里挂在主窗口下面（也可能是顶层），两处都找
function TopOfPid([string]$cls) {
    $cands = @((Main).FindAll($TS::Descendants, (C $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window))))
    $cands | Where-Object { $_ -and (($cls -ne "" -and $_.Current.ClassName -eq $cls) -or ($cls -eq "" -and $_.Current.ClassName -ne "#32770" -and $_.FindFirst($TS::Children, (C $AE::AutomationIdProperty "settingsClose")))) } | Select-Object -First 1
}
function Settings { $d = TopOfPid ""; if ($d) { return $d }; (ById (Main) "btnSettings").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 1200; TopOfPid "" }
function State($e) { $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
function Answer([string]$btn) { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "answer-box.ps1") -StudioPid $StudioPid -Button ([int]$btn) }
function ToggleInBackground([string]$id) {
    Start-Job -ArgumentList $StudioPid, $id -ScriptBlock {
        param($p, $id)
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
        $w = $AE::FromHandle((Get-Process -Id $p).MainWindowHandle)
        $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
}

switch ($Step) {
    "main" {
        "-- 主界面（没有样张模式和「全部窗口」；练习靶子默认不显示）"
        Has (Main) @("monitor", "bindWindow", "btnRefresh", "btnFreeze", "btnRun", "btnStop", "btnSettings", "btnOpenResults", "showcase", "allWindows", "scene", "btnLaunch")
        "btnOpenResults enabled=" + (ById (Main) "btnOpenResults").Current.IsEnabled
    }
    "all" {
        $d = Settings
        "-- 设置窗口：" + $d.Current.Name
        Has $d @("langZh", "langEn", "themeSystem", "themeLight", "themeDark", "showPracticeTarget", "humanize", "humanizeTiming", "humanizePath", "humanizeOvershoot", "settingsClose")
        "humanizeTiming enabled（总开关关着时应为 False）=" + (ById $d "humanizeTiming").Current.IsEnabled
        "-- 打开显示练习靶子（上一轮已经打开了就不再拨）"
        if ((State (ById $d "showPracticeTarget")) -ne "On") { try { (ById $d "showPracticeTarget").GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch { "  （UIA 客户端报错，按状态判断：$($_.Exception.Message)）" } }
        Start-Sleep -Milliseconds 500
        "showPracticeTarget=" + (State (ById $d "showPracticeTarget"))
        Has (Main) @("scene", "btnLaunch")
        "-- 打开随机化，确认框点「否」"
        $job = ToggleInBackground "humanize"
        Answer "7"
        Wait-Job $job -Timeout 10 | Out-Null; Remove-Job $job -Force
        Start-Sleep -Milliseconds 400
        "humanize=" + (State (ById $d "humanize")) + "（应为 Off）"
        "-- 再打开一次，点「是」"
        $job = ToggleInBackground "humanize"
        Answer "6"
        Wait-Job $job -Timeout 10 | Out-Null; Remove-Job $job -Force
        Start-Sleep -Milliseconds 400
        "humanize=" + (State (ById $d "humanize")) + "（应为 On）；humanizeTiming enabled=" + (ById $d "humanizeTiming").Current.IsEnabled
        "-- 主题：" + $Theme
        $tid = @{ dark = "themeDark"; light = "themeLight"; system = "themeSystem" }[$Theme]
        try { (ById $d $tid).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { "  （UIA 客户端报错，按状态判断：$($_.Exception.Message)）" }
        Start-Sleep -Milliseconds 600
        "$tid 选中=" + (ById $d $tid).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
        (ById $d "settingsClose").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 600
        "设置窗口已关：" + (-not (TopOfPid ""))
    }
    "theme" {
        $d = Settings
        $tid = @{ dark = "themeDark"; light = "themeLight"; system = "themeSystem" }[$Theme]
        (ById $d $tid).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 600
        (ById $d "settingsClose").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        "主题已切到 $Theme"
    }
    "humanizeOff" {
        $d = Settings
        if ((State (ById $d "humanize")) -eq "On") { (ById $d "humanize").GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
        "humanize=" + (State (ById $d "humanize"))
        (ById $d "settingsClose").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
}
