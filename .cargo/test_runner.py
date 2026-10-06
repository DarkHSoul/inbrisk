import os
import sys
import subprocess
import shutil
import tempfile

exe = sys.argv[1]
args = sys.argv[2:]

# Run via a clean temporary copy to bypass Windows PCA/AppCompat path-level shimming on the development folder
temp_dir = tempfile.mkdtemp(prefix="inbrisk_test_")
try:
    exe_name = os.path.basename(exe)
    temp_exe = os.path.join(temp_dir, exe_name)
    shutil.copyfile(exe, temp_exe)
    res = subprocess.run([temp_exe] + args)
    sys.exit(res.returncode)
finally:
    shutil.rmtree(temp_dir, ignore_errors=True)
