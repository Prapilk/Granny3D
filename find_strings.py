with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'rb') as f:
    data = f.read()

import re
matches = re.findall(b'[A-Za-z0-9_]{3,}', data)
strs = [m.decode('latin1') for m in matches]
print("All strings in H_Male_Lod2:", [s for s in strs if 'Copyright' not in s and 'RAD' not in s][:50])
