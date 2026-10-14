import struct

def show_bones(path):
    print("=== Bones for:", path)
    from scratch_test import parse_grn
    m = parse_grn(path)
    for b in m["bones"]:
        pname = m["bones"][b["parent"]]["name"] if 0 <= b["parent"] < len(m["bones"]) else "ROOT"
        print(f"[{b['id']}] {b['name']} (parent: {b['parent']} -> {pname}) pos={b['pos']}")

show_bones(r"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn")
show_bones(r"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn")
