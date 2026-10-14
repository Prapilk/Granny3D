import struct
from print_walk_tracks_all import chunks, d, objects, strings

b00_walk = [struct.unpack('<i', d[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
tracks = [c for c in chunks if c[0] == 0xCA5E1204]

print(f"Total bones/tracks: {len(tracks)}")
for i in range(len(tracks)):
    obj_idx = b00_walk[i] - 1
    name = objects[obj_idx]['name']
    off = tracks[i][1]
    cP, cR, cS = struct.unpack('<iii', d[off+24:off+36])
    pos_off = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', d[pos_off:pos_off+12]) if cP > 0 else (0,0,0)
    print(f"Track {i:2d} -> Bone {i:2d}: '{name:18s}' | pos=({p0[0]:6.3f}, {p0[1]:6.3f}, {p0[2]:6.3f})")
