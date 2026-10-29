import struct
import numpy as np

char_path = r"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\Superhero_Male_FullBody.char"

def load_bones_from_char(path):
    with open(path, "rb") as f:
        sig = f.read(7)
        ver = struct.unpack("<i", f.read(4))[0]
        def read_str():
            b = f.read(4)
            if not b: return ""
            length = struct.unpack("<i", b)[0]
            if length <= 0: return ""
            return f.read(length).decode("utf-8")
        name = read_str()
        bone_count = struct.unpack("<i", f.read(4))[0]
        bones = []
        for b in range(bone_count):
            bid = struct.unpack("<i", f.read(4))[0]
            pid = struct.unpack("<i", f.read(4))[0]
            bname = read_str()
            pos = struct.unpack("<3f", f.read(12))
            rot = struct.unpack("<4f", f.read(16))
            inv = struct.unpack("<16f", f.read(64))
            inv_mat = np.array(inv, dtype=np.float32).reshape((4, 4))
            bones.append({"id": bid, "pid": pid, "name": bname, "pos": pos, "rot": rot, "inv": inv_mat})
        return bones

char_bones = load_bones_from_char(char_path)
print("Loaded", len(char_bones), "char bones.")

for b in char_bones:
    if any(k in b["name"] for k in ["Pelvis", "Thigh", "Calf", "Foot"]):
        # InverseBindMatrix
        try:
            world = np.linalg.inv(b["inv"])
            wpos = world[3, :3] # in OpenTK Row3 is translation
            print(f"Bone {b['name']}: world pos = {wpos}")
        except Exception as e:
            print(f"Bone {b['name']}: inv error {e}")
