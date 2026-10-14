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

data_torso, flat_torso = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
data_walk, flat_walk = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
data_body, flat_body = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')

# Check bones in H_Male_Lod2
bones = []
for cid, off in flat_body:
    if cid == 0xCA5E0506:
        p, = struct.unpack('<i', data_body[off:off+4])
        pos = struct.unpack('<fff', data_body[off+4:off+16])
        rot = struct.unpack('<ffff', data_body[off+16:off+32])
        bones.append((len(bones), p, pos, rot))

print(f"H_Male_Lod2 total bones: {len(bones)}")
for b in bones[:8]:
    print(f"  Bone {b[0]}: parent={b[1]}, pos={b[2]}")

# Check tracks in H_Male_Walk_01
tracks = []
for cid, off in flat_walk:
    if cid == 0xCA5E1204:
        bone_id, = struct.unpack('<i', data_walk[off:off+4])
        cP, cR, cS = struct.unpack('<iii', data_walk[off+24:off+36])
        tracks.append((bone_id, cP, cR, cS))

print(f"H_Male_Walk_01 total tracks: {len(tracks)}")
print("  Track bone IDs:", [t[0] for t in tracks[:15]])

# Check skin in Torso
for cid, off in flat_torso:
    if cid == 0xCA5E0702:
        num_v, h1, h2 = struct.unpack('<iii', data_torso[off:off+12])
        print(f"Torso skin chunk: num_v={num_v}, h1={h1}, h2={h2}")
        p = off + 12
        sample_infs = []
        for v in range(min(5, num_v)):
            inf_count, = struct.unpack('<i', data_torso[p:p+4])
            p += 4
            infs = []
            for _ in range(inf_count):
                b_idx, weight = struct.unpack('<if', data_torso[p:p+8])
                p += 8
                infs.append((b_idx, weight))
            sample_infs.append((v, infs))
        print("  Sample vertex influences (local bone idx, weight):", sample_infs)

# Check c0a in Torso
c0a = []
for cid, off in flat_torso:
    if cid == 0xCA5E0C0A:
        val, = struct.unpack('<i', data_torso[off:off+4])
        c0a.append(val)
print("Torso c0a map:", c0a)
