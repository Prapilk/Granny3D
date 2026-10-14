import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn', 'rb') as f:
    data = f.read()

# find all 0xCA5E0200 chunks
chunks = []
p = 0
while True:
    p = data.find(b'\x00\x02\x5e\xca', p)
    if p == -1: break
    chunks.append(p)
    p += 4

print("Found 0xCA5E0200 at:", [hex(c) for c in chunks])

for off in chunks:
    # chunk starts at off - 4 or off?
    # Chunk ID is at off - 0 if little endian
    # 0xCA5E0200 is bytes: 00 02 5e ca
    c_off = off
    print(f"\n--- Chunk at {hex(c_off)} ---")
    # let's find data offset in chunk list or inspect string header
    # in second chunk:
    # count, len
    # let's search for count, len
    for try_pos in range(c_off, c_off + 64):
        cnt, l = struct.unpack('<II', data[try_pos:try_pos+8])
        if 10 < cnt < 100 and 100 < l < 5000:
            print(f"Candidate string table at {hex(try_pos)}: count={cnt}, len={l}")
            raw = data[try_pos+8:try_pos+8+l]
            parts = raw.split(b'\x00')
            for idx, s in enumerate(parts):
                print(f"  [{idx:2d}] {s.decode('latin1', errors='ignore')}")
            break
