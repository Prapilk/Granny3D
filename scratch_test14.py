import struct

# Let's inspect objects in H_Male_Lod2 and H_Male_Walk_01
def get_obj_names(path):
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
    
    # parse second string chunk
    str_chunks = [c for c in chunks if c[0] == 0xCA5E0200]
    sc = str_chunks[-1]
    count, _ = struct.unpack('<ii', data[sc[1]:sc[1]+8])
    pos = sc[1] + 8
    strings = []
    for _ in range(count):
        end = data.find(b'\x00', pos)
        strings.append(data[pos:end].decode('latin1'))
        pos = end + 1

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
    return objects, data, chunks

objs_m, data_m, chunks_m = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
objs_a, data_a, chunks_a = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

print("=== Model Objects ===")
for i, o in enumerate(objs_m):
    print(f"[{i:2d} | 1-based {i+1:2d}] {o['name']}")

print("\n=== Anim Objects ===")
for i, o in enumerate(objs_a):
    print(f"[{i:2d} | 1-based {i+1:2d}] {o['name']}")
