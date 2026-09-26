using Inbrisk.Platform.Windows.Input;
using Inbrisk.Core;

var fired = new ManualResetEventSlim();
var resumed = new ManualResetEventSlim();
var svc = new GlobalHotkeyService("Ctrl+Alt+F10", "Ctrl+Alt+Shift+F10",
    () => { Console.Error.WriteLine("PANIC FIRED"); fired.Set(); },
    () => { Console.Error.WriteLine("RESUME FIRED"); resumed.Set(); },
    m => Console.Error.WriteLine($"FAIL: {m}"));
svc.Start();
Console.Error.WriteLine($"panic={svc.PanicAvailable} resume={svc.ResumeAvailable}");
var input = new SendInputService();
await Task.Delay(300);
input.Hotkey(new[] { KeyCode.Ctrl, KeyCode.Alt }, KeyCode.F10);
var ok = fired.Wait(3000);
Console.Error.WriteLine(ok ? "PANIC OK" : "PANIC NOT RECEIVED");
input.Hotkey(new[] { KeyCode.Ctrl, KeyCode.Alt, KeyCode.Shift }, KeyCode.F10);
var ok2 = resumed.Wait(3000);
Console.Error.WriteLine(ok2 ? "RESUME OK" : "RESUME NOT RECEIVED");
svc.Dispose();
return ok && ok2 ? 0 : 1;
