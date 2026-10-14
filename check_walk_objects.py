import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn', 'rb') as f:
    d = f.read()

cid, _, data_off, _, _ = struct.unpack('<IIIII', d[0x74:0x74+20])
sub_count = struct.unpack('<I', d[data_off:data_off+4])[0]
list_pos = data_off + 16
chunks = []
for i in range(sub_count):
    lp = list_pos + i * 12
    scid, local_off, _ = struct.unpack('<III', d[lp:lp+12])
    chunks.append((scid, data_off + local_off))

# Strings
str_chunk = [c for c in chunks if c[0] == 0xCA5E0200][0]
count, _ = struct.unpack('<ii', d[str_chunk[1]:str_chunk[1]+8])
pos = str_chunk[1] + 8
strings = []
for _ in range(count):
    end = d.find(b'\x00', pos)
    strings.append(d[pos:end].decode('latin1'))
    pos = end + 1

# Objects
f00 = [c for c in chunks if c[0] == 0xCA5E0F00]
print(f"Total objects in Walk anim: {len(f00)}")

# Objects with props
objects = []
current_obj = None
for scid, off in chunks:
    if scid == 0xCA5E0F00:
        current_obj = {'props': []}
        objects.append(current_obj)
    elif scid == 0xCA5E0F01 and current_obj is not None:
        v0, v1, v2, v3 = struct.unpack('<iiii', d[off:off+16])
        current_obj['props'].append((v0, v1, v2, v3))

print(f"Parsed {len(objects)} objects with props:")
for i, obj in enumerate(objects):
    name = "NO_NAME"
    for p in obj['props']:
        if p[0] == 4: name = strings[p[2]]
    print(f"  Object {i} (1-based {i+1}): '{name}'")
