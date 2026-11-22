# 用 Win32（不经过 UIA）找到某个进程弹出的系统确认框（#32770），读文字，给「是」(6) 或「否」(7) 发 BM_CLICK。
# UIA 的 Toggle 调用要等确认框关掉才返回，期间对这个进程的其他 UIA 请求会超时，所以这里只用 Win32。
param([int]$StudioPid, [int]$Button = 7, [int]$WaitMs = 6000)
if (-not ("B4.W" -as [type])) {
    Add-Type -Namespace B4 -Name W -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
'@
}
function FindBox {
    $found = [IntPtr]::Zero
    $cb = [B4.W+EnumProc]{ param($h, $l)
        $p = 0; [void][B4.W]::GetWindowThreadProcessId($h, [ref]$p)
        if ($p -eq $StudioPid -and [B4.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 64; [void][B4.W]::GetClassName($h, $sb, 64)
            if ($sb.ToString() -eq "#32770") { $script:box = $h; return $false }
        }
        return $true }
    $script:box = [IntPtr]::Zero
    [void][B4.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:box
}
$sw = [Diagnostics.Stopwatch]::StartNew(); $h = [IntPtr]::Zero
while ($sw.ElapsedMilliseconds -lt $WaitMs -and $h -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 150; $h = FindBox }
if ($h -eq [IntPtr]::Zero) { "没有出现确认框"; exit 1 }
$t = New-Object System.Text.StringBuilder 512; [void][B4.W]::GetWindowText($h, $t, 512)
$m = New-Object System.Text.StringBuilder 1024; [void][B4.W]::GetWindowText([B4.W]::GetDlgItem($h, 0xFFFF), $m, 1024)
$btn = [B4.W]::GetDlgItem($h, $Button)
[void][B4.W]::SendMessage($btn, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
$first = ($m.ToString() -split "`n")[0]
"确认框「$($t.ToString())」：$first … → 点了 " + $(if ($Button -eq 6) { "是" } else { "否" })
