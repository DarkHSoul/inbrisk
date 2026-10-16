"""Dogrulama: test hostu OLDURULDUgunde TestApp de olmeli.

Sorunun uzeri yaziliyordu. DesktopFixture.Dispose() TestApp'i olduruyordu
AMA Dispose yalnizca koleksiyon normal BITINCE cagriliyor. Host olundugu
veya bir test asili kaldiginda Dispose hic calismiyor -> TestApp penceresi
ortada kaliyor -> kullanici "inbrisk takildi" diyor.

Duzeltme: TestApp bir Windows Job Object icine alindi; isletim sistemi
tum handle'lari kapattiginda KILL_ON_JOB_CLOSE tetiklenir.

Bu betik bilerek ASILI BIR KOSUM baslatip hostu olduruyor ve TestApp'in
ortada kalmadigini olcuyor.
"""
import io
import subprocess
import sys
import time

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8",
                              errors="replace")

REPO = r"C:\Users\Ahmet\Documents\inbrisk"
PATTERN = ("Inbrisk.Tests", "Inbrisk.TestApp", "vstest")


def procs(kind):
    """kind: 'hosts' | 'testapp'"""
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | "
         "ForEach-Object { \"$($_.ProcessId)|$($_.CommandLine)\" }"],
        capture_output=True, text=True, timeout=120).stdout
    hosts, app = [], []
    for line in out.splitlines():
        if "|" not in line:
            continue
        pid, cmd = line.split("|", 1)
        pid = pid.strip()
        if any(k in cmd for k in PATTERN):
            hosts.append(int(pid))
        if "Inbrisk.TestApp" in cmd:
            app.append(int(pid))
    return hosts if kind == "hosts" else app


print("=" * 72)
print("DOGRULAMA: host oldurulunca TestApp de oluyor mu?")
print("=" * 72)

print(f"  baslangicta TestApp   : {procs('testapp')}")

# desktop koleksiyonuna ait bir test kosumu baslat
p = subprocess.Popen(
    ["dotnet", "test", "--nologo", "--filter",
     "FullyQualifiedName~Inbrisk.Tests.ActionTests"],
    cwd=REPO, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    creationflags=subprocess.CREATE_NO_WINDOW)

print("  kosum baslatildi, TestApp'in acmasi bekleniyor...")
app, t0 = [], time.time()
while time.time() - t0 < 90:
    app = procs("testapp")
    if app:
        break
    time.sleep(2)
print(f"  TestApp acildi        : {app}")
if not app:
    print("  TestApp acilmadi; dogrulanamadi.")
    try:
        p.kill()
    except Exception:
        pass
    sys.exit(2)

print("\n  >>> simdi test hostunu OLDURUYORUZ (Dispose calismayacak)")
try:
    p.kill()
except Exception:
    pass
time.sleep(2)
for pid in procs("hosts"):
    subprocess.run(["taskkill", "/PID", str(pid), "/F"],
                   capture_output=True)
time.sleep(6)

left = procs("testapp")
print(f"  host oldukten sonra TestApp : {left}")
if left:
    print(f"  BASARISIZ: {len(left)} TestApp ortada kaldi -> pencere ekranda kalir")
    for pid in left:
        subprocess.run(["taskkill", "/PID", str(pid), "/F"],
                       capture_output=True)
    sys.exit(1)
print("  GECTI: host oldu, TestApp de Job Object sayesinde sonlandi")
