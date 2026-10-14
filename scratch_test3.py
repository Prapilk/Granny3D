import struct
from test_full_pipeline import parse_granny_file

torso = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
male = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
anim = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

print("=== Torso Bones ===")
for b in torso['bones']:
    print(f"[{b['id']:2d}] p={b['parent']:2d} name='{b['name']}' pos={b['pos']}")

print("\n=== Male Bones ===")
for b in male['bones']:
    print(f"[{b['id']:2d}] p={b['parent']:2d} name='{b['name']}' pos={b['pos']}")
