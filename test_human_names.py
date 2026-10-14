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

def parse_strings(data, flat):
    chunks = [c for c in flat if c[0] == 0xCA5E0200 and c[1] > 0x1000]
    if not chunks:
        chunks = [c for c in flat if c[0] == 0xCA5E0200]
    if not chunks: return []
    off = chunks[-1][1]
    count, _ = struct.unpack('<ii', data[off:off+8])
    pos = off + 8
    strings = []
    for _ in range(count):
        end = data.find(b'\x00', pos)
        strings.append(data[pos:end].decode('latin1'))
        pos = end + 1
    return strings

data_body, flat_body = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
body_strings = parse_strings(data_body, flat_body)
body_b00 = [struct.unpack('<i', data_body[c[1]:c[1]+4])[0] for c in flat_body if c[0] == 0xCA5E0B00]
body_bone_names = [body_strings[idx] if idx < len(body_strings) else f"bone_{i}" for i, idx in enumerate(body_b00)]

print("H_Male_Lod2 Bone Names (sample):")
for i, name in enumerate(body_bone_names[:20]):
    print(f"  Bone {i}: '{name}'")

data_torso, flat_torso = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
torso_strings = parse_strings(data_torso, flat_torso)
torso_b00 = [struct.unpack('<i', data_torso[c[1]:c[1]+4])[0] for c in flat_torso if c[0] == 0xCA5E0B00]
torso_bone_names = [torso_strings[idx] if idx < len(torso_strings) else f"bone_{i}" for i, idx in enumerate(torso_b00)]

print("\nTorso Bone Names:")
for i, name in enumerate(torso_bone_names):
    print(f"  Torso Bone {i}: '{name}'")
