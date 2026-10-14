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

data_torso, flat_torso = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
torso_bones = []
for cid, off in flat_torso:
    if cid == 0xCA5E0506:
        p, = struct.unpack('<i', data_torso[off:off+4])
        pos = struct.unpack('<fff', data_torso[off+4:off+16])
        rot = struct.unpack('<ffff', data_torso[off+16:off+32])
        torso_bones.append((p, pos, rot))

for i, (p, pos, rot) in enumerate(torso_bones):
    print(f"Torso bone {i}: parent={p}, pos={pos}")
