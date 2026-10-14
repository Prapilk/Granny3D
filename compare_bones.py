import struct

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

data_body, flat_body = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
body_bones = []
for cid, off in flat_body:
    if cid == 0xCA5E0506:
        p, = struct.unpack('<i', data_body[off:off+4])
        pos = struct.unpack('<fff', data_body[off+4:off+16])
        rot = struct.unpack('<ffff', data_body[off+16:off+32])
        body_bones.append((p, pos, rot))

data_torso, flat_torso = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
torso_bones = []
for cid, off in flat_torso:
    if cid == 0xCA5E0506:
        p, = struct.unpack('<i', data_torso[off:off+4])
        pos = struct.unpack('<fff', data_torso[off+4:off+16])
        rot = struct.unpack('<ffff', data_torso[off+16:off+32])
        torso_bones.append((p, pos, rot))

print(f"Body bones: {len(body_bones)}, Torso bones: {len(torso_bones)}")
for i in range(min(len(body_bones), len(torso_bones))):
    p_b, pos_b, rot_b = body_bones[i]
    p_t, pos_t, rot_t = torso_bones[i]
    pos_match = all(abs(a - b) < 1e-4 for a, b in zip(pos_b, pos_t))
    p_match = (p_b == p_t)
    print(f"Bone {i}: parent(b={p_b}, t={p_t}) pos_match={pos_match} pos_t={pos_t}")
