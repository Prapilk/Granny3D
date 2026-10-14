import struct
from parse_objects_clean import chunks, data

for c_idx in [595, 596, 597]:
    off = chunks[c_idx][1]
    cid = chunks[c_idx][0]
    ints = struct.unpack('<8i', data[off:off+32])
    print(f"Chunk {c_idx} ({hex(cid)}): {ints}")
