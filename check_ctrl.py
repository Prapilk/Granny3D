import re

target_file = r"c:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\UoFiddler.Plugin.Granny3D\Granny3DControl.cs"

with open(target_file, "r", encoding="utf-8-sig") as f:
    content = f.read()

# Let's inspect what namespaces and fields we need to add or update
print("Original size:", len(content))
