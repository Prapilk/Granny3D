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

for i, (cid, off) in enumerate(flat_body):
    sample = data_body[off:off+100]
    if b'Bip01' in sample or b'Spine' in sample or b'Pelvis' in sample:
        print(f"Body Chunk {i}: ID={hex(cid)}, offset={hex(off)}, sample={sample[:40]}")

data_torso, flat_torso = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
for i, (cid, off) in enumerate(flat_torso):
    sample = data_torso[off:off+100]
    if b'Bip01' in sample or b'Spine' in sample or b'Pelvis' in sample:
        print(f"Torso Chunk {i}: ID={hex(cid)}, offset={hex(off)}, sample={sample[:40]}")
