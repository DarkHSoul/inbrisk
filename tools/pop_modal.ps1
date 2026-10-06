param([long]$OwnerHwnd, [string]$Text, [string]$Title, [int]$Buttons = 1, [int]$DelayMs = 1200)
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class MB { [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type); }
'@
Start-Sleep -Milliseconds $DelayMs
# Real Win32 MessageBox (#32770, Button-class children) owned by the run's
# target hwnd — the shape the interceptor's owner-chain detection keys on.
[MB]::MessageBoxW([IntPtr]$OwnerHwnd, $Text, $Title, [uint32]($Buttons + 0x40)) | Out-Null
