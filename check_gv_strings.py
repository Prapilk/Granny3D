with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    data = f.read()

import re
strs = re.findall(b'[A-Za-z0-9_]{3,}', data)
relevant = [s.decode('latin1') for s in strs if any(k in s.lower() for k in [b'mesh_h', b'bip', b'skeleton', b'bone', b'lod2', b'wearable'])]
print("Relevant strings in GrannyViewer.exe:")
for r in relevant[:40]:
    print(" ", r)
