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

data_model, flat_model = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Animals\Birds_Eagle_Lod2.grn')
model_strings = parse_strings(data_model, flat_model)
model_b00 = [struct.unpack('<i', data_model[c[1]:c[1]+4])[0] for c in flat_model if c[0] == 0xCA5E0B00]

bone_names = [model_strings[idx] if idx < len(model_strings) else f"bone_{i}" for i, idx in enumerate(model_b00)]
print("Eagle Bone Names:")
for i, name in enumerate(bone_names):
    print(f"  Bone {i}: '{name}'")

data_anim, flat_anim = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Animals\Birds_Eagle_Attack1.grn')
anim_strings = parse_strings(data_anim, flat_anim)
track_chunks = [c for c in flat_anim if c[0] == 0xCA5E1204]

anim_tracks = {}
for c in track_chunks:
    off = c[1]
    str_idx, = struct.unpack('<i', data_anim[off:off+4])
    track_name = anim_strings[str_idx] if str_idx < len(anim_strings) else f"str_{str_idx}"
    anim_tracks[track_name] = off

matched = 0
for i, b_name in enumerate(bone_names):
    if b_name in anim_tracks:
        matched += 1
print(f"\nMatched {matched} / {len(bone_names)} eagle bones by name!")
