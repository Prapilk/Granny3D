with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

# Let's inspect instructions from 0x12480 to 0x12600
data = exe[0x12480:0x12600]
for i in range(0, len(data), 16):
    line = data[i:i+16]
    hex_str = ' '.join(f'{b:02x}' for b in line)
    print(f"0x{0x12480 + i:05x}: {hex_str}")
