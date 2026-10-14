from test_clean_deform import parse_granny_file
import struct

anim = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
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

# Strings
str_chunk = [c for c in chunks if c[0] == 0xCA5E0200][0]
count, _ = struct.unpack('<ii', data[str_chunk[1]:str_chunk[1]+8])
pos = str_chunk[1] + 8
strings = []
for _ in range(count):
    end = data.find(b'\x00', pos)
    strings.append(data[pos:end].decode('latin1'))
    pos = end + 1

# Objects
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

track_chunks = [c for c in chunks if c[0] == 0xCA5E1204]
print(f"Total track chunks: {len(track_chunks)}")
for i, c in enumerate(track_chunks):
    off = c[1]
    h0, = struct.unpack('<i', data[off:off+4])
    cP, cR, cS = struct.unpack('<iii', data[off+24:off+36])
    pos_off = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', data[pos_off:pos_off+12]) if cP > 0 else (0,0,0)
    # What is h0?
    obj_name = objects[h0 - 1]['name'] if 0 <= h0 - 1 < len(objects) else "OOB"
    str_name = strings[h0] if 0 <= h0 < len(strings) else "OOB"
    print(f"Track {i:2d}: h0={h0:2d} -> ObjName='{obj_name:20s}' | StrName='{str_name:20s}' | p0=({p0[0]:.2f}, {p0[1]:.2f}, {p0[2]:.2f})")
