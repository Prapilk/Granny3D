import struct
from scratch_test14 import get_obj_names

objs_a, data_a, chunks_a = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

# We know:
# Pelvis is Object 8 (0-based 7)
# Pelvis is Bone 2
# Let's search all chunks for where bone 2 and object 8 (or 7) occur together or mapped!
print(f"Total chunks: {len(chunks_a)}")
for cid, off in chunks_a:
    # check first 64 bytes of chunk
    # unpack as ints
    try:
        ints = [struct.unpack('<i', data_a[off+i*4:off+(i+1)*4])[0] for i in range(min(16, (len(data_a)-off)//4))]
        # if 8 or 7 is in ints and 2 is in ints:
        if (8 in ints or 7 in ints) and 2 in ints:
            print(f"Match in chunk {hex(cid)} at {hex(off)}: {ints[:8]}")
    except:
        pass
