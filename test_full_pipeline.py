import struct, math

# 1. Parse Mesh File
def parse_granny_file(path):
    with open(path, 'rb') as f:
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
    str_chunks = [c for c in chunks if c[0] == 0xCA5E0200]
    strings = []
    if str_chunks:
        off = str_chunks[0][1]
        count, _ = struct.unpack('<ii', data[off:off+8])
        pos = off + 8
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

    # Bone mapping (b00)
    b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]

    # Bone chunks (0xCA5E0506)
    bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]
    bones = []
    for i, c in enumerate(bone_chunks):
        p, = struct.unpack('<i', data[c[1]:c[1]+4])
        pos = struct.unpack('<fff', data[c[1]+4:c[1]+16])
        rot = struct.unpack('<ffff', data[c[1]+16:c[1]+32])
        name = f"Bone_{i}"
        if i < len(b00):
            obj_idx = b00[i] - 1
            if 0 <= obj_idx < len(objects):
                name = objects[obj_idx]['name']
        bones.append({'id': i, 'parent': p, 'pos': pos, 'rot': rot, 'name': name})

    # Animation tracks (0xCA5E1204)
    track_chunks = [c for c in chunks if c[0] == 0xCA5E1204]
    tracks = {}
    for c in track_chunks:
        off = c[1]
        obj_idx_1, = struct.unpack('<i', data[off:off+4])
        name = f"Track_{obj_idx_1}"
        if 0 <= obj_idx_1 - 1 < len(objects):
            name = objects[obj_idx_1 - 1]['name']
        cP, cR, cS = struct.unpack('<iii', data[off+24:off+36])
        pos_off = off + 52 + (cP + cR + cS) * 4
        p0 = struct.unpack('<fff', data[pos_off:pos_off+12]) if cP > 0 else (0,0,0)
        rot_off = pos_off + cP * 12
        r0 = struct.unpack('<ffff', data[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
        tracks[name] = {'p0': p0, 'r0': r0}

    # Skinning & Vertices
    pts_chunk = [c for c in chunks if c[0] == 0xCA5E0801]
    skin_chunk = [c for c in chunks if c[0] == 0xCA5E0702]
    c0a = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0C0A]

    return {
        'data': data,
        'bones': bones,
        'tracks': tracks,
        'pts_chunk': pts_chunk[0] if pts_chunk else None,
        'skin_chunk': skin_chunk[0] if skin_chunk else None,
        'c0a': c0a
    }

print("Parsing H_Male_Lod2...")
mesh = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
print("Parsing H_Male_Walk_01...")
anim = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

print(f"\nMesh bones count: {len(mesh['bones'])}")
print(f"Anim tracks count: {len(anim['tracks'])}")

matched = [b for b in mesh['bones'] if b['name'] in anim['tracks']]
print(f"Bones matched to animation: {len(matched)} / {len(mesh['bones'])}")
for b in mesh['bones']:
    status = "OK" if b['name'] in anim['tracks'] else "REST"
    print(f"  [{status:4s}] Bone {b['id']:2d} (p={b['parent']:2d}): '{b['name']}'")
