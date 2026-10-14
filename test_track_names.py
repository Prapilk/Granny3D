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

data_anim, flat_anim = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Animals\Birds_Eagle_Attack1.grn')
anim_strings = parse_strings(data_anim, flat_anim)

tracks = [c for c in flat_anim if c[0] == 0xCA5E1204]
print(f"Eagle attack tracks: {len(tracks)}")
for i, (cid, off) in enumerate(tracks[:15]):
    str_idx, = struct.unpack('<i', data_anim[off:off+4])
    s = anim_strings[str_idx] if str_idx < len(anim_strings) else f"??({str_idx})"
    print(f"  Track {i}: str_idx={str_idx} -> Bone Name='{s}'")

data_walk, flat_walk = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
walk_strings = parse_strings(data_walk, flat_walk)
walk_tracks = [c for c in flat_walk if c[0] == 0xCA5E1204]
print(f"\nH_Male_Walk tracks: {len(walk_tracks)}")
for i, (cid, off) in enumerate(walk_tracks[:15]):
    str_idx, = struct.unpack('<i', data_walk[off:off+4])
    s = walk_strings[str_idx] if str_idx < len(walk_strings) else f"??({str_idx})"
    print(f"  Track {i}: str_idx={str_idx} -> Bone Name='{s}'")
