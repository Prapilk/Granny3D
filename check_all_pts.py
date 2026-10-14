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

data_body, flat_body = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
pts = [c for c in flat_body if c[0] == 0xCA5E0801]
pols = [c for c in flat_body if c[0] == 0xCA5E0901]
skins = [c for c in flat_body if c[0] == 0xCA5E0702]
bones = [c for c in flat_body if c[0] == 0xCA5E0506]

print(f"H_Male_Lod2: pts={len(pts)}, pols={len(pols)}, skins={len(skins)}, bones={len(bones)}")
for i, (cid, off) in enumerate(pts):
    # let's find size
    print(f"  pts chunk {i} at {hex(off)}")
