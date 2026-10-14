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

# 1. Parse string table (0xCA5E0200 with off > 0x1000)
str_chunk = [c for c in flat_body if c[0] == 0xCA5E0200 and c[1] > 0x1000][0]
off = str_chunk[1]
str_count, _ = struct.unpack('<ii', data_body[off:off+8])
pos = off + 8
strings = []
for _ in range(str_count):
    end = data_body.find(b'\x00', pos)
    strings.append(data_body[pos:end].decode('latin1'))
    pos = end + 1

# 2. Parse f00 (objects) and f01 (properties)
f00 = [c for c in flat_body if c[0] == 0xCA5E0F00]
f01 = [c for c in flat_body if c[0] == 0xCA5E0F01]

names_list = []
for c in f01:
    v0, v1, v2, v3 = struct.unpack('<iiii', data_body[c[1]:c[1]+16])
    if v0 == 4 and v2 < len(strings):
        names_list.append(strings[v2])

print(f"Total bone/object names found in f01: {len(names_list)}")

# Now check body_b00
body_b00 = [struct.unpack('<i', data_body[off:off+4])[0] for cid, off in flat_body if cid == 0xCA5E0B00]
for bone_idx, obj_idx in enumerate(body_b00):
    name = names_list[obj_idx] if obj_idx < len(names_list) else f"Unknown({obj_idx})"
    print(f"Bone {bone_idx} (obj {obj_idx}): '{name}'")
