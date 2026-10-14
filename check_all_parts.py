import struct, os

def inspect_file(path):
    with open(path, 'rb') as f:
        data = f.read()
    num_roots = struct.unpack('<I', data[0x44:0x48])[0]
    flat = []
    pos = 0x60
    for r in range(num_roots):
        cid, _, data_off, _, _ = struct.unpack('<IIIII', data[pos:pos+20])
        pos += 20
        sub_count = struct.unpack('<I', data[data_off:data_off+4])[0]
        list_pos = data_off + 16
        for i in range(sub_count):
            lp = list_pos + i * 12
            scid, local_off, _ = struct.unpack('<III', data[lp:lp+12])
            flat.append((scid, data_off + local_off))
    return data, flat

parts = [
    "H_Male_Torso_V2_LOD2.grn", "H_Male_UArms_V2_LOD2.grn", "H_Male_FArms_V2_LOD2.grn",
    "H_Male_Hands_V2_LOD2.grn", "H_Male_ULegs_V2_LOD2.grn", "H_Male_LLegs_V2_LOD2.grn",
    "H_Male_Head_V2_LOD2.grn", "H_Male_Face_01_V2_LOD2.grn",
    "H_Male_Neck_V2_LOD2.grn", "H_Male_Pelvis_V2_LOD2.grn", "H_Male_Feet_V2_LOD2.grn"
]
others = r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others'

for p in parts:
    fpath = os.path.join(others, p)
    if not os.path.exists(fpath):
        print(f"NOT FOUND: {p}")
        continue
    data, flat = inspect_file(fpath)
    c0a = [struct.unpack('<i', data[off:off+4])[0] for cid, off in flat if cid == 0xCA5E0C0A]
    skins = [off for cid, off in flat if cid == 0xCA5E0702]
    max_b = -1
    if skins:
        off = skins[0]
        num_v, h1, h2 = struct.unpack('<iii', data[off:off+12])
        p_ptr = off + 12
        for _ in range(num_v):
            inf_count, = struct.unpack('<i', data[p_ptr:p_ptr+4])
            p_ptr += 4
            for _ in range(inf_count):
                b_idx, _ = struct.unpack('<if', data[p_ptr:p_ptr+8])
                p_ptr += 8
                if b_idx > max_b: max_b = b_idx
    print(f"{p}: c0a={len(c0a)}, max_b={max_b}, c0a_list={c0a}")
