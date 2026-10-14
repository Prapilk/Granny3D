import struct
from scratch_test import parse_grn

anim = parse_grn(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn', 'rb') as f:
    data = f.read()

# Let's inspect tracks in anim['tracks']
print("Total tracks extracted in scratch_test:", len(anim['tracks']))
for name, tr in list(anim['tracks'].items())[:15]:
    p0 = tr['pos'][0] if tr['pos'] else None
    print(f"Track '{name}': p0={p0}")
