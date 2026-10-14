import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'rb') as f:
    data = f.read()

num_roots = struct.unpack('<I', data[0x44:0x48])[0]
print(f"Num root chunks: {num_roots}")
pos = 0x60
for r in range(num_roots):
    cid, u1, data_off, u2, peers = struct.unpack('<IIIII', data[pos:pos+20])
    pos += 20
    print(f"Root {r}: cid={hex(cid)}, data_off={hex(data_off)}, peers={peers}")
