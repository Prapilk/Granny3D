using System;
using System.IO;
using System.Collections.Generic;
using OpenTK.Mathematics;
using UoFiddler.Plugin.Granny3D;

class TestVerification {
    static void Main() {
        string fbx2 = @"c:\Users\User\Downloads\T-Pose(2).fbx";
        string outDir = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Others";
        string uoRef = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn";

        Console.WriteLine("=== 1. Testing Import of T-Pose(2).fbx ===");
        bool ok = MixamoCharacterImport.TryImport(fbx2, outDir, uoRef, out var model, out string err);
        Console.WriteLine($"Import success: {ok}, error: {err}");
        if (!ok || model == null) return;

        Console.WriteLine($"Model Name: {model.Name}, Parts: {model.Parts.Count}, Bones: {model.Bones.Count}");
        foreach (var p in model.Parts) {
            string texFile = Path.Combine(outDir, p.TextureName);
            bool texExists = File.Exists(texFile);
            long texLen = texExists ? new FileInfo(texFile).Length : 0;
            Console.WriteLine($"Part '{p.Name}': TextureName='{p.TextureName}', Exists={texExists} ({texLen} bytes)");
        }

        Console.WriteLine("\n=== 2. Checking Finger Weights ===");
        var fingerBones = new HashSet<int>();
        for (int i = 0; i < model.Bones.Count; i++) {
            if (model.Bones[i].Name.Contains("Finger")) {
                fingerBones.Add(i);
                Console.WriteLine($"Bone[{i}]: {model.Bones[i].Name}");
            }
        }

        int verticesWithFingerWeights = 0;
        foreach (var p in model.Parts) {
            if (p.Skin == null) continue;
            for (int v = 0; v < p.Skin.Length; v++) {
                var infs = p.Skin[v];
                if (infs == null) continue;
                bool hasFinger = false;
                foreach (var inf in infs) {
                    if (fingerBones.Contains(inf.BoneId) && inf.Weight > 0.05f) {
                        hasFinger = true;
                        break;
                    }
                }
                if (hasFinger) verticesWithFingerWeights++;
            }
        }
        Console.WriteLine($"Total vertices influenced by finger bones: {verticesWithFingerWeights}");

        Console.WriteLine("\n=== 3. Testing Hand deformation under animation ===");
        string animPath = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn";
        var animParser = new GrannyFormatParser();
        bool animOk = animParser.Load(animPath);
        Console.WriteLine($"Loaded anim: {animOk}, tracks: {animParser.Animation?.Tracks?.Count ?? 0}");
        
        Console.WriteLine("VERIFICATION COMPLETE!");
    }
}
