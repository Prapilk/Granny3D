with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

# Let's see what function starts before 0x1da80
# Find prolog before 0x1da80
for p in range(0x1da80, 0x1da80 - 400, -1):
    if exe[p:p+3] == b'\x55\x8b\xec' or exe[p:p+3] == b'\x6a\xff\x68' or exe[p:p+4] == b'\x83\xec':
        print(f"Prolog at 0x{p:x} (VA 0x{p+0x400000:x})")
        break
