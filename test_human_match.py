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

def load_skeleton_and_names(path):
    data, flat = inspect_file(path)
    strings = parse_strings(data, flat)
    b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in flat if c[0] == 0xCA5E0B00]
    bone_chunks = [c for c in flat if c[0] == 0xCA5E0506]
    bones = []
    for i, c in enumerate(bone_chunks):
        p, = struct.unpack('<i', data[c[1]:c[1]+4])
        pos = struct.unpack('<fff', data[c[1]+4:c[1]+16])
        rot = struct.unpack('<ffff', data[c[1]+16:c[1]+32])
        name = strings[b00[i]] if i < len(b00) and b00[i] < len(strings) else f"bone_{i}"
        bones.append({'parent': p, 'pos': pos, 'rot': rot, 'name': name})
    return bones

def load_anim_tracks(path):
    data, flat = inspect_file(path)
    strings = parse_strings(data, flat)
    track_chunks = [c for c in flat if c[0] == 0xCA5E1204]
    tracks = {}
    for c in track_chunks:
        off = c[1]
        str_idx, = struct.unpack('<i', data[off:off+4])
        name = strings[str_idx] if str_idx < len(strings) else f"str_{str_idx}"
        tracks[name] = off
    return tracks

male_bones = load_skeleton_and_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
torso_bones = load_skeleton_and_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
walk_tracks = load_anim_tracks(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

print(f"H_Male_Lod2: {len(male_bones)} bones, Walk tracks: {len(walk_tracks)}")
matched_male = sum(1 for b in male_bones if b['name'] in walk_tracks)
print(f"Matched {matched_male} / {len(male_bones)} H_Male bones by name!")

print(f"\nTorso: {len(torso_bones)} bones, Walk tracks: {len(walk_tracks)}")
matched_torso = sum(1 for b in torso_bones if b['name'] in walk_tracks)
print(f"Matched {matched_torso} / {len(torso_bones)} Torso bones by name!")
for b in torso_bones:
    has = "YES" if b['name'] in walk_tracks else " NO"
    print(f"  [{has}] Torso Bone: '{b['name']}'")
