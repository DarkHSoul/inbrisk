import json
import os
import subprocess
import sys
import xml.etree.ElementTree as ET

INTERACTIVE_DESKTOP_CLASSES = {
    "ActionTests",
    "AgentTests",
    "ApplicationAdapterTests",
    "CaptureTests",
    "DesktopShellTests",
    "EmergencyHotkeyTests",
    "EndToEndAndEdgeCaseTests",
    "EventWaitCaptureTests",
    "HardeningTests",
    "IndicatorTests",
    "InputLifecycleTests",
    "InputStressTests",
    "LaunchIntegrationTests",
    "LaunchMcpTests",
    "McpTests",
    "StdioHygieneTests",
    "TopologyTests",
    "UiaTests"
}

def main():
    manifest_path = r"C:\Users\Ahmet\Documents\inbrisk\artifacts\baseline_manifest_4271b3f.json"
    if not os.path.exists(manifest_path):
        print(f"Error: Manifest not found at {manifest_path}")
        sys.exit(1)

    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    baseline_all_passed = set(manifest["passed"])
    baseline_all_failed = set(manifest["knownBaselineFailures"])

    # Step 1: Discover all current tests in the workspace
    print("[1/4] Discovering test suite...")
    list_cmd = ["dotnet", "test", "--list-tests", "--configuration", "Release", "--no-build", "--no-restore"]
    proc = subprocess.run(list_cmd, cwd=r"C:\Users\Ahmet\Documents\inbrisk\tests\Inbrisk.Tests", capture_output=True, text=True)
    if proc.returncode != 0:
        print("Failed to list tests:", proc.stderr)
        sys.exit(1)

    current_all_tests = set()
    for line in proc.stdout.splitlines():
        line = line.strip()
        if line.startswith("Inbrisk.Tests."):
            current_all_tests.add(line)

    # Classify safe vs interactive
    safe_current_tests = set()
    interactive_current_tests = set()

    for t in current_all_tests:
        cls = t.split(".")[2]
        if cls in INTERACTIVE_DESKTOP_CLASSES:
            interactive_current_tests.add(t)
        else:
            safe_current_tests.add(t)

    safe_classes = sorted({t.split(".")[2] for t in safe_current_tests})
    baseline_safe_passed = {t for t in baseline_all_passed if t.split(".")[2] not in INTERACTIVE_DESKTOP_CLASSES}
    new_safe_tests = safe_current_tests - baseline_safe_passed

    print(f"Total Workspace Tests:                {len(current_all_tests)}")
    print(f"  * Headless-Safe Tests:              {len(safe_current_tests)}")
    print(f"  * Desktop/Interactive Tests:        {len(interactive_current_tests)} (SKIPPED per user desktop policy)")
    print(f"  * Baseline Safe-Passed Tests:       {len(baseline_safe_passed)}")
    print(f"  * New Safe Tests on Branch:         {len(new_safe_tests)}")

    # Step 2: Build filter expression for safe test classes
    # Expression: FullyQualifiedName~Inbrisk.Tests.Class1|FullyQualifiedName~Inbrisk.Tests.Class2...
    filter_expr = "|".join([f"FullyQualifiedName~Inbrisk.Tests.{cls}." for cls in safe_classes])

    trx_filename = "fast_gate_safe_results.trx"
    trx_dir = r"C:\Users\Ahmet\Documents\inbrisk\tests\Inbrisk.Tests\TestResults"
    trx_path = os.path.join(trx_dir, trx_filename)
    if os.path.exists(trx_path):
        try: os.remove(trx_path)
        except: pass

    # Step 3: Run safe test suite
    print("\n[2/4] Executing Headless-Safe Test Suite (0 desktop disturbance)...")
    test_cmd = [
        "dotnet", "test",
        "--configuration", "Release",
        "--no-build",
        "--no-restore",
        "--filter", filter_expr,
        "--logger", f"trx;LogFileName={trx_filename}"
    ]
    res = subprocess.run(test_cmd, cwd=r"C:\Users\Ahmet\Documents\inbrisk\tests\Inbrisk.Tests")

    # Step 4: Parse Results
    print("\n[3/4] Parsing execution results...")
    if not os.path.exists(trx_path):
        print("Error: Results TRX was not generated!")
        print("STDOUT:\n", res.stdout)
        print("STDERR:\n", res.stderr)
        sys.exit(1)

    tree = ET.parse(trx_path)
    root = tree.getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

    executed_passed = set()
    executed_failed = set()

    for r in root.findall(".//t:UnitTestResult", ns):
        tname = r.get("testName")
        outcome = r.get("outcome")
        if outcome == "Passed":
            executed_passed.add(tname)
        elif outcome == "Failed":
            executed_failed.add(tname)

    baseline_safe_executed = baseline_safe_passed & (executed_passed | executed_failed)
    baseline_safe_newly_failing = baseline_safe_executed & executed_failed
    new_safe_executed = new_safe_tests & (executed_passed | executed_failed)
    new_safe_failing = new_safe_executed & executed_failed

    print("\n" + "=" * 65)
    print("FAST REVIEW GATE REPORT — HEADLESS / SAFE VALIDATION")
    print("=" * 65)
    print(f"Safe Tests Executed:                 {len(executed_passed) + len(executed_failed)}")
    print(f"Safe Tests Passed:                   {len(executed_passed)}")
    print(f"Safe Tests Failed:                   {len(executed_failed)}")
    print(f"Desktop/Interactive Tests Skipped:   {len(interactive_current_tests)}")
    print("-" * 65)
    print(f"Baseline Safe-Passed Executed:       {len(baseline_safe_executed)}")
    print(f"  * Baseline Newly Failing:          {len(baseline_safe_newly_failing)}")
    print(f"New Safe Tests Executed:             {len(new_safe_executed)}")
    print(f"  * New Safe Tests Failing:          {len(new_safe_failing)}")
    print("=" * 65)

    gate_passed = (len(baseline_safe_newly_failing) == 0) and (len(new_safe_failing) == 0)

    if baseline_safe_newly_failing:
        print("\n[REGRESSION] Baseline safe-passed tests failing:")
        for t in sorted(baseline_safe_newly_failing):
            print(f"  FAIL: {t}")

    if new_safe_failing:
        print("\n[REGRESSION] New safe tests failing:")
        for t in sorted(new_safe_failing):
            print(f"  FAIL: {t}")

    if gate_passed:
        print("GATE STATUS: PASSED (newly failing = 0, new tests failing = 0)")
    else:
        print("GATE STATUS: FAILED")
    print("=" * 65)

    # Save artifact
    summary = {
        "gatePassed": gate_passed,
        "totalWorkspaceTests": len(current_all_tests),
        "safeTestsExecuted": len(executed_passed) + len(executed_failed),
        "safeTestsPassed": len(executed_passed),
        "safeTestsFailed": len(executed_failed),
        "interactiveTestsSkippedCount": len(interactive_current_tests),
        "interactiveTestsSkipped": sorted(list(interactive_current_tests)),
        "baselineSafePassedExecuted": len(baseline_safe_executed),
        "baselineSafeNewlyFailing": sorted(list(baseline_safe_newly_failing)),
        "newSafeTestsExecuted": len(new_safe_executed),
        "newSafeTestsFailing": sorted(list(new_safe_failing))
    }
    with open(r"C:\Users\Ahmet\Documents\inbrisk\artifacts\fast_gate_summary.json", "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2)

    sys.exit(0 if gate_passed else 1)

if __name__ == "__main__":
    main()
