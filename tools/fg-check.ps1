Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.Text;
public class FG{
[DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out int pid);
[DllImport("user32.dll")]public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
}
'@
$h=[FG]::GetForegroundWindow();$p=0;[FG]::GetWindowThreadProcessId($h,[ref]$p)|Out-Null
$t=New-Object Text.StringBuilder 256;[FG]::GetWindowText($h,$t,256)|Out-Null
$proc=Get-Process -Id $p
Write-Output ("fg=0x{0:X} pid={1} name={2} title={3}" -f $h.ToInt64(),$p,$proc.ProcessName,$t.ToString())
# elevation check: whoami /groups | findstr high-integrity equivalent — use token query
$isElev = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Output ("this shell elevated: " + $isElev)
