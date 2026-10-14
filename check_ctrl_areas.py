p_ctrl = r"c:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\UoFiddler.Plugin.Granny3D\Granny3DControl.cs"
with open(p_ctrl, "r", encoding="utf-8-sig") as f:
    text = f.read()

import re
matches = [m.start() for m in re.finditer(r'class LoadedMesh|ApplyAnimationPose|btnPlay_Click|OnRenderFrame', text)]
for idx in matches:
    start = max(0, idx - 100)
    end = min(len(text), idx + 500)
    print("--- MATCH ---")
    print(text[start:end])
