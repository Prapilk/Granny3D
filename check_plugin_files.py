import os

p_parser = r"c:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\UoFiddler.Plugin.Granny3D\GrannyFormatParser.cs"
p_ctrl = r"c:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\UoFiddler.Plugin.Granny3D\Granny3DControl.cs"

with open(p_parser, "r", encoding="utf-8-sig") as f:
    c_parser = f.read()
print(f"Parser lines: {len(c_parser.splitlines())}")

with open(p_ctrl, "r", encoding="utf-8-sig") as f:
    c_ctrl = f.read()
print(f"Control lines: {len(c_ctrl.splitlines())}")
