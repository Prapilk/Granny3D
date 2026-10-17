using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UoFiddler.Plugin.Granny3D;

class TextureScanner {
    static void Main() {
        string baseModels = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\";
        string mapsPath = Path.Combine(baseModels, "Maps");

        if (!Directory.Exists(mapsPath)) {
            Console.WriteLine($"Maps folder not found at {mapsPath}");
            return;
        }

        // ONLY .tga files as requested by user
        var allTgaFiles = Directory.GetFiles(mapsPath, "*.tga", SearchOption.TopDirectoryOnly).ToList();
        var mapFileNames = new HashSet<string>(allTgaFiles.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        var mapFilesNoExt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in allTgaFiles) {
            string ne = Path.GetFileNameWithoutExtension(f);
            if (!mapFilesNoExt.ContainsKey(ne)) mapFilesNoExt[ne] = f;
        }

        int normalCount = allTgaFiles.Count(f => f.EndsWith("_m.tga", StringComparison.OrdinalIgnoreCase) || f.EndsWith("_M.tga", StringComparison.OrdinalIgnoreCase));
        int diffuseCount = allTgaFiles.Count - normalCount;

        var categories = new[] { "Monsters", "Animals", "Humans", "SeaCreatures", "Others" };
        var usedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelTextureMap = new List<(string Category, string ModelFile, string EmbeddedTex, string ResolvedTex, string Status)>();

        foreach (var cat in categories) {
            string catDir = Path.Combine(baseModels, cat);
            if (!Directory.Exists(catDir)) continue;

            var grnFiles = Directory.GetFiles(catDir, "*.grn", SearchOption.AllDirectories)
                .Where(f => {
                    string fn = Path.GetFileName(f);
                    if (cat == "Others") return true;
                    return fn.IndexOf("lod2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (fn.IndexOf("lod", StringComparison.OrdinalIgnoreCase) >= 0 && fn.IndexOf("lod1", StringComparison.OrdinalIgnoreCase) < 0 && fn.IndexOf("lod3", StringComparison.OrdinalIgnoreCase) < 0);
                })
                .ToList();

            foreach (var mf in grnFiles) {
                string mName = Path.GetFileName(mf);
                var parser = new GrannyFormatParser();
                bool ok = parser.Load(mf);
                if (!ok || parser.Meshes.Count == 0) continue;

                string emb = parser.TextureName ?? "";
                string resolved = null;

                // 1. Direct embedded texture match in Maps
                if (!string.IsNullOrEmpty(emb)) {
                    var cands = new List<string> { emb };
                    if (emb.Contains(" ")) cands.Add(emb.Replace(" ", "_"));
                    if (emb.Equals("CrystalEle_256x256", StringComparison.OrdinalIgnoreCase)) cands.Add("Crystal_Elemental_256x256");
                    else if (emb.Equals("Light-n-lad", StringComparison.OrdinalIgnoreCase)) cands.Add("LightingElemental01_512");
                    else if (emb.Equals("Wailing_Banshee_256x256", StringComparison.OrdinalIgnoreCase)) cands.Add("WailBanshee_256x256");
                    else if (emb.Equals("UT256_Samurai_Haidate_Studded_V2", StringComparison.OrdinalIgnoreCase)) cands.Add("UT256_Samurai_Studded_V2");
                    else if (emb.Equals("Shirt", StringComparison.OrdinalIgnoreCase)) cands.Add("UT256_Armor_Japanese_1_V2");
                    else if (emb.Equals("Weapons01", StringComparison.OrdinalIgnoreCase)) cands.Add("Weapons01_256x256");

                    foreach (var c in cands) {
                        if (mapFilesNoExt.TryGetValue(c, out string found)) {
                            resolved = Path.GetFileName(found);
                            break;
                        }
                    }
                }

                // 2. Creature derived name in Maps
                if (resolved == null) {
                    string creature = Granny3DControl.DeriveCreature(Path.GetFileNameWithoutExtension(mf));
                    var candidates = new List<string> { creature };
                    if (creature.StartsWith("Npc_", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(creature.Substring(4));
                    if (creature.StartsWith("Npc_Cpt_", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(creature.Substring(8));

                    foreach (var c in candidates) {
                        if (mapFilesNoExt.TryGetValue(c, out string f)) { resolved = Path.GetFileName(f); break; }
                        var prefMatch = mapFileNames.FirstOrDefault(fn => fn.StartsWith(c + "_", StringComparison.OrdinalIgnoreCase) && !fn.EndsWith("_m.tga", StringComparison.OrdinalIgnoreCase));
                        if (prefMatch != null) { resolved = prefMatch; break; }
                        prefMatch = mapFileNames.FirstOrDefault(fn => fn.StartsWith(c, StringComparison.OrdinalIgnoreCase) && !fn.EndsWith("_m.tga", StringComparison.OrdinalIgnoreCase));
                        if (prefMatch != null) { resolved = prefMatch; break; }
                    }
                }

                // 3. Human base skin
                if (resolved == null && cat == "Humans") {
                    resolved = "UT256_H_Male_V2.tga (skin par défaut)";
                }

                if (resolved != null && !resolved.Contains("(skin par défaut)")) {
                    usedTextures.Add(resolved);
                    string norm = Path.GetFileNameWithoutExtension(resolved) + "_M.tga";
                    if (mapFileNames.Contains(norm)) usedTextures.Add(norm);
                    norm = Path.GetFileNameWithoutExtension(resolved) + "_m.tga";
                    if (mapFileNames.Contains(norm)) usedTextures.Add(norm);
                }

                string status = resolved != null ? "OK" : "MISSING";
                modelTextureMap.Add((cat, mName, emb, resolved, status));
            }
        }

        var missingModels = modelTextureMap.Where(m => m.Status == "MISSING").ToList();
        var unusedTextures = mapFileNames.Where(f => !usedTextures.Contains(f)).OrderBy(f => f).ToList();
        var unusedDiffuse = unusedTextures.Where(f => !f.EndsWith("_m.tga", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("_M.tga", StringComparison.OrdinalIgnoreCase)).ToList();
        var unusedNormal = unusedTextures.Where(f => f.EndsWith("_m.tga", StringComparison.OrdinalIgnoreCase) || f.EndsWith("_M.tga", StringComparison.OrdinalIgnoreCase)).ToList();

        Console.WriteLine("\n================ SCAN TGA SUMMARY ================");
        Console.WriteLine($"Total textures .TGA dans Maps: {allTgaFiles.Count}");
        Console.WriteLine($"  Textures Diffuse (.TGA): {diffuseCount}");
        Console.WriteLine($"  Normal maps (_M.TGA): {normalCount}");
        Console.WriteLine($"Total modèles de base scannés: {modelTextureMap.Count}");
        Console.WriteLine($"Modèles avec texture trouvée: {modelTextureMap.Count - missingModels.Count}");
        Console.WriteLine($"Modèles sans texture: {missingModels.Count}");
        Console.WriteLine($"Textures .TGA utilisées: {usedTextures.Count}");
        Console.WriteLine($"Textures .TGA NON utilisées: {unusedTextures.Count} (Diffuse: {unusedDiffuse.Count}, Normal: {unusedNormal.Count})");

        if (missingModels.Count > 0) {
            Console.WriteLine("\n--- MODÈLES SANS TEXTURE TGA ---");
            foreach (var m in missingModels) {
                Console.WriteLine($"[{m.Category}] {m.ModelFile} -> Embedded: '{m.EmbeddedTex}'");
            }
        }

        Console.WriteLine("\n--- LISTE DES 141 TEXTURES DIFFUSE .TGA NON UTILISÉES ---");
        foreach (var t in unusedDiffuse) {
            Console.WriteLine($"  {t}");
        }
    }
}
