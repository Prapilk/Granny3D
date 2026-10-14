import struct
from test_full_pipeline import parse_granny_file

mesh = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'rb') as f:
    data = f.read()

cid, _, data_off, _, _ = struct.unpack('<IIIII', data[0x74:0x74+20])
sub_count = struct.unpack('<I', data[data_off:data_off+4])[0]
list_pos = data_off + 16
chunks = []
for i in range(sub_count):
    lp = list_pos + i * 12
    scid, local_off, _ = struct.unpack('<III', data[lp:lp+12])
    chunks.append((scid, data_off + local_off))

str_chunks = [c for c in chunks if c[0] == 0xCA5E0200]
count, _ = struct.unpack('<ii', data[str_chunks[0][1]:str_chunks[0][1]+8])
pos = str_chunks[0][1] + 8
strings = []
for _ in range(count):
    end = data.find(b'\x00', pos)
    strings.append(data[pos:end].decode('latin1'))
    pos = end + 1

objects = []
current_obj = None
for scid, off in chunks:
    if scid == 0xCA5E0F00:
        current_obj = {'name': 'NO_NAME'}
        objects.append(current_obj)
    elif scid == 0xCA5E0F01 and current_obj is not None:
        v0, v1, v2, v3 = struct.unpack('<iiii', data[off:off+16])
        if v0 == 4 and v2 < len(strings):
            current_obj['name'] = strings[v2]

c0a = mesh['c0a']
print("\nc0a mapping when treated as 1-based object indices:")
for local_b, obj_idx_1 in enumerate(c0a):
    obj_idx = obj_idx_1 - 1
    name = objects[obj_idx]['name'] if 0 <= obj_idx < len(objects) else "OUT_OF_BOUNDS"
    # Find which bone has this name
    bone_matches = [b['id'] for b in mesh['bones'] if b['name'] == name]
    print(f"  local {local_b} -> obj {obj_idx_1}: '{name}' (maps to Bone {bone_matches})")
