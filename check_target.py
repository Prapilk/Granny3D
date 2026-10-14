with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

# 0x124c0 is in file. VA is 0x4124c0.
# Call target is 0x4124c0 + 5 + (-0x2d52) = 0x40f773
call_target_va = 0x4124bf + 5 + (-0x2d52) # Let's calculate from offset
import struct
rel = struct.unpack('<i', exe[0x124be:0x124c2])[0]
target_off = 0x124be + 4 + rel
print(f"Target file offset: 0x{target_off:x}, VA: 0x{target_off + 0x400000:x}")

# Inspect target function
data = exe[target_off:target_off+100]
for i in range(0, len(data), 16):
    line = data[i:i+16]
    hex_str = ' '.join(f'{b:02x}' for b in line)
    print(f"0x{target_off + i:05x}: {hex_str}")
