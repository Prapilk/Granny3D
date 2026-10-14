from test_clean_deform import parse_granny_file
import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn', 'rb') as f:
    data = f.read()

cid, _, data_off, _, _ = struct.unpack('<IIIII', data[0x74:0x74+20])
sub_count = struct.unpack('<I', data[data_off:data_off+4])[0]
list_pos = data_off + 16
chunks = []
for i in range(sub_count):
    lp = list_pos + i * 12
    scid, local_off, _ = struct.unpack('<III', data[lp:lp+12])
    chunks.append((scid, data_off + local_off))

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
print("H_Male_Walk_01 b00 count:", len(b00))
print("b00 values:", b00)

tracks = [c for c in chunks if c[0] == 0xCA5E1204]
h0_list = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in tracks]
print("track h0 count:", len(h0_list))
print("track h0 values:", h0_list)

# Does track i correspond 1-to-1 with b00[i] or bone i?
print("Is h0_list == b00?", h0_list == b00)
# What if track i corresponds to bone i?
# In H_Male_Walk_01: there are 42 bones and 42 tracks!
bones_506 = [c for c in chunks if c[0] == 0xCA5E0506]
print("bones_506 count:", len(bones_506))
for i in range(len(bones_506)):
    p, = struct.unpack('<i', data[bones_506[i][1]:bones_506[i][1]+4])
    pos = struct.unpack('<fff', data[bones_506[i][1]+4:bones_506[i][1]+16])
    print(f"Anim Bone {i:2d}: p={p:2d}, b00={b00[i]:2d}, track_h0={h0_list[i]:2d}, pos=({pos[0]:.2f}, {pos[1]:.2f}, {pos[2]:.2f})")
