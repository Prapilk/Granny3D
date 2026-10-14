f = r"c:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\UoFiddler.Plugin.Granny3D\Granny3DControl.cs"
s = open(f, encoding="utf-8-sig", newline="").read()
nl = "\r\n" if "\r\n" in s else "\n"
s = s.replace("\r\n", "\n")
old_app = """                if (append) {
                    center = Vector3.Zero;
                    scale = 1.0f;
                }"""
new_app = """                // Cadre commun: tous les meshes (corps, parties, vetements) partagent le meme centre/echelle
                center = _refCenter;
                scale = _refScale;"""
assert old_app in s
s = s.replace(old_app, new_app)
old_idx = "            int meshIdx = 0;\n            foreach (var mesh in _parser.Meshes)"
new_idx = """            if (!append)
            {
                bool any = false;
                Vector3 gmin = Vector3.Zero, gmax = Vector3.Zero;
                foreach (var mm in _parser.Meshes)
                {
                    foreach (var v in mm.Vertices)
                    {
                        if (!any) { gmin = v; gmax = v; any = true; }
                        else { gmin = Vector3.ComponentMin(gmin, v); gmax = Vector3.ComponentMax(gmax, v); }
                    }
                }
                if (any)
                {
                    float gsize = (gmax - gmin).Length;
                    if (gsize == 0) gsize = 1;
                    _refCenter = (gmin + gmax) * 0.5f;
                    _refScale = 2.0f / gsize;
                }
                else { _refCenter = Vector3.Zero; _refScale = 1.0f; }
            }

            int meshIdx = 0;
            foreach (var mesh in _parser.Meshes)"""
assert old_idx in s
s = s.replace(old_idx, new_idx)
old_m = "        private bool LoadModel(string path, bool append)"
s = s.replace(old_m, "        private Vector3 _refCenter = Vector3.Zero;\n        private float _refScale = 1.0f;\n\n" + old_m, 1)
open(f, "w", encoding="utf-8-sig", newline="").write(s.replace("\n", nl))
print("ok")
