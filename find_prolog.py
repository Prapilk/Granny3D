with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

import struct

# Look for standard function prolog 55 8b ec or 6a ff 68 ... before 0x12320
for i in range(0x12200, 0x12320):
    if exe[i:i+3] == b'\x55\x8b\xec' or exe[i:i+4] == b'\x83\xec\x48' or exe[i:i+3] == b'\x6a\xff\x68':
        print(f"Prolog candidate at 0x{i:x} (VA 0x{i+0x400000:x})")
        # search callers to i
        for c in range(len(exe) - 5):
            if exe[c] == 0xe8:
                rel = struct.unpack('<i', exe[c+1:c+5])[0]
                if c + 5 + rel == i:
                    print(f"  Called from 0x{c:x} (VA 0x{c+0x400000:x})")
