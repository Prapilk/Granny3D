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

data_anim, flat_anim = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Animals\Birds_Eagle_Attack1.grn')

str_chunk = [c for c in flat_anim if c[0] == 0xCA5E0200 and c[1] > 0x1000]
if str_chunk:
    off = str_chunk[0][1]
    count, _ = struct.unpack('<ii', data_anim[off:off+8])
    pos = off + 8
    strings = []
    for _ in range(count):
        end = data_anim.find(b'\x00', pos)
        strings.append(data_anim[pos:end].decode('latin1'))
        pos = end + 1
    print(f"Anim strings count: {len(strings)}")
    for i, s in enumerate(strings):
        print(f"  [{i}]: '{s}'")
