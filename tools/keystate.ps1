Add-Type -Name U32 -Namespace W -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern short GetAsyncKeyState(int v);'
$m = @{Ctrl=0x11; Shift=0x10; Alt=0x12; LWin=0x5B; RWin=0x5C; LBtn=0x01; RBtn=0x02; MBtn=0x04}
foreach ($k in $m.GetEnumerator()) {
    $down = ([W.U32]::GetAsyncKeyState($k.Value) -band 0x8000) -ne 0
    Write-Output "$($k.Key)=$down"
}
