using System;
using System.IO;
using System.Linq;
using Assimp;
using OpenTK.Mathematics;
using UoFiddler.Plugin.Granny3D;

class TestSuperheroWithMapping
{
    static void Main()
    {
        string[] files = new string[]
        {
            @"c:\Games\Sphere\Universal Base Characters[Standard]\Base Characters\Unity\Superhero_Male_FullBody.fbx",
            @"c:\Games\Sphere\Universal Base Characters[Standard]\Base Characters\Unity\Superhero_Female_FullBody.fbx",
            @"c:\Users\User\Downloads\Female_mixamo_3finger.fbx",
            @"c:\Users\User\Downloads\T-Pose(3).fbx"
        };

        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            Console.WriteLine($"\n==========================================");
            Console.WriteLine($"Importing: {Path.GetFileName(file)}");
            Console.WriteLine($"==========================================");

            bool ok = MixamoCharacterImport.TryImport(file, out var model, out string err);
            Console.WriteLine($"Import OK: {ok}, err: {err}");
            if (model != null)
            {
                Console.WriteLine($"Parts: {model.Parts.Count}, Bones: {model.Bones.Count}");
                for (int pi = 0; pi < model.Parts.Count; pi++)
                {
                    var p = model.Parts[pi];
                    int skinnedVerts = 0;
                    for (int v = 0; v < p.Vertices.Length; v++)
                    {
                        if (p.Skin != null && v < p.Skin.Length && p.Skin[v] != null && p.Skin[v].Length > 0)
                            skinnedVerts++;
                    }
                    Console.WriteLine($"  Part [{pi}] '{p.Name}': verts={p.Vertices.Length}, skinned={skinnedVerts} ({(p.Vertices.Length > 0 ? (skinnedVerts * 100 / p.Vertices.Length) : 0)}%)");
                }

                // Check a few key bone positions
                foreach (var bname in new[] { "Bip01 L Hand", "Bip01 L Finger0", "Bip01 L Finger01", "Bip01 L Finger1", "Bip01 L Finger11", "Bip01 R Hand", "Bip01 R Finger0", "Bip01 R Finger01", "Bip01 R Finger1", "Bip01 R Finger11" })
                {
                    int bidx = model.Bones.FindIndex(x => x.Name == bname);
                    if (bidx >= 0)
                    {
                        var b = model.Bones[bidx];
                        var w = Matrix4.Invert(b.InverseBindMatrix);
                        int vCount = 0;
                        if (model.Parts.Count > 0)
                        {
                            foreach (var pt in model.Parts)
                            {
                                if (pt.Skin == null) continue;
                                for (int v = 0; v < pt.Skin.Length; v++)
                                {
                                    if (pt.Skin[v] != null && pt.Skin[v].Any(inf => inf.BoneId == bidx && inf.Weight > 0.05f))
                                        vCount++;
                                }
                            }
                        }
                        Console.WriteLine($"  Bone '{bname}': WorldPos=({w.Row3.X:F3}, {w.Row3.Y:F3}, {w.Row3.Z:F3}), Verts={vCount}");
                    }
                    else
                    {
                        Console.WriteLine($"  Bone '{bname}' NOT FOUND!");
                    }
                }
            }
        }
        return;

        string uoRef = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn";
        var parser = new GrannyFormatParser();
        parser.Load(uoRef);
        var refBones = parser.Bones;
        for (int i = 0; i < refBones.Count; i++)
        {
            var b = refBones[i];
            var w = Matrix4.Invert(b.InverseBindMatrix);
            if (b.Name.Contains("Pelvis") || b.Name.Contains("Head"))
                Console.WriteLine($"UO Ref Bone [{i}] '{b.Name}': WorldPos=({w.Row3.X:F3}, {w.Row3.Y:F3}, {w.Row3.Z:F3})");
        }

        string walkGrn = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn";
        var animParser = new GrannyFormatParser();
        animParser.Load(walkGrn);
        var anim = animParser.Animation;

        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            Console.WriteLine($"\n==========================================");
            Console.WriteLine($"Testing: {Path.GetFileName(file)}");
            Console.WriteLine($"==========================================");

            using var ctx = new AssimpContext();
            var scene = ctx.ImportFile(file, PostProcessSteps.Triangulate | PostProcessSteps.LimitBoneWeights | PostProcessSteps.GenerateNormals);

            var nodes = new System.Collections.Generic.List<Node>();
            var parent = new System.Collections.Generic.List<int>();
            Collect(scene.RootNode, -1, nodes, parent);
            int nn = nodes.Count;

            var nameToNode = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nn; i++)
                if (!nameToNode.ContainsKey(nodes[i].Name)) nameToNode[nodes[i].Name] = i;

            foreach (var mesh in scene.Meshes)
            {
                Console.WriteLine($"Mesh '{mesh.Name}' has {mesh.BoneCount} bones:");
                for (int b = 0; b < mesh.BoneCount; b++)
                    Console.Write($"'{mesh.Bones[b].Name}', ");
                Console.WriteLine();
            }

            var wMix = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                var m = ToMatrix4(nodes[i].Transform);
                int p = parent[i];
                wMix[i] = (p >= 0) ? m * wMix[p] : m;
            }

            for (int i = 0; i < Math.Min(15, nn); i++)
            {
                Console.WriteLine($"Node [{i}] '{nodes[i].Name}': Pos=({wMix[i].Row3.X:F3}, {wMix[i].Row3.Y:F3}, {wMix[i].Row3.Z:F3})");
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var mesh in scene.Meshes)
            {
                var meshNode = scene.RootNode.FindNode(mesh.Name) ?? scene.RootNode;
                int nidx = -1;
                nameToNode.TryGetValue(meshNode.Name, out nidx);
                Matrix4 mmat = nidx >= 0 ? wMix[nidx] : Matrix4.Identity;
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    var wv = new Vector4(mesh.Vertices[v].X, mesh.Vertices[v].Y, mesh.Vertices[v].Z, 1.0f) * mmat;
                    if (wv.X < minX) minX = wv.X; if (wv.X > maxX) maxX = wv.X;
                    if (wv.Y < minY) minY = wv.Y; if (wv.Y > maxY) maxY = wv.Y;
                    if (wv.Z < minZ) minZ = wv.Z; if (wv.Z > maxZ) maxZ = wv.Z;
                }
            }
            Console.WriteLine($"Extents: X=[{minX:F2}, {maxX:F2}] (dx={maxX-minX:F2}), Y=[{minY:F2}, {maxY:F2}] (dy={maxY-minY:F2}), Z=[{minZ:F2}, {maxZ:F2}] (dz={maxZ-minZ:F2})");
            bool isZUp = (maxZ - minZ) > (maxY - minY) * 1.5f;
            float rawHeight = isZUp ? (maxZ - minZ) : (maxY - minY);
            float scale = 0.01f;
            if (rawHeight > 500f) scale = 0.001f;
            else if (rawHeight > 50f) scale = 0.01f;
            else if (rawHeight > 5f) scale = 0.1f;
            else if (rawHeight > 0.5f) scale = 1.0f;

            Console.WriteLine($"Detection: isZUp={isZUp}, rawHeight={rawHeight:F2}, scale={scale}");

            Matrix4 C = isZUp ? Matrix4.Identity : Matrix4.CreateRotationX(MathHelper.DegreesToRadians(90f));
            Matrix4 Cinv = Matrix4.Invert(C);
            var wU = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                var mat = isZUp ? wMix[i] : (Cinv * wMix[i] * C);
                mat.Row3.X *= scale;
                mat.Row3.Y *= scale;
                mat.Row3.Z *= scale;
                wU[i] = mat;
            }

            int nb = refBones.Count;
            var fittedBones = new System.Collections.Generic.List<GrannyBone>(nb);
            for (int i = 0; i < nb; i++)
            {
                fittedBones.Add(new GrannyBone
                {
                    Name = refBones[i].Name,
                    ParentId = refBones[i].ParentId,
                    LocalRotation = refBones[i].LocalRotation,
                    LocalPosition = refBones[i].LocalPosition,
                    InverseBindMatrix = refBones[i].InverseBindMatrix
                });
            }

            var uoBindWorld = new Matrix4[nb];
            for (int i = 0; i < nb; i++)
                uoBindWorld[i] = Matrix4.Invert(refBones[i].InverseBindMatrix);

            var uoBoneByName = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < refBones.Count; i++) uoBoneByName[refBones[i].Name] = i;

            int FindNode(string baseName)
            {
                if (string.IsNullOrEmpty(baseName)) return -1;
                if (nameToNode.TryGetValue(baseName, out int idx)) return idx;
                string clean = baseName;
                int col = clean.IndexOf(':');
                if (col >= 0) clean = clean.Substring(col + 1);
                if (nameToNode.TryGetValue(clean, out idx)) return idx;

                // Search nodes that resolve to this UO bone name
                for (int ni = 0; ni < nn; ni++)
                {
                    int res = ResolveBoneToUoTest(nodes[ni].Name, uoBoneByName, nameToNode, nodes, parent);
                    if (res >= 0 && refBones[res].Name.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                        return ni;
                }

                return -1;
            }

            var fittedWorld = new Matrix4[nb];
            for (int i = 0; i < nb; i++)
            {
                string uoName = refBones[i].Name;
                fittedWorld[i] = uoBindWorld[i];

                if (string.Equals(uoName, "__Root", StringComparison.OrdinalIgnoreCase) ||
                    uoName.StartsWith("master_", StringComparison.OrdinalIgnoreCase))
                    continue;

                int ni = FindNode(uoName);
                if (ni >= 0)
                {
                    Vector3 mixPos = wU[ni].Row3.Xyz;
                    var rot = uoBindWorld[i].ExtractRotation();
                    fittedWorld[i] = Matrix4.CreateFromQuaternion(rot) * Matrix4.CreateTranslation(mixPos);
                }
                else
                {
                    int p = refBones[i].ParentId;
                    if (p >= 0 && p < nb && p != i)
                    {
                        var uoLocal = uoBindWorld[i] * Matrix4.Invert(uoBindWorld[p]);
                        fittedWorld[i] = uoLocal * fittedWorld[p];
                    }
                }
            }

            for (int i = 0; i < nb; i++)
            {
                int p = fittedBones[i].ParentId;
                if (p >= 0 && p < nb && p != i)
                {
                    var localMat = fittedWorld[i] * Matrix4.Invert(fittedWorld[p]);
                    fittedBones[i].LocalPosition = localMat.ExtractTranslation();
                    fittedBones[i].LocalRotation = localMat.ExtractRotation();
                }
                else
                {
                    fittedBones[i].LocalPosition = fittedWorld[i].ExtractTranslation();
                    fittedBones[i].LocalRotation = fittedWorld[i].ExtractRotation();
                }
                fittedBones[i].InverseBindMatrix = Matrix4.Invert(fittedWorld[i]);
            }

            Console.WriteLine("Bone positions fitted:");
            for (int i = 0; i < nb; i++)
            {
                var b = fittedBones[i];
                if (b.Name.Contains("Pelvis") || b.Name.Contains("Forearm") || b.Name.Contains("Hand") || b.Name.Contains("Calf") || b.Name.Contains("Foot"))
                {
                    Console.WriteLine($"  [{i:D2}] '{b.Name}': LocalPos=({b.LocalPosition.X:F3}, {b.LocalPosition.Y:F3}, {b.LocalPosition.Z:F3})");
                }
            }

            // Check animated motion of hands, calves, feet at t=0 and t=0.5
            var localMats = new Matrix4[nb];
            var worldMats = new Matrix4[nb];
            for (float t = 0f; t <= 0.5f; t += 0.5f)
            {
                for (int i = 0; i < nb; i++)
                {
                    var b = fittedBones[i];
                    var tr = anim.Tracks.FirstOrDefault(track => track.BoneId == i);
                    Vector3 pos;
                    OpenTK.Mathematics.Quaternion rot;
                    if (tr != null)
                    {
                        bool isRoot = (i == 0 || i == 1 || i == 2 || i == 3);
                        bool useAnimPos = isRoot || b.Name.StartsWith("CP_", StringComparison.OrdinalIgnoreCase) || (tr.Pos != null && tr.Pos.Length > 1);
                        pos = useAnimPos ? tr.EvaluatePos(t) : b.LocalPosition;
                        rot = tr.EvaluateRot(t);
                    }
                    else
                    {
                        pos = b.LocalPosition;
                        rot = b.LocalRotation;
                    }
                    localMats[i] = Matrix4.CreateFromQuaternion(rot) * Matrix4.CreateTranslation(pos);
                }
                for (int i = 0; i < nb; i++)
                {
                    int p = fittedBones[i].ParentId;
                    worldMats[i] = (p >= 0 && p < nb && p != i) ? localMats[i] * worldMats[p] : localMats[i];
                }

                int lHand = fittedBones.FindIndex(b => b.Name == "Bip01 L Hand");
                int rHand = fittedBones.FindIndex(b => b.Name == "Bip01 R Hand");
                int lFoot = fittedBones.FindIndex(b => b.Name == "Bip01 L Foot");
                int rFoot = fittedBones.FindIndex(b => b.Name == "Bip01 R Foot");

                Console.WriteLine($"Walk t={t:F1}:");
                Console.WriteLine($"  Hands: L=({worldMats[lHand].Row3.X:F3}, {worldMats[lHand].Row3.Y:F3}, {worldMats[lHand].Row3.Z:F3}) | R=({worldMats[rHand].Row3.X:F3}, {worldMats[rHand].Row3.Y:F3}, {worldMats[rHand].Row3.Z:F3})");
                Console.WriteLine($"  Feet:  L=({worldMats[lFoot].Row3.X:F3}, {worldMats[lFoot].Row3.Y:F3}, {worldMats[lFoot].Row3.Z:F3}) | R=({worldMats[rFoot].Row3.X:F3}, {worldMats[rFoot].Row3.Y:F3}, {worldMats[rFoot].Row3.Z:F3})");
            }

            int totalBones = 0;
            int mappedBones = 0;
            foreach (var mesh in scene.Meshes)
            {
                totalBones += mesh.BoneCount;
                for (int b = 0; b < mesh.BoneCount; b++)
                {
                    string bname = mesh.Bones[b].Name;
                    int uo = ResolveBoneToUoTest(bname, uoBoneByName, nameToNode, nodes, parent);
                    if (uo >= 0) mappedBones++;
                    else Console.WriteLine($"  [UNMAPPED BONE] '{bname}' in mesh '{mesh.Name}'");
                }
            }
            Console.WriteLine($"Bone Mapping Result: {mappedBones}/{totalBones} mapped ({(totalBones == 0 ? 100 : (mappedBones * 100 / totalBones))}%)");
        }
    }

    static int ResolveBoneToUoTest(
        string bname,
        System.Collections.Generic.Dictionary<string, int> uoBoneByName,
        System.Collections.Generic.Dictionary<string, int> nameToNode,
        System.Collections.Generic.List<Node> nodes,
        System.Collections.Generic.List<int> nodeParent)
    {
        if (string.IsNullOrEmpty(bname)) return -1;
        if (uoBoneByName.TryGetValue(bname, out int uoIdx)) return uoIdx;

        string clean = bname;
        int col = clean.IndexOf(':');
        if (col >= 0) clean = clean.Substring(col + 1);
        if (uoBoneByName.TryGetValue(clean, out uoIdx)) return uoIdx;

        string norm = NormalizeBiped(bname);
        if (uoBoneByName.TryGetValue(norm, out uoIdx)) return uoIdx;

        // Determine side: Left or Right
        bool isLeft = clean.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      clean.StartsWith("L_", StringComparison.OrdinalIgnoreCase) ||
                      clean.StartsWith("L-", StringComparison.OrdinalIgnoreCase) ||
                      clean.StartsWith("L.", StringComparison.OrdinalIgnoreCase) ||
                      clean.StartsWith("L ", StringComparison.OrdinalIgnoreCase) ||
                      clean.EndsWith("_L", StringComparison.OrdinalIgnoreCase) ||
                      clean.EndsWith(".L", StringComparison.OrdinalIgnoreCase) ||
                      clean.EndsWith("-L", StringComparison.OrdinalIgnoreCase) ||
                      clean.EndsWith("_left", StringComparison.OrdinalIgnoreCase);

        bool isRight = clean.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       clean.StartsWith("R_", StringComparison.OrdinalIgnoreCase) ||
                       clean.StartsWith("R-", StringComparison.OrdinalIgnoreCase) ||
                       clean.StartsWith("R.", StringComparison.OrdinalIgnoreCase) ||
                       clean.StartsWith("R ", StringComparison.OrdinalIgnoreCase) ||
                       clean.EndsWith("_R", StringComparison.OrdinalIgnoreCase) ||
                       clean.EndsWith(".R", StringComparison.OrdinalIgnoreCase) ||
                       clean.EndsWith("-R", StringComparison.OrdinalIgnoreCase) ||
                       clean.EndsWith("_right", StringComparison.OrdinalIgnoreCase);

        string side = isLeft ? "L" : (isRight ? "R" : null);

        // Core spine / torso / head
        string lower = clean.ToLowerInvariant();
        if (lower == "pelvis" || lower == "hips" || lower == "hip")
            if (uoBoneByName.TryGetValue("Bip01 Pelvis", out uoIdx)) return uoIdx;

        if (lower == "spine_01" || lower == "spine1" || lower == "spine")
            if (uoBoneByName.TryGetValue("Bip01 Spine", out uoIdx)) return uoIdx;

        if (lower == "spine_02" || lower == "spine2" || lower == "chest")
            if (uoBoneByName.TryGetValue("Bip01 Spine1", out uoIdx)) return uoIdx;

        if (lower == "spine_03" || lower == "spine3" || lower == "upperchest" || lower == "upper_chest")
            if (uoBoneByName.TryGetValue("Bip01 Spine2", out uoIdx)) return uoIdx;

        if (lower == "neck_01" || lower == "neck")
            if (uoBoneByName.TryGetValue("Bip01 Neck", out uoIdx)) return uoIdx;

        if (lower == "head")
            if (uoBoneByName.TryGetValue("Bip01 Head", out uoIdx)) return uoIdx;

        // Limbs if side is known
        if (side != null)
        {
            if (lower.Contains("clavicle") || lower.Contains("shoulder"))
                if (uoBoneByName.TryGetValue($"Bip01 {side} Clavicle", out uoIdx)) return uoIdx;

            if (lower.Contains("upperarm") || lower.Contains("upper_arm") || lower.Contains("uparm") || (lower.Contains("arm") && !lower.Contains("lower") && !lower.Contains("fore")))
                if (uoBoneByName.TryGetValue($"Bip01 {side} UpperArm", out uoIdx)) return uoIdx;

            if (lower.Contains("lowerarm") || lower.Contains("lower_arm") || lower.Contains("forearm"))
                if (uoBoneByName.TryGetValue($"Bip01 {side} Forearm", out uoIdx)) return uoIdx;

            if (lower.Contains("hand") || lower.Contains("wrist"))
                if (uoBoneByName.TryGetValue($"Bip01 {side} Hand", out uoIdx)) return uoIdx;

            if (lower.Contains("thigh") || lower.Contains("upperleg") || lower.Contains("upper_leg") || lower.Contains("upleg"))
                if (uoBoneByName.TryGetValue($"Bip01 {side} Thigh", out uoIdx)) return uoIdx;

            if (lower.Contains("calf") || lower.Contains("lowerleg") || lower.Contains("lower_leg") || lower.Contains("shin") || (lower.Contains("leg") && !lower.Contains("up") && !lower.Contains("upper")))
                if (uoBoneByName.TryGetValue($"Bip01 {side} Calf", out uoIdx)) return uoIdx;

            if (lower.Contains("foot") || lower.Contains("ankle"))
                if (uoBoneByName.TryGetValue($"Bip01 {side} Foot", out uoIdx)) return uoIdx;

            if (lower.Contains("ball") || lower.Contains("toe"))
                if (uoBoneByName.TryGetValue($"Bip01 {side} Toe0", out uoIdx)) return uoIdx;

            // Fingers
            if (lower.Contains("thumb"))
            {
                bool isBase = lower.Contains("01") || lower.Contains("thumb1");
                string target = isBase ? $"Bip01 {side} Finger0" : $"Bip01 {side} Finger01";
                if (uoBoneByName.TryGetValue(target, out uoIdx)) return uoIdx;
            }

            if (lower.Contains("index") || lower.Contains("middle") || lower.Contains("ring") || lower.Contains("pinky") || lower.Contains("little"))
            {
                bool isBase = lower.Contains("01") || lower.Contains("index1") || lower.Contains("middle1") || lower.Contains("ring1") || lower.Contains("pinky1");
                string target = isBase ? $"Bip01 {side} Finger1" : $"Bip01 {side} Finger11";
                if (uoBoneByName.TryGetValue(target, out uoIdx)) return uoIdx;
            }
        }

        return -1;
    }

    static string NormalizeBiped(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string s = System.Text.RegularExpressions.Regex.Replace(name, @"[-_]+", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^Bip\d*", "Bip01", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s;
    }

    static void Collect(Node n, int p, System.Collections.Generic.List<Node> nodes, System.Collections.Generic.List<int> parent)
    {
        int idx = nodes.Count;
        nodes.Add(n);
        parent.Add(p);
        foreach (var c in n.Children) Collect(c, idx, nodes, parent);
    }

    static Matrix4 ToMatrix4(Assimp.Matrix4x4 m)
    {
        return new Matrix4(
            m.A1, m.B1, m.C1, m.D1,
            m.A2, m.B2, m.C2, m.D2,
            m.A3, m.B3, m.C3, m.D3,
            m.A4, m.B4, m.C4, m.D4
        );
    }
}
