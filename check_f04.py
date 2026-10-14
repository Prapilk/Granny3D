import struct
from parse_objects_clean import chunks, data

f04_chunks = [c for c in chunks if c[0] == 0xCA5E0F04]
print(f"Total f04 chunks: {len(f04_chunks)}")
for i, c in enumerate(f04_chunks[:10]):
    ints = struct.unpack('<4i', data[c[1]:c[1]+16])
    print(f"  f04[{i}]: {ints}")
