param([long]$OwnerHwnd = 0)
$src = @'
using System; using System.Runtime.InteropServices; using System.Text;
public class W32 {
 [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
 public delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
 [DllImport("user32.dll")] public static extern int GetWindowLongPtrW(IntPtr h, int idx);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
'@
Add-Type -TypeDefinition $src
Start-Sleep -Milliseconds 400
$found = $false
[W32]::EnumWindows({ param($h,$p)
    $sb = New-Object Text.StringBuilder 256
    [void][W32]::GetClassNameW($h,$sb,256)
    if ($sb.ToString() -eq "#32770") {
        $t = New-Object Text.StringBuilder 512; [void][W32]::GetWindowTextW($h,$t,512)
        $own = [W32]::GetWindow($h,4)
        $style = [W32]::GetWindowLongPtrW($h,-16)
        $ex = [W32]::GetWindowLongPtrW($h,-20)
        $pid2 = 0; [void][W32]::GetWindowThreadProcessId($h,[ref]$pid2)
        Write-Output ("DIALOG hwnd=0x{0:X} title='{1}' owner=0x{2:X} style=0x{3:X8} ex=0x{4:X8} vis={5} pid={6}" -f $h.ToInt64(),$t,$own.ToInt64(),$style,$ex,[W32]::IsWindowVisible($h),$pid2)
        $script:found = $true
    }
    return $true
}, [IntPtr]::Zero) | Out-Null
if (-not $found) { Write-Output "no #32770 dialogs found" }
Write-Output ("target owner hwnd was 0x{0:X}" -f $OwnerHwnd)
