with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

# Let's inspect function containing 0x1e0e0 (0xca5e0b00)
# Look backwards for function prolog
import struct

def find_prolog(off):
    for p in range(off, off - 500, -1):
        if exe[p:p+3] == b'\x55\x8b\xec' or exe[p:p+3] == b'\x6a\xff\x68' or exe[p:p+4] == b'\x83\xec':
            return p
    return off - 100

prolog_b00 = find_prolog(0x1e0e0)
print(f"Prolog for 0x1e0e0 at 0x{prolog_b00:x} (VA 0x{prolog_b00+0x400000:x})")

# Let's dump bytes from 0x1e0c0 to 0x1e180
data = exe[0x1e0c0:0x1e180]
for i in range(0, len(data), 16):
    line = data[i:i+16]
    hex_str = ' '.join(f'{b:02x}' for b in line)
    print(f"0x{0x1e0c0 + i:05x}: {hex_str}")
