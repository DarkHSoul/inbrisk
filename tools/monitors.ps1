Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
  $_.DeviceName + " bounds=" + $_.Bounds + " primary=" + $_.Primary
}
