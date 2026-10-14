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

data_male, flat_male = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
data_walk, flat_walk = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
walk_strings = parse_strings(data_walk, flat_walk)

# Compare bone bind pos/rot vs track pos/rot for matching bones
from test_male_sim import parse_bone_names
male_bone_names = parse_bone_names(data_male, flat_male)

bone_chunks = [c for c in flat_male if c[0] == 0xCA5E0506]
bones = {}
for i, c in enumerate(bone_chunks):
    p, = struct.unpack('<i', data_male[c[1]:c[1]+4])
    pos = struct.unpack('<fff', data_male[c[1]+4:c[1]+16])
    rot = struct.unpack('<ffff', data_male[c[1]+16:c[1]+32])
    name = male_bone_names[i]
    bones[name] = {'parent': p, 'pos': pos, 'rot': rot}

track_chunks = [c for c in flat_walk if c[0] == 0xCA5E1204]
for c in track_chunks:
    off = c[1]
    str_idx, = struct.unpack('<i', data_walk[off:off+4])
    name = walk_strings[str_idx] if str_idx < len(walk_strings) else f"str_{str_idx}"
    cP, cR, cS = struct.unpack('<iii', data_walk[off+24:off+36])
    pos_off = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', data_walk[pos_off:pos_off+12]) if cP > 0 else (0,0,0)
    rot_off = pos_off + cP * 12
    r0 = struct.unpack('<ffff', data_walk[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
    if name in bones:
        b = bones[name]
        print(f"Bone '{name}':")
        print(f"  Bind:  pos=({b['pos'][0]:.3f}, {b['pos'][1]:.3f}, {b['pos'][2]:.3f}) rot=({b['rot'][0]:.3f}, {b['rot'][1]:.3f}, {b['rot'][2]:.3f}, {b['rot'][3]:.3f})")
        print(f"  Track: pos=({p0[0]:.3f}, {p0[1]:.3f}, {p0[2]:.3f}) rot=({r0[0]:.3f}, {r0[1]:.3f}, {r0[2]:.3f}, {r0[3]:.3f}) (cP={cP}, cR={cR})")
