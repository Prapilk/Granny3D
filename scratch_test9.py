from test_clean_deform import parse_granny_file
import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn', 'rb') as f:
    data = f.read()

cid, _, data_off, _, _ = struct.unpack('<IIIII', data[0x74:0x74+20])
sub_count = struct.unpack('<I', data[data_off:data_off+4])[0]
list_pos = data_off + 16
chunks = []
for i in range(sub_count):
    lp = list_pos + i * 12
    scid, local_off, _ = struct.unpack('<III', data[lp:lp+12])
    chunks.append((scid, data_off + local_off))

str_chunk = [c for c in chunks if c[0] == 0xCA5E0200][0]
count, _ = struct.unpack('<ii', data[str_chunk[1]:str_chunk[1]+8])
pos = str_chunk[1] + 8
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

print("=== Anim Bone Names via b00 ===")
for i in range(len(b00)):
    obj_idx_1 = b00[i]
    name = objects[obj_idx_1 - 1]['name'] if 0 <= obj_idx_1 - 1 < len(objects) else "OOB"
    print(f"Track/Bone {i:2d} -> b00={obj_idx_1:2d} -> '{name}'")
