import struct
from parse_objects_clean import chunks, data

bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]
print(f"Total 0xCA5E0506 chunks: {len(bone_chunks)}")
# Size of 0xCA5E0506 chunk:
# In chunk list, size is chunks[i][2] (wait, chunks in parse_objects_clean only saved (scid, off))
# Let's see distance between consecutive bone_chunks:
diff = bone_chunks[1][1] - bone_chunks[0][1]
print(f"Offset difference: {diff} bytes (0x{diff:x})")

# Let's inspect the first 3 chunks:
for i in range(5):
    off = bone_chunks[i][1]
    raw = data[off:off+diff]
    # Unpack as ints and floats
    ints = struct.unpack(f'<{len(raw)//4}i', raw[:(len(raw)//4)*4])
    floats = struct.unpack(f'<{len(raw)//4}f', raw[:(len(raw)//4)*4])
    print(f"\nBone chunk {i} at 0x{off:x}:")
    print(f"  ints:   {ints}")
    print(f"  floats: {[round(f, 3) for f in floats]}")
