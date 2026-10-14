with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

import struct

chunks_to_find = [
    0xCA5E0200, 0xCA5E0506, 0xCA5E0702, 0xCA5E0801, 
    0xCA5E0B00, 0xCA5E0C0A, 0xCA5E0F00, 0xCA5E0F01, 0xCA5E1204
]

print("Searching Granny chunk IDs in GrannyViewer.exe:")
for cid in chunks_to_find:
    pos = 0
    matches = []
    while True:
        pos = exe.find(struct.pack('<I', cid), pos)
        if pos == -1: break
        matches.append(hex(pos))
        pos += 1
    print(f"  {hex(cid)}: found at {matches}")
