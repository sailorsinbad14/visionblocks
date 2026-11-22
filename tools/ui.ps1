# 用 UI Automation 操作 VisionBlocks 编辑器（2026-11-20）。
# 只用控件模式（Invoke / Toggle / SelectionItem / ExpandCollapse）和给消息框按钮发 BM_CLICK，不发模拟键鼠。
# 注意：Windows PowerShell 5.1 按系统代码页读不带 BOM 的脚本，本文件必须存成 UTF-8 带 BOM。
# 用法：powershell -File ui.ps1 <步骤> [参数]
param([string]$Step, [string]$Arg = "")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ("VbUi.Native" -as [type])) {
    Add-Type -Namespace VbUi -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);'
}
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }

function Studio {
    # 按主窗口句柄找：运行中遮罩窗口也属于编辑器进程，按进程枚举会找错
    # 设了环境变量 VB_STUDIO_PID 就只认这个进程：用户自己的编辑器开着时，测试不能碰到它
    if ($env:VB_STUDIO_PID) { $p = Get-Process -Id ([int]$env:VB_STUDIO_PID) -ErrorAction SilentlyContinue }
    # 2026-11-22：靶子现在由编辑器自己的 exe 启动（--bench-target），同名进程里要跳过窗口标题是 BenchTarget 的那个
    else { $p = Get-Process VisionBlocksStudio -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -ne "BenchTarget" } | Select-Object -First 1 }
    if (-not $p) { throw "编辑器没在运行" }
    return $AE::FromHandle($p.MainWindowHandle)
}
function ById($parent, $id) {
    $e = $parent.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id))
    if (-not $e) { throw "找不到控件 $id" }
    return $e
}
function Click($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function TextOf($e) {
    $t = $e.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Text)) | ForEach-Object { $_.Current.Name }
    return ($t -join " ")
}
function SelectIn($combo, $text) {
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 300
    $items = $combo.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::ListItem))
    $hit = $items | Where-Object { $_.Current.Name -like $text } | Select-Object -First 1
    if (-not $hit) { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); throw "下拉框里没有 $text" }
    $hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
    return $hit.Current.Name
}
function ComboItems($combo) {
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 300
    $names = $combo.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::ListItem)) | ForEach-Object { $_.Current.Name }
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    return $names
}
function TreeItems($w) {
    $tree = $w.FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Tree))
    return $tree.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::TreeItem))
}
function LogLines($w, $n) {
    $log = ById $w "log"
    $all = $log.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $x = TextOf $_; if ($x) { $x } else { $_.Current.Name } }
    return $all | Select-Object -Last $n
}

$w = Studio
switch ($Step) {
    "title" { $w.Current.Name }
    "click" { Click (ById $w $Arg); "点了 $Arg" }
    "toggle" { (ById $w $Arg).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); "切换了 $Arg" }
    "scene" { SelectIn (ById $w "scene") $Arg }
    "windows" { ComboItems (ById $w "bindWindow") }
    "bind" { SelectIn (ById $w "bindWindow") $Arg }
    "log" { LogLines $w ([int]$(if ($Arg) { $Arg } else { 12 })) }
    "palette" {
        # 点积木面板里名字等于 $Arg 的按钮（按钮的内容是文字）
        $btn = $w.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Button)) | Where-Object { $_.Current.AutomationId -eq "pal_$Arg" } | Select-Object -First 1
        if (-not $btn) { throw "积木面板里没有 $Arg" }
        Click $btn; "插入了 $Arg"
    }
    "treecount" { @(TreeItems $w).Count }
    "selectblock" {
        $it = TreeItems $w | Where-Object { (TextOf $_) -like $Arg } | Select-Object -Last 1
        if (-not $it) { throw "程序树里没有 $Arg" }
        $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        "选中：" + (TextOf $it).Split("`n")[0]
    }
    "selectres" {
        $lb = ById $w "resources"
        $it = $lb.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::ListItem)) | Where-Object { $_.Current.Name -like $Arg -or (TextOf $_) -like $Arg } | Select-Object -First 1
        if (-not $it) { throw "资源列表里没有 $Arg" }
        $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        "选中资源：" + $(if ($it.Current.Name) { $it.Current.Name } else { TextOf $it })
    }
    "has" { foreach ($id in $Arg.Split(",")) { "{0}: {1}" -f $id, [bool]$w.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)) } }
    "text" { (ById $w $Arg).Current.Name }
    "find" { $w.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Text)) | Where-Object { $_.Current.Name -like $Arg } | ForEach-Object { $_.Current.Name } }
    "dialog" {
        # 消息框（#32770）：读文字；$Arg = 6（是/Yes）或 7（否/No）时发 BM_CLICK
        $d = $w.FindFirst($TS::Descendants, (Cond $AE::ClassNameProperty "#32770"))
        if (-not $d) { throw "没有对话框" }
        $msg = $d.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty "65535"))
        "对话框「$($d.Current.Name)」：$($msg.Current.Name)"
        if ($Arg) {
            $b = $d.FindFirst($TS::Children, (Cond $AE::AutomationIdProperty $Arg))
            [void][VbUi.Native]::SendMessage([IntPtr]$b.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
            "点了「$($b.Current.Name)」"
        }
    }
    "focusblock" {
        # 只给程序树里的积木设键盘焦点、不改选中，模拟「点回原本就高亮的那块」（2026-11-20 选中不同步的问题）
        $it = TreeItems $w | Where-Object { (TextOf $_) -like $Arg } | Select-Object -Last 1
        if (-not $it) { throw "程序树里没有 $Arg" }
        $it.SetFocus()
        "聚焦：" + (TextOf $it).Split("`n")[0]
    }
    "open" {
        # 「打开…」的文件对话框：文件名框（id 1148）填路径，再给「打开」（id 1）发 BM_CLICK
        # 2026-11-20：用 UI 自动化点「打开…」时编辑器崩过两次（0xc0000409），所以必须用 VB_STUDIO_PID 指定测试用的编辑器，不能碰用户自己开着的那个
        if (-not $env:VB_STUDIO_PID) { throw "open 会让编辑器崩溃：先设环境变量 VB_STUDIO_PID 指向测试用的编辑器进程" }
        Click (ById $w "btnOpen")
        Start-Sleep -Milliseconds 1500
        $d = $w.FindFirst($TS::Descendants, (Cond $AE::ClassNameProperty "#32770"))
        if (-not $d) { throw "没有打开对话框" }
        $name = $d.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((Cond $AE::AutomationIdProperty "1148"), (Cond $AE::ControlTypeProperty $CT::Edit))))
        $name.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Arg)
        $b = $d.FindFirst($TS::Children, (Cond $AE::AutomationIdProperty "1"))
        [void][VbUi.Native]::SendMessage([IntPtr]$b.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
        "打开：$Arg"
    }
    default { throw "未知步骤 $Step" }
}
