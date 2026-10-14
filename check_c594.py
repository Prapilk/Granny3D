import struct
from parse_objects_clean import chunks, data

off = chunks[594][1]
ints = struct.unpack('<8i', data[off:off+32])
print("Chunk 594 ints:", ints)
