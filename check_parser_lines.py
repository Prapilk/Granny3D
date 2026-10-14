p_parser = r"c:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\UoFiddler.Plugin.Granny3D\GrannyFormatParser.cs"
with open(p_parser, "r", encoding="utf-8-sig") as f:
    lines = f.readlines()

for i, line in enumerate(lines[:50]):
    print(f"{i+1:3d}: {line}", end="")
print("...")
for i, line in enumerate(lines[320:393], 321):
    print(f"{i+321:3d}: {line}", end="")
