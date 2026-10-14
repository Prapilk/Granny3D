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

data_torso, flat_torso = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')

for cid, off in flat_torso:
    if cid == 0xCA5E0200 and off > 0x1000:
        count, total_bytes = struct.unpack('<ii', data_torso[off:off+8])
        print(f"Torso strings count: {count}, total_bytes: {total_bytes}")
        pos = off + 8
        strings = []
        for i in range(count):
            end = data_torso.find(b'\x00', pos)
            s = data_torso[pos:end].decode('latin1')
            strings.append((i, s))
            pos = end + 1

        for idx, s in strings[:30]:
            print(f"  [{idx}]: '{s}'")
