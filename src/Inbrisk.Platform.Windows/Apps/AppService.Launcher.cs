using System.Diagnostics;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Microsoft.Win32;
using Windows.Win32;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Apps;

public sealed partial class AppService : IAppService, IDisposable
{
    /// <summary>Top-level user-facing window owned by a process matching the
    /// app. Helper processes never win — only windows with a real title and
    /// non-empty bounds count; shell surfaces (desktop/tray) never match.</summary>
    private WindowInfo? FindRunning(string displayName, string[]? exeHints,
        bool titleMatch = true)
    {
        var norm = Norm(displayName);
        var hints = (exeHints ?? []).Select(Norm).ToList();
        var wins = SnapshotWindows();
        return wins
            .Where(w =>
            {
                if (w.Bounds.IsEmpty || string.IsNullOrEmpty(w.Title) ||
                    IsShellSurface(w.Hwnd))
                    return false;
                var pn = NormProc(w.ProcessName);
                if (hints.Contains(pn)) return true;
                if (pn == norm) return true;
                // Title match only counts at the title BOUNDARY as a whole
                // word-run — a document title merely containing the app name
                // ("…Spotify/Chrome Hatası…" in an IDE window) is not the
                // running app. Windows owned by processes whose titles embed
                // documents/tabs/chats (IDEs, terminals, editors, the agent
                // host itself) are never attributed by title at all.
                return titleMatch && pn.Length > 0 && norm.Length >= 3 &&
                    !DocumentTitleHosts.Contains(pn) &&
                    TitleBoundaryMatch(w.Title, norm);
            })
            .OrderByDescending(w => w.IsForeground)
            .ThenByDescending(w => w.Bounds.Width * w.Bounds.Height)
            .ThenBy(w => w.Hwnd)
            .FirstOrDefault();
    }

    // ------------------------------------------------------------------
    //  spawn + readiness
    // ------------------------------------------------------------------

    private int? SpawnReal(ResolvedApp app, IReadOnlyList<string>? args)
    {
        switch (app.Method)
        {
            case LaunchMethod.Aumid:
                return ActivateAumid(app.Identifier);
            case LaunchMethod.Protocol:
                DesktopBridge.RunOnDefaultDesktop(() =>
                {
                    Process.Start(new ProcessStartInfo(app.Identifier)
                        { UseShellExecute = true });
                });
                return null;
            default:
            {
                if (app.Identifier.Contains(@"\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                {
                    return SpawnWindowsApp(app, args);
                }

                return DesktopBridge.CreateProcessOnDefaultDesktop(app.Identifier, args);
            }
        }
    }

    private int? SpawnWindowsApp(ResolvedApp app, IReadOnlyList<string>? args)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var aliasDir = Path.Combine(localAppData, @"Microsoft\WindowsApps");
        var exeName = Path.GetFileName(app.Identifier);

        // 1. Try execution alias in %LOCALAPPDATA%\Microsoft\WindowsApps (e.g. notepad.exe, mspaint.exe)
        var candidates = new List<string> { exeName };
        if (exeName.Contains("paint", StringComparison.OrdinalIgnoreCase) ||
            app.DisplayName.Contains("paint", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("mspaint.exe");
            candidates.Add("pbrush.exe");
        }
        else if (exeName.Contains("notepad", StringComparison.OrdinalIgnoreCase) ||
                 app.DisplayName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("notepad.exe");
        }
        foreach (var hint in app.ExeHints)
        {
            if (!string.IsNullOrEmpty(hint))
            {
                candidates.Add(hint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? hint : hint + ".exe");
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var aliasPath = Path.Combine(aliasDir, candidate);
            if (File.Exists(aliasPath))
            {
                return DesktopBridge.CreateProcessOnDefaultDesktop(aliasPath, args);
            }
        }

        // 2. Try AUMID resolution from package directory manifest
        var aumid = TryResolveAumidFromWindowsAppsPath(app.Identifier);
        if (aumid != null)
        {
            return ActivateAumid(aumid);
        }

        // 3. Fallback to package search
        var pkg = (PackageEnumerator ?? EnumeratePackages)()
            .FirstOrDefault(p => app.ExeHints.Any(h => p.ExeHints.Contains(h, StringComparer.OrdinalIgnoreCase)) ||
                                 string.Equals(p.DisplayName, app.DisplayName, StringComparison.OrdinalIgnoreCase));
        if (pkg != null)
        {
            return ActivateAumid(pkg.Identifier);
        }

        return DesktopBridge.CreateProcessOnDefaultDesktop(app.Identifier, args);
    }

    private static string? TryResolveAumidFromWindowsAppsPath(string path)
    {
        try
        {
            var marker = @"\Program Files\WindowsApps\";
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var sub = path.Substring(idx + marker.Length);
            var packageDirName = sub.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            var rootDir = path.Substring(0, idx + marker.Length);
            var manifestPath = Path.Combine(rootDir, packageDirName, "AppxManifest.xml");
            if (File.Exists(manifestPath))
            {
                var parts = packageDirName.Split('_');
                if (parts.Length >= 2)
                {
                    var pfn = $"{parts[0]}_{parts[^1]}";
                    var appId = ReadAppId(manifestPath);
                    if (!string.IsNullOrEmpty(appId))
                    {
                        return $"{pfn}!{appId}";
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Packaged-app activation via IApplicationActivationManager;
    /// falls back to `explorer shell:AppsFolder\aumid` — a folder verb, not
    /// a command line.</summary>
    private static int? ActivateAumid(string aumid)
    {
        return DesktopBridge.RunOnDefaultDesktop(() =>
        {
            try
            {
                var mgr = (IApplicationActivationManager)
                    Activator.CreateInstance(
                        Type.GetTypeFromCLSID(new Guid(
                            "45BA127D-10A8-46EA-8AB7-56EA9078943C"))!)!;
                var hr = mgr.ActivateApplication(aumid, null, 0, out var pid);
                if (hr >= 0 && pid > 0) return (int?)pid;
            }
            catch { }
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"shell:AppsFolder\\{aumid}") { UseShellExecute = true });
            return null;
        });
    }

    private static bool IsLauncherOrStub(ResolvedApp app, string? spawnedName)
    {
        if (app.Method == LaunchMethod.Aumid) return true;

        var id = app.Identifier ?? "";
        if (id.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
            id.Contains(@"\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            return true;

        if (id.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("stub", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("bootstrapper", StringComparison.OrdinalIgnoreCase))
            return true;

        var disp = app.DisplayName ?? "";
        if (disp.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
            disp.Contains("stub", StringComparison.OrdinalIgnoreCase))
            return true;

        if (spawnedName != null && (spawnedName.Contains("launcher") || spawnedName.Contains("stub")))
            return true;

        if (app.ExeHints.Any(h => h.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
                                 h.Contains("stub", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private sealed class ApplicationActivationManager { }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.InterfaceType(
        System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    private interface IApplicationActivationManager
    {
        [System.Runtime.InteropServices.PreserveSig]
        int ActivateApplication(
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            string appUserModelId,
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            string? arguments,
            int options, out int processId);

        // vtable placeholders — ActivateForFile / ActivateForProtocol
        [System.Runtime.InteropServices.PreserveSig]
        int ActivateForFile(string aumid, System.IntPtr itemArray,
            string? verb, out int processId);
        [System.Runtime.InteropServices.PreserveSig]
        int ActivateForProtocol(string aumid, System.IntPtr itemArray,
            out int processId);
    }
}
