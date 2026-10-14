import struct, math
from parse_objects_clean import objects, strings, chunks, data

bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]

# Read all walk tracks
def inspect_file(path):
    with open(path, 'rb') as f:
        d = f.read()
    cid, _, data_off, _, _ = struct.unpack('<IIIII', d[0x74:0x74+20])
    sub_count = struct.unpack('<I', d[data_off:data_off+4])[0]
    list_pos = data_off + 16
    ch = []
    for i in range(sub_count):
        lp = list_pos + i * 12
        scid, local_off, _ = struct.unpack('<III', d[lp:lp+12])
        ch.append((scid, data_off + local_off))
    return d, ch

data_walk, walk_chunks = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
str_chunk = [c for c in walk_chunks if c[0] == 0xCA5E0200][0]
count, _ = struct.unpack('<ii', data_walk[str_chunk[1]:str_chunk[1]+8])
pos = str_chunk[1] + 8
walk_strings = []
for _ in range(count):
    end = data_walk.find(b'\x00', pos)
    walk_strings.append(data_walk[pos:end].decode('latin1'))
    pos = end + 1

walk_tracks = []
for c in [c for c in walk_chunks if c[0] == 0xCA5E1204]:
    off = c[1]
    str_idx, = struct.unpack('<i', data_walk[off:off+4])
    name = walk_strings[str_idx] if str_idx < len(walk_strings) else f"str_{str_idx}"
    cP, cR, cS = struct.unpack('<iii', data_walk[off+24:off+36])
    pos_off = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', data_walk[pos_off:pos_off+12]) if cP > 0 else (0,0,0)
    rot_off = pos_off + cP * 12
    r0 = struct.unpack('<ffff', data_walk[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
    walk_tracks.append({'name': name, 'p0': p0, 'r0': r0})

print(f"Total bones: {len(bone_chunks)}, Total tracks: {len(walk_tracks)}")

# For each bone chunk, find best matching track by position and rotation:
for b_idx, c in enumerate(bone_chunks):
    p, = struct.unpack('<i', data[c[1]:c[1]+4])
    b_pos = struct.unpack('<fff', data[c[1]+4:c[1]+16])
    b_rot = struct.unpack('<ffff', data[c[1]+16:c[1]+32])
    
    matches = []
    for t in walk_tracks:
        p_dist = math.sqrt(sum((x-y)**2 for x,y in zip(b_pos, t['p0'])))
        r_dist = math.sqrt(sum((x-y)**2 for x,y in zip(b_rot, t['r0'])))
        score = p_dist * 2.0 + r_dist
        matches.append((score, p_dist, r_dist, t['name']))
    matches.sort(key=lambda x: x[0])
    best = matches[0]
    print(f"Bone {b_idx:2d} (p={p:2d}, pos=({b_pos[0]:.2f},{b_pos[1]:.2f},{b_pos[2]:.2f})) -> Best Track: '{best[3]}' (p_dist={best[1]:.3f}, r_dist={best[2]:.3f})")
