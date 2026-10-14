import struct
from match_by_transform import data_walk, walk_chunks, walk_strings

tracks = [c for c in walk_chunks if c[0] == 0xCA5E1204]
print(f"Total tracks in walk anim: {len(tracks)}")

# Let's inspect the first 50 bytes of each track chunk!
for i, c in enumerate(tracks[:15]):
    off = c[1]
    raw = data_walk[off:off+48]
    ints = struct.unpack('<12i', raw)
    print(f"Track {i} at 0x{off:x}: {ints}")
