with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

# Let's inspect bytes in 0x12320..0x124b0
# Let's write a small disassembler using python or capstone if available, or manual decode
data = exe[0x12320:0x124a0]
print(f"Bytes length: {len(data)}")

# Let's see what instructions are around 0x12340:
# Is it an array of structs or a function?
# Let's print in hex
for i in range(0, min(100, len(data)), 16):
    line = data[i:i+16]
    hex_str = ' '.join(f'{b:02x}' for b in line)
    print(f"0x{0x12320 + i:05x}: {hex_str}")
