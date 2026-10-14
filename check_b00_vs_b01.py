import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'rb') as f:
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

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in flat if c[0] == 0xCA5E0B00]
print(f"b00 ({len(b00)} entries): {b00}")

b01_off = [c[1] for c in flat if c[0] == 0xCA5E0B01][0]
b01 = struct.unpack('<43i', data[b01_off:b01_off+43*4])
print(f"b01 ({len(b01)} entries): {b01}")
