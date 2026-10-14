with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

import struct
rel = struct.unpack('<i', exe[0x1e1ff:0x1e203])[0]
target = 0x1e1ff + 4 + rel
print(f"Target of 0xCA5E0B00 call: 0x{target:x} (VA 0x{target+0x400000:x})")

data = exe[target:target+120]
for i in range(0, len(data), 16):
    line = data[i:i+16]
    hex_str = ' '.join(f'{b:02x}' for b in line)
    print(f"0x{target + i:05x}: {hex_str}")
