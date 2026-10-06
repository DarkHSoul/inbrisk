#!/usr/bin/env python3
"""
tools/synthetic_concurrency_benchmark.py
SYNTHETIC / HEADLESS CONCURRENCY BENCHMARK Runner.
Runs Scenarios A–G (N >= 100 iterations) using dotnet test without live desktop interaction.
Outputs artifacts/synthetic_concurrency_benchmark.json and displays a statistical summary.
"""

import subprocess
import sys
import os
import json

def run_benchmark():
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    print("=" * 70)
    print("  SYNTHETIC / HEADLESS CONCURRENCY BENCHMARK (N = 100)")
    print("=" * 70)
    print("Executing Phase D Scenarios A-G via dotnet test...")

    cmd = [
        "dotnet", "test",
        os.path.join(repo_root, "tests", "Inbrisk.Tests", "Inbrisk.Tests.csproj"),
        "--configuration", "Release",
        "--no-build", "--no-restore",
        "--filter", "FullyQualifiedName~SyntheticConcurrencyBenchmark"
    ]
    res = subprocess.run(cmd, cwd=repo_root, capture_output=True, text=True)
    if res.returncode != 0:
        print("Benchmark run failed!")
        print(res.stdout)
        print(res.stderr)
        return 1

    artifact_path = os.path.join(repo_root, "artifacts", "synthetic_concurrency_benchmark.json")
    if not os.path.exists(artifact_path):
        print(f"Error: {artifact_path} was not generated.")
        return 1

    with open(artifact_path, "r", encoding="utf-8") as f:
        data = json.load(f)

    print("\nBenchmark Results Summary (Scenarios A - G):")
    print("-" * 70)
    for key, val in data.items():
        if isinstance(val, dict):
            print(f"[{key}]")
            for sk, sv in val.items():
                print(f"   {sk}: {sv}")
        else:
            print(f"{key}: {val}")
    print("-" * 70)
    print(f"Saved benchmark artifact to: {artifact_path}")
    print("=" * 70)
    return 0

if __name__ == "__main__":
    sys.exit(run_benchmark())
