import struct
import os

char_path = r"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\Superhero_Male_FullBody.char"

with open(char_path, "rb") as f:
    sig = f.read(7)
    ver = struct.unpack("<i", f.read(4))[0]
    print(f"Sig: {sig}, Version: {ver}")

    def read_str():
        b = f.read(4)
        if not b: return ""
        length = struct.unpack("<i", b)[0]
        if length <= 0: return ""
        return f.read(length).decode("utf-8")

    name = read_str()
    print("Name:", name)

    bone_count = struct.unpack("<i", f.read(4))[0]
    print("Bone count:", bone_count)
    for b in range(bone_count):
        bid = struct.unpack("<i", f.read(4))[0]
        pid = struct.unpack("<i", f.read(4))[0]
        bname = read_str()
        pos = struct.unpack("<3f", f.read(12))
        rot = struct.unpack("<4f", f.read(16))
        inv = struct.unpack("<16f", f.read(64))
        if b < 10 or "Thigh" in bname or "Pelvis" in bname or "Calf" in bname:
            print(f"Bone {bid}: {bname}, Parent={pid}")
            print(f"  Pos: ({pos[0]:.4f}, {pos[1]:.4f}, {pos[2]:.4f})")
            print(f"  Rot: ({rot[0]:.4f}, {rot[1]:.4f}, {rot[2]:.4f}, {rot[3]:.4f})")
