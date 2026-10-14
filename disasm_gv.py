with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

# PE header image base
import struct
pe_offset = struct.unpack('<I', exe[0x3c:0x40])[0]
image_base = struct.unpack('<I', exe[pe_offset+0x34:pe_offset+0x38])[0]
print(f"PE ImageBase = {hex(image_base)}")

# RVA of 0x3242c
# Let's find sections
num_sections = struct.unpack('<H', exe[pe_offset+0x6:pe_offset+0x8])[0]
opt_hdr_size = struct.unpack('<H', exe[pe_offset+0x14:pe_offset+0x16])[0]
sect_start = pe_offset + 0x18 + opt_hdr_size

def file_to_va(file_offset):
    for i in range(num_sections):
        s_off = sect_start + i * 40
        name = exe[s_off:s_off+8].rstrip(b'\x00').decode('latin1')
        vsize, va, rsize, roff = struct.unpack('<IIII', exe[s_off+8:s_off+24])
        if roff <= file_offset < roff + rsize:
            return image_base + va + (file_offset - roff)
    return None

str_va = file_to_va(0x3242c)
print(f"File 0x3242c -> Virtual Address = {hex(str_va)}")

# Find all references to addresses in [str_va - 0x1000, str_va + 0x1000] in .text
text_sect = sect_start
tvsize, tva, trsize, troff = struct.unpack('<IIII', exe[text_sect+8:text_sect+24])
text_data = exe[troff:troff+trsize]

# Search for DWORD references
for offset in range(0, len(text_data) - 4, 1):
    val = struct.unpack('<I', text_data[offset:offset+4])[0]
    if str_va - 200 <= val <= str_va + 1000:
        va_ref = image_base + tva + offset
        print(f"Reference at VA {hex(va_ref)} (file {hex(troff + offset)}): points to {hex(val)}")
