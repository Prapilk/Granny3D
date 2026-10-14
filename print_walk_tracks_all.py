import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn', 'rb') as f:
    d = f.read()

cid, _, data_off, _, _ = struct.unpack('<IIIII', d[0x74:0x74+20])
sub_count = struct.unpack('<I', d[data_off:data_off+4])[0]
list_pos = data_off + 16
chunks = []
for i in range(sub_count):
    lp = list_pos + i * 12
    scid, local_off, _ = struct.unpack('<III', d[lp:lp+12])
    chunks.append((scid, data_off + local_off))

str_chunk = [c for c in chunks if c[0] == 0xCA5E0200][0]
count, _ = struct.unpack('<ii', d[str_chunk[1]:str_chunk[1]+8])
pos = str_chunk[1] + 8
strings = []
for _ in range(count):
    end = d.find(b'\x00', pos)
    strings.append(d[pos:end].decode('latin1'))
    pos = end + 1

objects = []
current_obj = None
for scid, off in chunks:
    if scid == 0xCA5E0F00:
        current_obj = {'name': 'NO_NAME'}
        objects.append(current_obj)
    elif scid == 0xCA5E0F01 and current_obj is not None:
        v0, v1, v2, v3 = struct.unpack('<iiii', d[off:off+16])
        if v0 == 4 and v2 < len(strings):
            current_obj['name'] = strings[v2]

tracks = [c for c in chunks if c[0] == 0xCA5E1204]
print(f"Total tracks: {len(tracks)}, Total objects: {len(objects)}")
for i, c in enumerate(tracks):
    off = c[1]
    h0, = struct.unpack('<i', d[off:off+4])
    cP, cR, cS = struct.unpack('<iii', d[off+24:off+36])
    pos_off = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', d[pos_off:pos_off+12]) if cP > 0 else (0,0,0)
    rot_off = pos_off + cP * 12
    r0 = struct.unpack('<ffff', d[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
    name_obj = objects[h0-1]['name'] if 0 <= h0-1 < len(objects) else f"?({h0})"
    name_str = strings[h0] if 0 <= h0 < len(strings) else f"?str({h0})"
    print(f"Track {i:2d}: h0={h0:2d} (cP={cP:2d}, cR={cR:2d}) -> obj='{name_obj:18s}' | str='{name_str:18s}' | pos=({p0[0]:6.3f}, {p0[1]:6.3f}, {p0[2]:6.3f})")
