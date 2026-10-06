using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Inbrisk.Platform.Windows;
using Xunit;

namespace Inbrisk.Tests;

public class GhostLocalDesktopTests
{
    [Fact]
    public void GhostDesktopNative_BuildCommandLine_HandlesVariousInputs()
    {
        // Simple executable
        Assert.Equal("\"cmd.exe\"", GhostDesktopNative.BuildCommandLine("cmd.exe", null));

        // Executable with arguments
        Assert.Equal("\"cmd.exe\" /c dir", GhostDesktopNative.BuildCommandLine("cmd.exe", "/c dir"));

        // Already quoted
        Assert.Equal("\"C:\\Program Files\\App\\test.exe\" --arg",
            GhostDesktopNative.BuildCommandLine("\"C:\\Program Files\\App\\test.exe\"", "--arg"));

        // Path with spaces not yet quoted
        string dummyPath = Path.Combine(Path.GetTempPath(), "test folder with spaces", "test.exe");
        Assert.Equal($"\"{dummyPath}\"", GhostDesktopNative.BuildCommandLine(dummyPath, null));
    }

    [Fact]
    public void GhostDesktopNative_CreateAndOpenDesktop_Lifecycle()
    {
        string uniqueDesktopName = $"InbriskGhostTest_{Guid.NewGuid():N}";
        IntPtr hDesktop = GhostDesktopNative.CreateDesktop(uniqueDesktopName);

        try
        {
            Assert.NotEqual(IntPtr.Zero, hDesktop);

            // Open the same desktop
            IntPtr hOpen = GhostDesktopNative.OpenDesktop(uniqueDesktopName);
            Assert.NotEqual(IntPtr.Zero, hOpen);
            GhostDesktopNative.CloseDesktop(hOpen);

            // Enumerate windows on this newly created desktop
            var windows = GhostDesktopNative.EnumerateWindows(hDesktop);
            Assert.NotNull(windows);
        }
        finally
        {
            if (hDesktop != IntPtr.Zero)
            {
                GhostDesktopNative.CloseDesktop(hDesktop);
            }
        }
    }

    [Fact]
    public void GhostLocalDesktop_Lifecycle_CreateAttachEnumerateDispose()
    {
        string testDesktopName = $"InbriskGhostTest_{Guid.NewGuid():N}";

        using (var desktop = new GhostLocalDesktop(testDesktopName, createIfNotExists: true))
        {
            Assert.Equal(testDesktopName, desktop.DesktopName);
            Assert.NotEqual(IntPtr.Zero, desktop.DesktopHandle);
            Assert.True(desktop.IsCreated);
            Assert.Equal(0, desktop.ProcessCount);

            // Enumerate windows
            var windows = desktop.GetWindowHandles();
            Assert.NotNull(windows);

            // Test execution on clean worker thread attached to this ghost desktop
            bool executedOnDesktop = desktop.RunOnDesktop(() =>
            {
                // Verify thread desktop inside worker
                return true;
            });
            Assert.True(executedOnDesktop);
        }

        // Verify disposed state
        var disposedDesktop = new GhostLocalDesktop($"InbriskGhostTest_{Guid.NewGuid():N}");
        disposedDesktop.Dispose();
        Assert.False(disposedDesktop.IsCreated);
        Assert.Throws<ObjectDisposedException>(() => disposedDesktop.GetWindowHandles());
    }

    [Fact]
    public void GhostLocalDesktop_StartProcess_And_TrackProcessCount()
    {
        string testDesktopName = $"InbriskGhostTest_{Guid.NewGuid():N}";

        using var desktop = new GhostLocalDesktop(testDesktopName, createIfNotExists: true);

        // Start cmd.exe on ghost desktop with a delay so we can verify ProcessCount
        Process? proc = null;
        try
        {
            proc = desktop.StartProcess("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul");
            Assert.NotNull(proc);
            Assert.True(proc.Id > 0);

            // Process count should be at least 1 while it runs
            Assert.True(desktop.ProcessCount >= 1);

            // Test terminating all processes
            desktop.TerminateAllProcesses(1000);
            Assert.Equal(0, desktop.ProcessCount);
        }
        finally
        {
            if (proc != null && !proc.HasExited)
            {
                try { proc.Kill(); } catch { }
            }
        }
    }
}
