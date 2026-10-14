import struct
from parse_objects_clean import chunks, data

bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]

for i, c in enumerate(bone_chunks):
    p, = struct.unpack('<i', data[c[1]:c[1]+4])
    pos = struct.unpack('<fff', data[c[1]+4:c[1]+16])
    rot = struct.unpack('<ffff', data[c[1]+16:c[1]+32])
    print(f"Bone {i:2d} (p={p:2d}): pos=({pos[0]:6.3f}, {pos[1]:6.3f}, {pos[2]:6.3f}) rot=({rot[0]:6.3f}, {rot[1]:6.3f}, {rot[2]:6.3f}, {rot[3]:6.3f})")
