using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UoFiddler.Plugin.Granny3D;

class TestGatherModels {
    static void Main() {
        string basePath = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\";
        var categories = new (string FolderName, string Display)[] {
            ("Humans", "Humans"),
            ("Monsters", "Monsters"),
            ("Animals", "Animals"),
            ("SeaCreatures", "Sea Creatures"),
            ("Others", "Others")
        };

        int totalCreatures = 0;
        foreach (var (folderName, display) in categories) {
            string folder = Path.Combine(basePath, folderName);
            if (!Directory.Exists(folder)) continue;

            var entries = new List<string>();
            if (folderName == "Others") {
                foreach (var f in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)) {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".char" || ext == ".fbx" || ext == ".grn") {
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (!entries.Contains(name)) entries.Add(name);
                    }
                }
            } else {
                var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(folder, "*.grn", SearchOption.AllDirectories)) {
                    string fn = Path.GetFileNameWithoutExtension(f);
                    if (fn.IndexOf("lod", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string c = Granny3DControl.DeriveCreature(fn);
                    if (string.IsNullOrEmpty(c)) c = fn;
                    if (!map.ContainsKey(c) || fn.IndexOf("lod2", StringComparison.OrdinalIgnoreCase) >= 0)
                        map[c] = f;
                }
                entries.AddRange(map.Keys);
            }

            Console.WriteLine($"[{display}]: {entries.Count} entries");
            totalCreatures += entries.Count;
            // Print sample
            for (int i = 0; i < Math.Min(3, entries.Count); i++) {
                Console.WriteLine($"   - {entries[i]}");
            }
        }
        Console.WriteLine($"Total across all categories: {totalCreatures}");
    }
}
