with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

from disasm_gv import file_to_va

# Let's read string at address:
def read_str_at_va(va):
    fo = va - 0x400000 - 0x30000 + 0x30000 # Wait, let's find file offset
    # Virtual Address = 0x400000 + section_va + (file_offset - roff)
    # in disasm_gv, file 0x3242c -> VA 0x43242c. So file_off = va - 0x400000
    fo = va - 0x400000
    end = exe.find(b'\x00', fo)
    return exe[fo:end].decode('latin1')

print("Disassembling around 0x412344 (file 0x12344):")
# Let's inspect instructions from file offset 0x12300 to 0x12500
slice_bytes = exe[0x12320:0x124b0]

# Let's inspect what strings are pushed or assigned
import struct
for fo in range(0x12340, 0x124a0, 1):
    val = struct.unpack('<I', exe[fo:fo+4])[0]
    if 0x432380 <= val <= 0x432650:
        s = read_str_at_va(val)
        print(f"File 0x{fo:x} (VA 0x{fo+0x400000:x}): references string '{s}'")
