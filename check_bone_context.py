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
    if cid == 0xCA5E0506:
        # Check 3 chunks before and 3 after
        for k in range(max(0, i-3), min(len(flat_body), i+5)):
            c_id, c_off = flat_body[k]
            ints = struct.unpack('<iiii', data_body[c_off:c_off+16])
            print(f"  Chunk {k}: ID={hex(c_id)}, offset={hex(c_off)}, ints={ints}")
        break
