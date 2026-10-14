import struct, os

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

def parse_bone_names(path):
    data, flat = inspect_file(path)
    strings = parse_strings(data, flat)
    
    # 0xCA5E0F01 chunks have object properties. type 4 = object name
    f01 = [c for c in flat if c[0] == 0xCA5E0F01]
    obj_names = []
    for c in f01:
        v0, v1, v2, v3 = struct.unpack('<iiii', data[c[1]:c[1]+16])
        if v0 == 4:
            s = strings[v2] if v2 < len(strings) else f"str_{v2}"
            obj_names.append(s)
            
    # 0xCA5E0B00 chunks map bone -> obj_idx (1-based)
    b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in flat if c[0] == 0xCA5E0B00]
    bone_chunks = [c for c in flat if c[0] == 0xCA5E0506]
    
    bone_names = []
    for i in range(len(bone_chunks)):
        if i < len(b00):
            obj_idx = b00[i] - 1 # 1-based to 0-based
            if 0 <= obj_idx < len(obj_names):
                bone_names.append(obj_names[obj_idx])
            else:
                bone_names.append(f"obj_{b00[i]}")
        else:
            bone_names.append(f"bone_{i}")
    return bone_names

def parse_anim_tracks(path):
    data, flat = inspect_file(path)
    strings = parse_strings(data, flat)
    track_chunks = [c for c in flat if c[0] == 0xCA5E1204]
    track_names = []
    for c in track_chunks:
        off = c[1]
        str_idx, = struct.unpack('<i', data[off:off+4])
        s = strings[str_idx] if str_idx < len(strings) else f"str_{str_idx}"
        track_names.append(s)
    return track_names

print("--- H_Male_Lod2.grn ---")
male_bones = parse_bone_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
for i, b in enumerate(male_bones[:10]):
    print(f"  Bone {i}: {b}")
print(f"Total male bones: {len(male_bones)}")

print("\n--- H_Male_Walk_01.grn ---")
walk_tracks = parse_anim_tracks(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
for i, t in enumerate(walk_tracks[:10]):
    print(f"  Track {i}: {t}")
print(f"Total walk tracks: {len(walk_tracks)}")

male_match = sum(1 for b in male_bones if b in walk_tracks)
print(f"H_Male matches: {male_match} / {len(male_bones)}")

print("\n--- H_Male_Torso_V2_LOD2.grn ---")
torso_bones = parse_bone_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
for i, b in enumerate(torso_bones[:10]):
    print(f"  Bone {i}: {b}")
print(f"Total torso bones: {len(torso_bones)}")

torso_match = sum(1 for b in torso_bones if b in walk_tracks)
print(f"Torso matches: {torso_match} / {len(torso_bones)}")
