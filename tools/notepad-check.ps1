Get-Process notepad -ErrorAction SilentlyContinue | ForEach-Object {
  Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public class WR{[DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out RECT r);
public struct RECT{public int Left,Top,Right,Bottom;}}
'@ -ErrorAction SilentlyContinue
  $r = New-Object WR+RECT
  [WR]::GetWindowRect($_.MainWindowHandle, [ref]$r) | Out-Null
  Write-Output ("pid=" + $_.Id + " title='" + $_.MainWindowTitle + "' rect=" + $r.Left + "," + $r.Top)
}
