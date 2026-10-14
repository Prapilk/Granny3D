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

data_body, flat_body = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
body_strings = parse_strings(data_body, flat_body)

for i in [2, 3, 4, 5, 6, 7]:
    cid, off = [c for c in flat_body if c[0] == 0xCA5E0506][i]
    raw = data_body[off:off+68]
    ints = struct.unpack('<' + 'i'*17, raw)
    print(f"Bone {i}:")
    print(f"  Parent: {ints[0]}")
    print(f"  Pos: {struct.unpack('<fff', raw[4:16])}")
    print(f"  Rot: {struct.unpack('<ffff', raw[16:32])}")
    mat3 = struct.unpack('<fffffffff', raw[32:68])
    print(f"  Mat3: {mat3}")
