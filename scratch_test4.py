from test_full_pipeline import parse_granny_file

torso = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn', 'rb') as f:
    data = f.read()

import struct
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

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]

print("=== Torso Objects ===")
for i, obj in enumerate(objects):
    print(f"Object {i} (1-based {i+1}): '{obj['name']}'")

print("\n=== Torso b00 ===")
for i, obj_1 in enumerate(b00):
    name = objects[obj_1 - 1]['name'] if 0 <= obj_1 - 1 < len(objects) else "OOB"
    print(f"b00[{i}] = {obj_1} -> '{name}'")
