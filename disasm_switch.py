with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

# Let's inspect bytes from 0x1e1e0 to 0x1e240
# Let's print each instruction with its address
data = exe[0x1e1d0:0x1e240]
import struct

# Let's see all jumps and calls in this range
for i in range(len(data) - 4):
    off = 0x1e1d0 + i
    b = data[i]
    if b == 0xe8:
        rel = struct.unpack('<i', data[i+1:i+5])[0]
        print(f"0x{off:x}: CALL 0x{off + 5 + rel:x}")
    elif b == 0xe9:
        rel = struct.unpack('<i', data[i+1:i+5])[0]
        print(f"0x{off:x}: JMP  0x{off + 5 + rel:x}")
    elif b == 0x81 and data[i+1] == 0xfd:
        cid = struct.unpack('<I', data[i+2:i+6])[0]
        print(f"0x{off:x}: CMP EBP, {hex(cid)}")
