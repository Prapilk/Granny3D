import struct
from parse_objects_clean import strings, chunks, data

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]

# Objects
objects = []
current_obj = None
for scid, off in chunks:
    if scid == 0xCA5E0F00:
        current_obj = {'props': []}
        objects.append(current_obj)
    elif scid == 0xCA5E0F01 and current_obj is not None:
        v0, v1, v2, v3 = struct.unpack('<iiii', data[off:off+16])
        current_obj['props'].append((v0, v1, v2, v3))

bone_names = []
for i, obj_idx_1 in enumerate(b00):
    obj_idx = obj_idx_1 - 1
    name = f"Bone_{i}"
    if 0 <= obj_idx < len(objects):
        for p in objects[obj_idx]['props']:
            if p[0] == 4:
                name = strings[p[2]]
                break
    bone_names.append(name)

# Now inspect animation
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

walk_tracks = {}
for c in [c for c in walk_chunks if c[0] == 0xCA5E1204]:
    off = c[1]
    str_idx, = struct.unpack('<i', data_walk[off:off+4])
    name = walk_strings[str_idx] if str_idx < len(walk_strings) else f"str_{str_idx}"
    cP, cR, cS = struct.unpack('<iii', data_walk[off+24:off+36])
    pos_off = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', data_walk[pos_off:pos_off+12]) if cP > 0 else (0,0,0)
    rot_off = pos_off + cP * 12
    r0 = struct.unpack('<ffff', data_walk[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
    walk_tracks[name] = (p0, r0)

print("\n--- Comparing Bind Pose vs Walk Pose with CLEAN bone names ---")
match_count = 0
for i, c in enumerate(bone_chunks):
    p, = struct.unpack('<i', data[c[1]:c[1]+4])
    pos = struct.unpack('<fff', data[c[1]+4:c[1]+16])
    rot = struct.unpack('<ffff', data[c[1]+16:c[1]+32])
    name = bone_names[i]
    if name in walk_tracks:
        match_count += 1
        p0, r0 = walk_tracks[name]
        pos_dist = sum((a-b)**2 for a, b in zip(pos, p0))**0.5
        rot_dist = sum((a-b)**2 for a, b in zip(rot, r0))**0.5
        print(f"Bone {i} '{name}': Bind pos=({pos[0]:.3f}, {pos[1]:.3f}, {pos[2]:.3f}) | Track pos=({p0[0]:.3f}, {p0[1]:.3f}, {p0[2]:.3f}) -> dist={pos_dist:.3f}, rot_dist={rot_dist:.3f}")

print(f"\nTotal matched bones: {match_count} / {len(bone_chunks)}")
