import scratch_spell as S
import numpy as np

def inspect_bone_rot(path):
    print('=== ROT & WORLD in', path)
    mdata, mflat = S.inspect_file(path)
    mstr = S.parse_strings(mdata, mflat)
    mobjs = S.object_names(mdata, mflat, mstr)
    mb00, mptrs2 = S.build_pointers(mdata, mflat)
    bones = []
    for i, c in enumerate([c for c in mflat if c[0] == 0xCA5E0506]):
        p, = S.struct.unpack('<i', mdata[c[1]:c[1]+4])
        pos = S.struct.unpack('<fff', mdata[c[1]+4:c[1]+16])
        rot = S.struct.unpack('<ffff', mdata[c[1]+16:c[1]+32])
        inv = S.struct.unpack('<16f', mdata[c[1]+32:c[1]+96])
        nm = S.plugin_name(i, mb00, mptrs2, mobjs, f'Bone_{i}')
        bones.append({'id': i, 'p': p, 'pos': pos, 'rot': rot, 'inv': inv, 'name': nm})
    
    for b in bones:
        if any(k in b['name'] for k in ['Thigh', 'Calf', 'Foot', 'Pelvis']):
            mat = np.array(b['inv']).reshape(4,4) # OpenTK is row-major or column-major? In C# row3 is translation
            # In C#, mat.Row3 is (M41, M42, M43)
            # In Granny, InverseBindMatrix stores 16 floats.
            world = np.linalg.inv(mat)
            # Let's check both [3, :3] and [:3, 3]
            pos_row = world[3, :3]
            pos_col = world[:3, 3]
            print(f"{b['id']:2d} {b['name']:18s} rot={tuple(round(x,4) for x in b['rot'])}")
            print(f"    row3=({pos_row[0]:.4f}, {pos_row[1]:.4f}, {pos_row[2]:.4f}) col3=({pos_col[0]:.4f}, {pos_col[1]:.4f}, {pos_col[2]:.4f})")

inspect_bone_rot(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
inspect_bone_rot(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Female_Lod2.grn')
