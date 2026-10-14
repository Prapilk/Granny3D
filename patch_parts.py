import re
f = r"c:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\UoFiddler.Plugin.Granny3D\Granny3DControl.cs"
s = open(f, encoding="utf-8-sig").read()
n = 0
def sub(m):
    global n
    n += 1
    txt = m.group(0)
    if "H_Female" in txt or "Female" in txt:
        txt = txt.replace("H_Female_Face_01_V2_LOD2.grn", "H_Female_Face_V2_lod2.grn").replace("E_Female_Hair01_V2_LOD2.grn", "H_Female_Hair_Long_V2_LOD2.grn")
        extra = ', "H_Female_Neck_V2_LOD2.grn", "H_%sFemale_Pelvis_V2_LOD2.grn", "H_Female_Feet_V2_LOD2.grn"' % ("Savage_" if "Savage_Female" in txt else "")
    else:
        extra = ', "H_Male_Neck_V2_LOD2.grn", "H_%sMale_Pelvis_V2_LOD2.grn", "H_Male_Feet_V2_LOD2.grn"' % ("Savage_" if "Savage_Male" in txt else "")
    return txt.rstrip().rstrip("};").rstrip() + extra + "\n                      };"
s2 = re.sub(r'string\[\] parts = \{[^}]*\};', sub, s)
open(f, "w", encoding="utf-8-sig").write(s2)
print("patched", n)
