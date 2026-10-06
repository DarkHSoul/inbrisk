using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Runtime;
using Xunit;
using Xunit.Abstractions;
using Role = Inbrisk.Core.Role;

namespace Inbrisk.Tests;

public sealed class PhaseFBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public PhaseFBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class BenchEventSource : IEventSource
    {
        public event Action<ObservedEvent>? Event;
        public void Start() { }
        public void Dispose() { }
        public void Fire(ObservedEvent e) => Event?.Invoke(e);
    }

    private sealed class BenchCaptureService : ICaptureService
    {
        public Frame Capture(RectPx region, int maxImageWidth = 1600) => throw new NotImplementedException();
        public Frame CaptureWindow(long hwnd, int maxImageWidth = 1600) => throw new NotImplementedException();
        public RawFrame CaptureRaw(RectPx region) => throw new NotImplementedException();
        public double DiffFraction(RectPx region, int sampleScale = 8) => 0;
        public byte[] Sample(RectPx region, int scale = 8) => [0, 0, 0];
        public ICaptureSession CreateSession(CaptureTarget target) => throw new NotImplementedException();
    }

    private sealed class BenchWindowService : IWindowService
    {
        public WindowInfo? Foreground;
        public WindowInfo? ActiveModal;
        public IReadOnlyList<WindowInfo> ListWindows() => Foreground != null ? [Foreground] : [];
        public WindowInfo? GetWindow(long hwnd) => Foreground?.Hwnd == hwnd ? Foreground : null;
        public WindowInfo? GetForegroundWindow() => Foreground;
        public IReadOnlyList<MonitorInfo> GetMonitors() => [];
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;
        public WindowInfo? GetModalPopup(long hwnd) => ActiveModal;
        public WindowInfo? GetActiveBlockingPopup(long? targetHwnd = null) => ActiveModal;
        public bool IsWindowEnabled(long hwnd) => true;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => [];
        public bool IsWindowProtected(long hwnd, out string? reason) { reason = null; return false; }
    }

    private static UiElement MakeEl(string id, string name, Role role, long hwnd = 100)
    {
        var props = new Dictionary<string, object?> { ["enabled"] = true };
        var bounds = new RectPx(10, 10, 100, 30);
        var handle = new ElementHandle(BackendId.Uia, id,
            new ReResolveRecipe(1, hwnd, "app", role, name, id, [], bounds));
        return new UiElement(id, BackendId.Uia, role, name, bounds, ["click"], props, handle, 1, hwnd);
    }

    private static double P50(List<double> vals)
    {
        if (vals.Count == 0) return 0;
        vals.Sort();
        return vals[vals.Count / 2];
    }

    private static double P90(List<double> vals)
    {
        if (vals.Count == 0) return 0;
        vals.Sort();
        return vals[(int)(vals.Count * 0.9)];
    }

    [Fact]
    public void RunAllSyntheticBenchmarks_N100()
    {
        const int N = 100;
        var results = new Dictionary<string, object>();

        // -------------------------------------------------------------
        // Scenario A: Immediate event completion
        // -------------------------------------------------------------
        {
            var latencies = new List<double>();
            long totalEventWakes = 0;
            long totalFallbackPolls = 0;
            long totalPropertyChecks = 0;

            for (int i = 0; i < N; i++)
            {
                var events = new BenchEventSource();
                var elements = new List<UiElement>();
                var wait = new WaitService(spec => elements, new BenchCaptureService(), events, new ElementRegistry([]));

                var sw = Stopwatch.StartNew();
                var task = Task.Run(() => wait.ForElement(new FindSpec(Name: "Target", Hwnd: 100), timeoutMs: 1000));

                // Emit event after 15ms
                Thread.Sleep(15);
                elements.Add(MakeEl("t1", "Target", Role.Button, 100));
                events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 100, ElementId: "t1", Detail: "Button"));

                var res = task.Result;
                sw.Stop();
                latencies.Add(res.Elapsed.TotalMilliseconds);
                totalEventWakes += wait.Telemetry.EventWakeCount;
                totalFallbackPolls += wait.Telemetry.FallbackPollCount;
                totalPropertyChecks += wait.Telemetry.PropertyCheckCount;
            }

            results["ScenarioA"] = new
            {
                Description = "Immediate event completion (N=100)",
                P50Ms = Math.Round(P50(latencies), 2),
                P90Ms = Math.Round(P90(latencies), 2),
                AvgLatencyMs = Math.Round(latencies.Average(), 2),
                AvgEventWakesPerRun = Math.Round((double)totalEventWakes / N, 2),
                AvgFallbackPollsPerRun = Math.Round((double)totalFallbackPolls / N, 2),
                AvgPropertyChecksPerRun = Math.Round((double)totalPropertyChecks / N, 2)
            };
        }

        // -------------------------------------------------------------
        // Scenario B: No event available (fallback discovers change)
        // -------------------------------------------------------------
        {
            var latencies = new List<double>();
            long totalEventWakes = 0;
            long totalFallbackPolls = 0;
            long totalPropertyChecks = 0;

            for (int i = 0; i < N; i++)
            {
                var events = new BenchEventSource(); // emits no events
                var elements = new List<UiElement>();
                var wait = new WaitService(spec => elements, new BenchCaptureService(), events, new ElementRegistry([]));

                var sw = Stopwatch.StartNew();
                var task = Task.Run(() => wait.ForElement(new FindSpec(Name: "Target", Hwnd: 100), timeoutMs: 1500));

                // State changes at 60ms without event
                Task.Delay(60).ContinueWith(_ => elements.Add(MakeEl("t1", "Target", Role.Button, 100)));

                var res = task.Result;
                sw.Stop();
                latencies.Add(res.Elapsed.TotalMilliseconds);
                totalEventWakes += wait.Telemetry.EventWakeCount;
                totalFallbackPolls += wait.Telemetry.FallbackPollCount;
                totalPropertyChecks += wait.Telemetry.PropertyCheckCount;
            }

            results["ScenarioB"] = new
            {
                Description = "No event available / fallback polling (N=100)",
                P50Ms = Math.Round(P50(latencies), 2),
                P90Ms = Math.Round(P90(latencies), 2),
                AvgLatencyMs = Math.Round(latencies.Average(), 2),
                AvgEventWakesPerRun = Math.Round((double)totalEventWakes / N, 2),
                AvgFallbackPollsPerRun = Math.Round((double)totalFallbackPolls / N, 2),
                AvgPropertyChecksPerRun = Math.Round((double)totalPropertyChecks / N, 2)
            };
        }

        // -------------------------------------------------------------
        // Scenario C: Event storm (irrelevant bursts + 1 relevant)
        // -------------------------------------------------------------
        {
            var latencies = new List<double>();
            long totalEventWakes = 0;
            long totalIgnoredEvents = 0;
            long totalPropertyChecks = 0;

            for (int i = 0; i < N; i++)
            {
                var events = new BenchEventSource();
                var elements = new List<UiElement>();
                var wait = new WaitService(spec => elements, new BenchCaptureService(), events, new ElementRegistry([]));

                var sw = Stopwatch.StartNew();
                var task = Task.Run(() => wait.ForElement(new FindSpec(Name: "Target", Hwnd: 100), timeoutMs: 1000));

                // Blast 50 irrelevant events
                for (int k = 0; k < 50; k++)
                {
                    events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 9999, ElementId: $"noise_{k}", Detail: "Edit"));
                }

                // Relevant event arrives
                elements.Add(MakeEl("t1", "Target", Role.Button, 100));
                events.Fire(new ObservedEvent(EventKind.StructureChanged, DateTimeOffset.UtcNow, Hwnd: 100, ElementId: "t1", Detail: "Button"));

                var res = task.Result;
                sw.Stop();
                latencies.Add(res.Elapsed.TotalMilliseconds);
                totalEventWakes += wait.Telemetry.EventWakeCount;
                totalIgnoredEvents += wait.Telemetry.IgnoredEvents;
                totalPropertyChecks += wait.Telemetry.PropertyCheckCount;
            }

            results["ScenarioC"] = new
            {
                Description = "Event storm filtering & coalescing (N=100)",
                P50Ms = Math.Round(P50(latencies), 2),
                P90Ms = Math.Round(P90(latencies), 2),
                AvgLatencyMs = Math.Round(latencies.Average(), 2),
                AvgIgnoredEventsPerRun = Math.Round((double)totalIgnoredEvents / N, 2),
                AvgPropertyChecksPerRun = Math.Round((double)totalPropertyChecks / N, 2)
            };
        }

        // -------------------------------------------------------------
        // Scenario D: Unexpected dialog interruption
        // -------------------------------------------------------------
        {
            var latencies = new List<double>();
            int interruptedCount = 0;

            for (int i = 0; i < N; i++)
            {
                var events = new BenchEventSource();
                var winSvc = new BenchWindowService
                {
                    Foreground = new WindowInfo(100, 1, "App", "app", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0)
                };
                var wait = new WaitService(spec => [], new BenchCaptureService(), events, new ElementRegistry([]), winSvc);

                var sw = Stopwatch.StartNew();
                var task = Task.Run(() => wait.ForCondition(new WaitCondition(Name: "Save", Hwnd: 100, StopOnUnexpectedDialog: true), timeoutMs: 1000));

                Thread.Sleep(10);
                winSvc.ActiveModal = new WindowInfo(999, 1, "Error: Disk Full", "app", new RectPx(100, 100, 400, 200), WindowState.Normal, true, false, true, 0);
                events.Fire(new ObservedEvent(EventKind.WindowOpened, DateTimeOffset.UtcNow, Hwnd: 999, ElementId: "Error Dialog", Detail: "Window"));

                var res = task.Result;
                sw.Stop();
                latencies.Add(res.Elapsed.TotalMilliseconds);
                if (res.InterruptedByDialog) interruptedCount++;
            }

            results["ScenarioD"] = new
            {
                Description = "Unexpected dialog event-gated detection (N=100)",
                P50Ms = Math.Round(P50(latencies), 2),
                P90Ms = Math.Round(P90(latencies), 2),
                InterruptedRate = $"{interruptedCount}/{N}"
            };
        }

        // -------------------------------------------------------------
        // Scenario E: Timeout enforcement (no terminal condition)
        // -------------------------------------------------------------
        {
            var latencies = new List<double>();
            long totalFallbackPolls = 0;

            // Run 20 reps for timeout with 150ms timeout to avoid excessive test suite delay
            for (int i = 0; i < 20; i++)
            {
                var events = new BenchEventSource();
                var wait = new WaitService(spec => [], new BenchCaptureService(), events, new ElementRegistry([]));

                var res = wait.ForElement(new FindSpec(Name: "NeverFound"), timeoutMs: 150);
                latencies.Add(res.Elapsed.TotalMilliseconds);
                totalFallbackPolls += wait.Telemetry.FallbackPollCount;
            }

            results["ScenarioE"] = new
            {
                Description = "Timeout bounded polling (N=20, timeout=150ms)",
                P50Ms = Math.Round(P50(latencies), 2),
                P90Ms = Math.Round(P90(latencies), 2),
                AvgFallbackPollsPerRun = Math.Round((double)totalFallbackPolls / 20, 2)
            };
        }

        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        _output.WriteLine(json);
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
            var targetPath = Path.Combine(repoRoot, "tools", "action_benchmark_results.json");
            File.WriteAllText(targetPath, json);
        }
        catch { }
    }
}
