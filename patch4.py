f = r"c:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\UoFiddler.Plugin.Granny3D\Granny3DControl.cs"
s = open(f, encoding="utf-8-sig", newline="").read()
nl = "\r\n" if "\r\n" in s else "\n"
s = s.replace("\r\n", "\n")
old = """                else if (append)
                {
                    HideBodyPartsCoveredBy(selected);
                }
"""
assert old in s
s = s.replace(old, "")
open(f, "w", encoding="utf-8-sig", newline="").write(s.replace("\n", nl))
print("ok")
