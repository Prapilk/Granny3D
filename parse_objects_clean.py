import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'rb') as f:
    data = f.read()

def parse_strings(data):
    # string chunk in root 1
    # find CA5E0200
    pos = 0x60
    # Root 1
    # pos 0x60 is Root 0 (20 bytes), pos 0x74 is Root 1
    cid, _, data_off, _, _ = struct.unpack('<IIIII', data[0x74:0x74+20])
    sub_count = struct.unpack('<I', data[data_off:data_off+4])[0]
    list_pos = data_off + 16
    chunks = []
    for i in range(sub_count):
        lp = list_pos + i * 12
        scid, local_off, _ = struct.unpack('<III', data[lp:lp+12])
        chunks.append((scid, data_off + local_off))
    
    str_chunks = [c for c in chunks if c[0] == 0xCA5E0200]
    off = str_chunks[0][1]
    count, total_bytes = struct.unpack('<ii', data[off:off+8])
    pos = off + 8
    strings = []
    for _ in range(count):
        end = data.find(b'\x00', pos)
        strings.append(data[pos:end].decode('latin1'))
        pos = end + 1
    return strings, chunks

strings, chunks = parse_strings(data)
print(f"Strings in Root 1: {len(strings)}")

# Group chunks into objects by 0xCA5E0F00
objects = []
current_obj = None

for scid, off in chunks:
    if scid == 0xCA5E0F00:
        current_obj = {'props': []}
        objects.append(current_obj)
    elif scid == 0xCA5E0F01 and current_obj is not None:
        v0, v1, v2, v3 = struct.unpack('<iiii', data[off:off+16])
        current_obj['props'].append((v0, v1, v2, v3))

print(f"Found {len(objects)} objects in Root 1:")
for i, obj in enumerate(objects):
    names = [strings[p[2]] for p in obj['props'] if p[0] == 4]
    name = names[0] if names else "NO_NAME"
    print(f"  Object {i} (1-based {i+1}): '{name}' (props count: {len(obj['props'])})")
