import xml.etree.ElementTree as ET
import json
import os
import shutil

src_trx = r'C:\Users\Ahmet\Documents\inbrisk-baseline\tests\Inbrisk.Tests\TestResults\baseline_clean_4271b3f.trx'
dst_dir = r'C:\Users\Ahmet\Documents\inbrisk\artifacts'
os.makedirs(dst_dir, exist_ok=True)
dst_trx = os.path.join(dst_dir, 'baseline_clean_4271b3f.trx')
shutil.copyfile(src_trx, dst_trx)

tree = ET.parse(dst_trx)
root = tree.getroot()
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

passed = []
failed = []

for r in root.findall('.//t:UnitTestResult', ns):
    test_name = r.get('testName')
    outcome = r.get('outcome')
    if outcome == 'Passed':
        passed.append(test_name)
    elif outcome == 'Failed':
        failed.append(test_name)

manifest = {
    'commit': '4271b3f',
    'total': len(passed) + len(failed),
    'passedCount': len(passed),
    'failedCount': len(failed),
    'passed': sorted(passed),
    'knownBaselineFailures': sorted(failed)
}

manifest_path = os.path.join(dst_dir, 'baseline_manifest_4271b3f.json')
with open(manifest_path, 'w', encoding='utf-8') as f:
    json.dump(manifest, f, indent=2)

print(f"Saved baseline manifest to {manifest_path}")
print(f"Total: {manifest['total']}, Passed: {manifest['passedCount']}, KnownBaselineFailures: {manifest['failedCount']}")
