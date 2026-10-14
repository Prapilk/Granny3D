with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

import struct

# Search for 0x1204
pos = 0
while True:
    pos = exe.find(b'\x04\x12', pos)
    if pos == -1: break
    val = struct.unpack('<I', exe[pos:pos+4])[0]
    if (val & 0xFFFFFF00) == 0xCA5E1200:
        print(f"Found {hex(val)} at 0x{pos:x}")
    pos += 1
