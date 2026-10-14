f = r"c:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\UoFiddler.Plugin.Granny3D\Granny3DControl.cs"
s = open(f, encoding="utf-8-sig", newline="").read()
nl = "\r\n" if "\r\n" in s else "\n"
s = s.replace("\r\n", "\n")

old = """                glControl.Invalidate();
            }
        }

        private void LstAnimations_DoubleClick"""
new = """                // Le corps de base (H_Male_Lod2...) double les parties detaillees et depasse des vetements: on le retire
                if (!append && (fnameCheck.StartsWith("h_male_lod2") || fnameCheck.StartsWith("h_female_lod2") ||
                                fnameCheck.StartsWith("h_savage_male_lod2") || fnameCheck.StartsWith("h_savage_female_lod2")))
                {
                    RemoveMeshesByFile(Path.GetFileName(grnPath));
                }
                else if (append)
                {
                    HideBodyPartsCoveredBy(selected);
                }

                glControl.Invalidate();
            }
        }

        private void RemoveMeshesByFile(string fileName)
        {
            this.glControl.MakeCurrent();
            for (int i = _loadedMeshes.Count - 1; i >= 0; i--)
            {
                if (_loadedMeshes[i].Name.StartsWith(fileName + " (", StringComparison.OrdinalIgnoreCase))
                {
                    var m = _loadedMeshes[i];
                    if (m.Vbo > 0) GL.DeleteBuffer(m.Vbo);
                    if (m.UvVbo > 0) GL.DeleteBuffer(m.UvVbo);
                    _loadedMeshes.RemoveAt(i);
                    lstLoaded.Items.RemoveAt(i);
                }
            }
        }

        // Comme dans le jeu: un vetement remplace les parties du corps qu'il recouvre (evite que la peau depasse)
        private void HideBodyPartsCoveredBy(string clothFile)
        {
            string n = clothFile.ToLowerInvariant();
            string gender = null;
            foreach (var lm in _loadedMeshes)
            {
                string ln = lm.Name.ToLowerInvariant();
                if (ln.StartsWith("h_male_torso_v2")) gender = "H_Male_";
                else if (ln.StartsWith("h_female_torso_v2")) gender = "H_Female_";
            }
            if (gender == null) return; // pas de corps humain affiche

            var hide = new List<string>();
            bool upper = n.Contains("shirt") || n.Contains("tunic") || n.Contains("chest") || n.Contains("robe") ||
                         n.Contains("jacket") || n.Contains("dress") || n.Contains("mogamido") || n.Contains("breastplate") || n.Contains("doublet");
            if (n.Contains("sleeve") || n.Contains("robe") || n.Contains("jacket") || n.Contains("dress")) { hide.Add("UArms_V2"); }
            if (n.Contains("sleeve") || n.Contains("robe") || n.Contains("jacket") || n.Contains("dress")) { hide.Add("FArms_V2"); }
            if (upper && !n.Contains("sleeves")) hide.Add("Torso_V2");
            if (n.Contains("glove") || n.Contains("kote")) hide.Add("Hands_V2");
            if (n.Contains("shoes") || n.Contains("boots") || n.Contains("sandals") || n.Contains("waraji")) hide.Add("Feet_V2");
            if (n.Contains("pants_calf") || n.Contains("boots") || n.Contains("suneate_v2")) hide.Add("LLegs_V2");
            if (n.Contains("pants_thigh") || n.Contains("pants_mid") || n.Contains("thigh")) hide.Add("ULegs_V2");
            if (n.Contains("pants") && !n.Contains("calf") && !n.Contains("thigh") && !n.Contains("mid")) { hide.Add("Pelvis_V2"); }
            if (n.Contains("robe") || n.Contains("dress")) { hide.Add("ULegs_V2"); hide.Add("LLegs_V2"); hide.Add("Pelvis_V2"); }

            foreach (var part in hide)
                RemoveMeshesByFile(gender + part + "_LOD2.grn");
        }

        private void LstAnimations_DoubleClick"""
assert old in s
s = s.replace(old, new, 1)
open(f, "w", encoding="utf-8-sig", newline="").write(s.replace("\n", nl))
print("ok")
