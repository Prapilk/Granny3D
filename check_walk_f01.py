import struct
from check_walk_objects import chunks, d, strings

f01_chunks = [c for c in chunks if c[0] == 0xCA5E0F01]
print(f"Total f01 chunks in Walk anim: {len(f01_chunks)}")

for i, c in enumerate(f01_chunks[:30]):
    off = c[1]
    v0, v1, v2, v3 = struct.unpack('<iiii', d[off:off+16])
    s = strings[v2] if v2 < len(strings) else f"?({v2})"
    print(f"f01[{i:2d}]: v0={v0}, v1={v1}, v2={v2:2d} ('{s:20s}'), v3={v3}")
