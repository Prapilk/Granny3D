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

data_torso, flat_torso = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
torso_strings = parse_strings(data_torso, flat_torso)

f00 = [c for c in flat_torso if c[0] == 0xCA5E0F00]
f01 = [c for c in flat_torso if c[0] == 0xCA5E0F01]

object_names = []
for c in f01:
    v0, v1, v2, v3 = struct.unpack('<iiii', data_torso[c[1]:c[1]+16])
    if v0 == 4:
        object_names.append(torso_strings[v2])

print(f"Total object names: {len(object_names)}")
for i, name in enumerate(object_names):
    print(f"  Object {i}: '{name}'")

b00 = [struct.unpack('<i', data_torso[c[1]:c[1]+4])[0] for c in flat_torso if c[0] == 0xCA5E0B00]
print("\nNow mapping bones 0..24 to object names:")
for bone_idx, obj_idx in enumerate(b00):
    name_0 = object_names[obj_idx] if obj_idx < len(object_names) else f"?({obj_idx})"
    name_1 = object_names[obj_idx - 1] if 0 <= obj_idx - 1 < len(object_names) else f"?({obj_idx-1})"
    print(f"  Bone {bone_idx} (obj {obj_idx}): 0-based='{name_0}' | 1-based='{name_1}'")
