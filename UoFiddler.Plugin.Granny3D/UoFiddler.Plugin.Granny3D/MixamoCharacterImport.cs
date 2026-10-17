using System;
using System.Collections.Generic;
using System.IO;
using Assimp;
using OpenTK.Mathematics;
using Quaternion = OpenTK.Mathematics.Quaternion;

namespace UoFiddler.Plugin.Granny3D
{
    // Imports a Mixamo character FBX (mesh + textures + skinning),
    // retargets/reskins it onto the canonical UO Biped skeleton (H_Male_Lod2.grn),
    // extracts embedded/external textures into the target directory,
    // and saves as a standalone UO Z-up character model (.char).
    public static class MixamoCharacterImport
    {
        // Coordinate bridge: Mixamo Y-up to UO Z-up (+90deg around X)
        // In local vertices: X_uo = X_mix, Y_uo = -Z_mix, Z_uo = Y_mix
        const float Scale = 0.01f; // Mixamo is in cm (height ~180), UO is in meters (height ~1.8)

        public static bool TryImport(string fbxPath, string outPath, out string? error)
        {
            string outFolder = Path.GetDirectoryName(outPath) ?? string.Empty;
            string charName = Path.GetFileNameWithoutExtension(outPath);
            return TryImport(fbxPath, outFolder, null, out var model, out error) &&
                   model != null &&
                   GrannyModelFile.Save(model, outPath);
        }

        public static bool TryImport(string fbxPath, out GrannyModelFile? model, out string? error)
        {
            return TryImport(fbxPath, null, null, out model, out error);
        }

        public static bool TryImport(string fbxPath, string? outFolder, string? uoRefPath, out GrannyModelFile? model, out string? error)
        {
            try
            {
                model = Import(fbxPath, null, outFolder, uoRefPath);
                if (model == null || model.Parts.Count == 0)
                {
                    error = "Aucun mesh valide trouvé dans le fichier FBX.";
                    return false;
                }
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                model = null;
                error = ex.Message;
                return false;
            }
        }

        public static GrannyModelFile? Import(string fbxPath, string? characterName = null, string? outFolder = null, string? uoRefPath = null)
        {
            if (string.IsNullOrEmpty(fbxPath) || !File.Exists(fbxPath)) return null;

            if (string.IsNullOrEmpty(outFolder))
                outFolder = Path.GetDirectoryName(fbxPath) ?? string.Empty;

            // 1) Find and load the reference UO Biped skeleton (H_Male_Lod2.grn or H_Female_Lod2.grn)
            var refBones = LoadReferenceBones(uoRefPath, outFolder);

            using var ctx = new AssimpContext();
            var scene = ctx.ImportFile(fbxPath,
                PostProcessSteps.Triangulate |
                PostProcessSteps.LimitBoneWeights |
                PostProcessSteps.GenerateNormals);

            if (scene == null || scene.MeshCount == 0 || scene.RootNode == null) return null;

            string name = string.IsNullOrEmpty(characterName)
                ? Path.GetFileNameWithoutExtension(fbxPath)
                : characterName;

            var model = new GrannyModelFile { Name = name };

            // Extract all embedded textures from FBX binary and Assimp scene into outFolder
            var extractedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var embedded = ExtractFbxEmbeddedTextures(fbxPath);
            for (int i = 0; i < embedded.Count; i++)
            {
                var (rawFn, data) = embedded[i];
                string savedName = $"{name}_{rawFn}";
                if (!string.IsNullOrEmpty(outFolder))
                {
                    if (!Directory.Exists(outFolder)) Directory.CreateDirectory(outFolder);
                    File.WriteAllBytes(Path.Combine(outFolder, savedName), data);
                }
                extractedMap[rawFn] = savedName;
                extractedMap[Path.GetFileName(rawFn)] = savedName;
                extractedMap[$"*{i}"] = savedName;
            }

            if (scene.TextureCount > 0)
            {
                for (int ti = 0; ti < scene.TextureCount; ti++)
                {
                    var emb = scene.Textures[ti];
                    if (emb != null && emb.IsCompressed && emb.CompressedData != null)
                    {
                        string embFn = $"tex_{ti}.png";
                        string savedName = $"{name}_{embFn}";
                        if (!string.IsNullOrEmpty(outFolder))
                        {
                            if (!Directory.Exists(outFolder)) Directory.CreateDirectory(outFolder);
                            File.WriteAllBytes(Path.Combine(outFolder, savedName), emb.CompressedData);
                        }
                        extractedMap[embFn] = savedName;
                        extractedMap[$"*{ti}"] = savedName;
                    }
                }
            }

            // Set skeleton: if reference bones are found, use the standard 43 UO bones!
            var uoBoneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (refBones != null && refBones.Count > 0)
            {
                model.Bones = new List<GrannyBone>(refBones);
                for (int i = 0; i < refBones.Count; i++)
                    uoBoneByName[refBones[i].Name] = i;
            }

            // 2) Index all scene nodes to allow hierarchy traversal (for fingers/helpers -> parent UO bone)
            var nodes = new List<Node>();
            var nodeParent = new List<int>();
            void Collect(Node n, int par)
            {
                int idx = nodes.Count;
                nodes.Add(n);
                nodeParent.Add(par);
                foreach (var c in n.Children) Collect(c, idx);
            }
            Collect(scene.RootNode, -1);

            int nn = nodes.Count;
            var nameToNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nn; i++)
            {
                if (!nameToNode.ContainsKey(nodes[i].Name))
                    nameToNode[nodes[i].Name] = i;
            }

            // Fallback skeleton if no UO reference was found: build from FBX
            if (model.Bones.Count == 0)
            {
                BuildFallbackSkeleton(scene, nodes, nodeParent, nameToNode, model);
                for (int i = 0; i < model.Bones.Count; i++)
                    uoBoneByName[model.Bones[i].Name] = i;
            }

            // 2b) Compute world matrices of Mixamo FBX scene nodes and joint deltas for UO skeleton
            // 2b) Compute world matrices of Mixamo FBX scene nodes in UO basis
            Matrix4 C = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(90f));
            Matrix4 Cinv = Matrix4.Invert(C);

            var localMix = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                nodes[i].Transform.Decompose(out var sc, out var qq, out var tt);
                localMix[i] = Matrix4.CreateScale(sc.X, sc.Y, sc.Z) *
                              Matrix4.CreateFromQuaternion(new Quaternion(qq.X, qq.Y, qq.Z, qq.W)) *
                              Matrix4.CreateTranslation(tt.X * Scale, tt.Y * Scale, tt.Z * Scale);
            }

            var wMix = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                int p = nodeParent[i];
                wMix[i] = (p >= 0 && p < nn && p != i) ? localMix[i] * wMix[p] : localMix[i];
            }

            var wU = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
                wU[i] = Cinv * wMix[i] * C;

            // Adapt skeleton in model.Bones to match the character's exact proportions:
            // For each bone, retain canonical UO Biped joint rotation (so animation tracks rotate along the exact intended axes)
            // and relocate the joint pivot to the imported character's actual joint position from the FBX.
            if (refBones != null && refBones.Count > 0)
            {
                int nb = refBones.Count;
                var fittedBones = new List<GrannyBone>(nb);
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

                // Reference UO bind world matrices (canonical 3ds Max Biped joint orientations)
                var uoBindWorld = new Matrix4[nb];
                for (int i = 0; i < nb; i++)
                    uoBindWorld[i] = Matrix4.Invert(refBones[i].InverseBindMatrix);

                int FindMixNode(string baseName)
                {
                    if (string.IsNullOrEmpty(baseName)) return -1;
                    if (nameToNode.TryGetValue(baseName, out int idx)) return idx;
                    if (nameToNode.TryGetValue("mixamorig:" + baseName, out idx)) return idx;
                    string clean = baseName;
                    int col = clean.IndexOf(':');
                    if (col >= 0) clean = clean.Substring(col + 1);
                    if (nameToNode.TryGetValue(clean, out idx)) return idx;
                    foreach (var kvp in nameToNode)
                    {
                        string k = kvp.Key;
                        int c = k.IndexOf(':');
                        if (c >= 0) k = k.Substring(c + 1);
                        if (string.Equals(k, clean, StringComparison.OrdinalIgnoreCase))
                            return kvp.Value;
                    }
                    return -1;
                }

                // Fitted world matrices:
                var fittedWorld = new Matrix4[nb];
                for (int i = 0; i < nb; i++)
                {
                    string uoName = refBones[i].Name;
                    fittedWorld[i] = uoBindWorld[i];

                    // Find corresponding Mixamo node.
                    // For the 4-finger cluster (Finger1 / Finger11), prefer the central Middle finger pivot if present, otherwise Index
                    int ni = -1;
                    if (string.Equals(uoName, "Bip01 L Finger1", StringComparison.OrdinalIgnoreCase))
                        ni = FindMixNode("LeftHandMiddle1");
                    else if (string.Equals(uoName, "Bip01 L Finger11", StringComparison.OrdinalIgnoreCase))
                        ni = FindMixNode("LeftHandMiddle2");
                    else if (string.Equals(uoName, "Bip01 R Finger1", StringComparison.OrdinalIgnoreCase))
                        ni = FindMixNode("RightHandMiddle1");
                    else if (string.Equals(uoName, "Bip01 R Finger11", StringComparison.OrdinalIgnoreCase))
                        ni = FindMixNode("RightHandMiddle2");

                    if (ni < 0 && MixamoRetarget.TryGetBoneAlias(uoName, out string mixAlias))
                    {
                        ni = FindMixNode(mixAlias);
                    }

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

                // Recompute local transforms and inverse bind matrices for the fitted skeleton
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

                model.Bones = fittedBones;
                uoBoneByName.Clear();
                for (int i = 0; i < model.Bones.Count; i++)
                    uoBoneByName[model.Bones[i].Name] = i;
            }

            // 3) Process Meshes
            for (int m = 0; m < scene.MeshCount; m++)
            {
                var mesh = scene.Meshes[m];
                if (mesh.VertexCount == 0 || mesh.FaceCount == 0) continue;

                var part = new GrannyModelPart
                {
                    Name = string.IsNullOrEmpty(mesh.Name) ? $"Mesh_{m}" : mesh.Name
                };

                // Material / Color / Texture
                if (mesh.MaterialIndex >= 0 && mesh.MaterialIndex < scene.MaterialCount)
                {
                    var mat = scene.Materials[mesh.MaterialIndex];
                    part.MaterialName = mat.Name ?? string.Empty;
                    var diff = mat.ColorDiffuse;
                    if (diff.R > 0.001f || diff.G > 0.001f || diff.B > 0.001f)
                    {
                        part.Color = new Vector3(diff.R, diff.G, diff.B);
                    }
                    else
                    {
                        part.Color = new Vector3(0.72f, 0.66f, 0.55f);
                    }

                    string texName = null;
                    if (mat.HasTextureDiffuse && !string.IsNullOrEmpty(mat.TextureDiffuse.FilePath))
                    {
                        string fp = mat.TextureDiffuse.FilePath;
                        string fn = Path.GetFileName(fp);

                        if (extractedMap.TryGetValue(fp, out string exName))
                        {
                            texName = exName;
                        }
                        else if (extractedMap.TryGetValue(fn, out exName))
                        {
                            texName = exName;
                        }
                        else
                        {
                            // Check on disk next to FBX
                            string fbxDir = Path.GetDirectoryName(fbxPath) ?? string.Empty;
                            string candidate = Path.Combine(fbxDir, fn);
                            if (File.Exists(candidate))
                            {
                                texName = $"{name}_{fn}";
                                if (!string.IsNullOrEmpty(outFolder))
                                {
                                    if (!Directory.Exists(outFolder)) Directory.CreateDirectory(outFolder);
                                    File.Copy(candidate, Path.Combine(outFolder, texName), true);
                                }
                            }
                            else
                            {
                                // Check if any extracted texture filename matches or contains fn without extension
                                string fnNoExt = Path.GetFileNameWithoutExtension(fn);
                                foreach (var kvp in extractedMap)
                                {
                                    if (kvp.Key.IndexOf(fnNoExt, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        texName = kvp.Value;
                                        break;
                                    }
                                }
                                if (string.IsNullOrEmpty(texName)) texName = fn;
                            }
                        }
                    }

                    // Fallback if no texture found for material but embedded textures exist:
                    // pick diffuse/albedo texture or first extracted texture
                    if (string.IsNullOrEmpty(texName) || (!string.IsNullOrEmpty(outFolder) && !File.Exists(Path.Combine(outFolder, texName))))
                    {
                        string bestDiffuse = null;
                        foreach (var kvp in extractedMap)
                        {
                            if (kvp.Key.StartsWith("*")) continue;
                            if (kvp.Key.IndexOf("diffuse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                kvp.Key.IndexOf("albedo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                kvp.Key.IndexOf("basecolor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                kvp.Key.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                bestDiffuse = kvp.Value;
                                break;
                            }
                        }
                        if (string.IsNullOrEmpty(bestDiffuse) && embedded.Count > 0)
                        {
                            bestDiffuse = extractedMap[embedded[0].Filename];
                        }
                        if (!string.IsNullOrEmpty(bestDiffuse))
                        {
                            texName = bestDiffuse;
                        }
                    }

                    part.TextureName = texName ?? string.Empty;
                }

                // Vertices & Normals (transformed to UO Z-up: X_uo=X, Y_uo=-Z, Z_uo=Y)
                int vcount = mesh.VertexCount;
                part.Vertices = new Vector3[vcount];
                part.Normals = new Vector3[vcount];
                for (int v = 0; v < vcount; v++)
                {
                    var pt = mesh.Vertices[v];
                    part.Vertices[v] = new Vector3(pt.X * Scale, -pt.Z * Scale, pt.Y * Scale);

                    if (mesh.HasNormals)
                    {
                        var n = mesh.Normals[v];
                        var uoNorm = new Vector3(n.X, -n.Z, n.Y);
                        if (uoNorm.LengthSquared > 1e-12f) uoNorm = Vector3.Normalize(uoNorm);
                        part.Normals[v] = uoNorm;
                    }
                    else
                    {
                        part.Normals[v] = Vector3.UnitZ;
                    }
                }

                // UVs (invert V coordinate for standard OpenGL bottom-up vs top-down bitmap)
                part.UVs = new Vector2[vcount];
                if (mesh.HasTextureCoords(0))
                {
                    var uvs = mesh.TextureCoordinateChannels[0];
                    for (int v = 0; v < vcount; v++)
                        part.UVs[v] = new Vector2(uvs[v].X, 1.0f - uvs[v].Y);
                }

                // Indices
                var indices = new List<int>(mesh.FaceCount * 3);
                foreach (var face in mesh.Faces)
                {
                    if (face.IndexCount == 3)
                    {
                        indices.Add(face.Indices[0]);
                        indices.Add(face.Indices[1]);
                        indices.Add(face.Indices[2]);
                    }
                }
                part.Indices = indices.ToArray();

                // Bone mapping: map each FBX mesh bone to a UO skeleton bone
                var meshBoneToUo = new int[mesh.BoneCount];
                for (int bi = 0; bi < mesh.BoneCount; bi++)
                {
                    string bname = mesh.Bones[bi].Name;
                    string cleanBName = bname;
                    int colon = cleanBName.IndexOf(':');
                    if (colon >= 0) cleanBName = cleanBName.Substring(colon + 1);

                    int targetBoneIdx = -1;

                    // Hand and Finger Bone Mapping:
                    // - Thumb: Thumb1 -> Finger0 (base), Thumb2..4 -> Finger01 (phalanges)
                    // - 4 Fingers (Index, Middle / Majeur, Ring / Annulaire, Pinky / Auriculaire):
                    //   Base (Index1, Middle1, Ring1, Pinky1) -> Finger1 (knuckle base)
                    //   Phalanges (Index2..4, Middle2..4, Ring2..4, Pinky2..4) -> Finger11 (phalanges / finger curl)
                    //   This allows all fingers to bend naturally together with the UO animation ("ensemble").
                    // - Wrist / Palm: Hand -> Bip01 L/R Hand
                    bool isLeft = cleanBName.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0 || cleanBName.StartsWith("L_", StringComparison.OrdinalIgnoreCase);
                    bool isRight = cleanBName.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0 || cleanBName.StartsWith("R_", StringComparison.OrdinalIgnoreCase);

                    if (cleanBName.IndexOf("Thumb", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string targetUo = cleanBName.IndexOf("Thumb1", StringComparison.OrdinalIgnoreCase) >= 0
                            ? (isLeft ? "Bip01 L Finger0" : (isRight ? "Bip01 R Finger0" : null))
                            : (isLeft ? "Bip01 L Finger01" : (isRight ? "Bip01 R Finger01" : null));

                        if (targetUo != null && uoBoneByName.TryGetValue(targetUo, out targetBoneIdx))
                        {
                            meshBoneToUo[bi] = targetBoneIdx;
                            continue;
                        }
                    }

                    if (cleanBName.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cleanBName.IndexOf("Middle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cleanBName.IndexOf("Ring", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cleanBName.IndexOf("Pinky", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        bool isBase = cleanBName.IndexOf("Index1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      cleanBName.IndexOf("Middle1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      cleanBName.IndexOf("Ring1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      cleanBName.IndexOf("Pinky1", StringComparison.OrdinalIgnoreCase) >= 0;

                        string targetUo = isBase
                            ? (isLeft ? "Bip01 L Finger1" : (isRight ? "Bip01 R Finger1" : null))
                            : (isLeft ? "Bip01 L Finger11" : (isRight ? "Bip01 R Finger11" : null));

                        if (targetUo != null && uoBoneByName.TryGetValue(targetUo, out targetBoneIdx))
                        {
                            meshBoneToUo[bi] = targetBoneIdx;
                            continue;
                        }
                    }

                    if (cleanBName.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cleanBName.IndexOf("Finger", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string targetUo = isLeft ? "Bip01 L Hand" : (isRight ? "Bip01 R Hand" : null);
                        if (targetUo != null && uoBoneByName.TryGetValue(targetUo, out targetBoneIdx))
                        {
                            meshBoneToUo[bi] = targetBoneIdx;
                            continue;
                        }
                    }

                    if (MixamoRetarget.TryGetBoneAlias(bname, out string uoName) && uoBoneByName.TryGetValue(uoName, out targetBoneIdx))
                    {
                        meshBoneToUo[bi] = targetBoneIdx;
                    }
                    else if (nameToNode.TryGetValue(bname, out int ni))
                    {
                        // Walk up parent hierarchy to find closest ancestor bone mapped to UO skeleton
                        int cur = nodeParent[ni];
                        int found = -1;
                        while (cur >= 0)
                        {
                            string pName = nodes[cur].Name;
                            if (MixamoRetarget.TryGetBoneAlias(pName, out string puo) && uoBoneByName.TryGetValue(puo, out int pUoIdx))
                            {
                                found = pUoIdx;
                                break;
                            }
                            cur = nodeParent[cur];
                        }
                        meshBoneToUo[bi] = found;
                    }
                    else
                    {
                        meshBoneToUo[bi] = -1;
                    }
                }

                // Skin weights
                var skinInfluences = new List<VertexInfluence>[vcount];
                for (int v = 0; v < vcount; v++)
                    skinInfluences[v] = new List<VertexInfluence>();

                for (int bi = 0; bi < mesh.BoneCount; bi++)
                {
                    int uoBone = meshBoneToUo[bi];
                    if (uoBone < 0) continue;

                    var b = mesh.Bones[bi];
                    foreach (var weight in b.VertexWeights)
                    {
                        if (weight.VertexID >= 0 && weight.VertexID < vcount && weight.Weight > 1e-4f)
                        {
                            skinInfluences[weight.VertexID].Add(new VertexInfluence
                            {
                                BoneId = uoBone,
                                Weight = weight.Weight
                            });
                        }
                    }
                }

                part.Skin = new VertexInfluence[vcount][];
                for (int v = 0; v < vcount; v++)
                {
                    var infs = skinInfluences[v];
                    if (infs.Count > 0)
                    {
                        // Combine duplicate bone influences
                        var combined = new Dictionary<int, float>();
                        foreach (var item in infs)
                        {
                            if (combined.ContainsKey(item.BoneId))
                                combined[item.BoneId] += item.Weight;
                            else
                                combined[item.BoneId] = item.Weight;
                        }

                        float sum = 0f;
                        foreach (var val in combined.Values) sum += val;

                        var finalInfs = new List<VertexInfluence>(combined.Count);
                        if (sum > 1e-5f)
                        {
                            foreach (var kvp in combined)
                                finalInfs.Add(new VertexInfluence { BoneId = kvp.Key, Weight = kvp.Value / sum });
                        }
                        else
                        {
                            foreach (var kvp in combined)
                                finalInfs.Add(new VertexInfluence { BoneId = kvp.Key, Weight = kvp.Value });
                        }
                        part.Skin[v] = finalInfs.ToArray();
                    }
                    else
                    {
                        part.Skin[v] = Array.Empty<VertexInfluence>();
                    }
                }



                model.Parts.Add(part);
            }

            return model;
        }

        private static List<GrannyBone>? LoadReferenceBones(string? uoRefPath, string outFolder)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(uoRefPath)) candidates.Add(uoRefPath);

            // Try relative to outFolder (outFolder is typically BasePath\Others)
            string? basePath = null;
            if (!string.IsNullOrEmpty(outFolder))
            {
                var dirInfo = new DirectoryInfo(outFolder);
                if (string.Equals(dirInfo.Name, "Others", StringComparison.OrdinalIgnoreCase) && dirInfo.Parent != null)
                {
                    basePath = dirInfo.Parent.FullName;
                }
                else
                {
                    basePath = outFolder;
                }
            }

            if (!string.IsNullOrEmpty(basePath))
            {
                candidates.Add(Path.Combine(basePath, "Humans", "H_Male_Lod2.grn"));
                candidates.Add(Path.Combine(basePath, "Humans", "H_Female_Lod2.grn"));
            }

            candidates.Add(@"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn");
            candidates.Add(@"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Female_Lod2.grn");

            foreach (var cand in candidates)
            {
                if (File.Exists(cand))
                {
                    var parser = new GrannyFormatParser();
                    if (parser.Load(cand) && parser.Bones != null && parser.Bones.Count > 0)
                        return parser.Bones;
                }
            }

            return null;
        }

        private static void BuildFallbackSkeleton(Scene scene, List<Node> nodes, List<int> nodeParent, Dictionary<string, int> nameToNode, GrannyModelFile model)
        {
            Matrix4 C = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(90f));
            Matrix4 Cinv = Matrix4.Invert(C);
            int nn = nodes.Count;

            var localMix = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                nodes[i].Transform.Decompose(out var sc, out var qq, out var tt);
                localMix[i] = Matrix4.CreateScale(sc.X, sc.Y, sc.Z) *
                              Matrix4.CreateFromQuaternion(new Quaternion(qq.X, qq.Y, qq.Z, qq.W)) *
                              Matrix4.CreateTranslation(tt.X * Scale, tt.Y * Scale, tt.Z * Scale);
            }

            var wMix = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                int p = nodeParent[i];
                wMix[i] = (p >= 0 && p < nn && p != i) ? localMix[i] * wMix[p] : localMix[i];
            }

            var wU = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
                wU[i] = Cinv * wMix[i] * C;

            var boneNodes = new List<int>();
            var boneMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int m = 0; m < scene.MeshCount; m++)
            {
                var mesh = scene.Meshes[m];
                foreach (var b in mesh.Bones)
                {
                    if (!boneMap.ContainsKey(b.Name) && nameToNode.TryGetValue(b.Name, out int ni))
                    {
                        boneMap[b.Name] = boneNodes.Count;
                        boneNodes.Add(ni);
                    }
                }
            }

            var nodeToBone = new Dictionary<int, int>();
            for (int bi = 0; bi < boneNodes.Count; bi++)
                nodeToBone[boneNodes[bi]] = bi;

            var boneParent = new int[boneNodes.Count];
            for (int bi = 0; bi < boneNodes.Count; bi++)
            {
                int ni = boneNodes[bi];
                int cur = nodeParent[ni];
                int foundParent = -1;
                while (cur >= 0)
                {
                    if (nodeToBone.TryGetValue(cur, out int pBone))
                    {
                        foundParent = pBone;
                        break;
                    }
                    cur = nodeParent[cur];
                }
                boneParent[bi] = foundParent;
            }

            for (int bi = 0; bi < boneNodes.Count; bi++)
            {
                int ni = boneNodes[bi];
                int parBone = boneParent[bi];
                int parNode = parBone >= 0 ? boneNodes[parBone] : -1;

                Matrix4 localMatU = (parNode >= 0)
                    ? wU[ni] * Matrix4.Invert(wU[parNode])
                    : wU[ni];

                Matrix4 invBindU = Matrix4.Invert(wU[ni]);

                var bone = new GrannyBone
                {
                    Id = bi,
                    ParentId = parBone,
                    Name = nodes[ni].Name,
                    LocalPosition = localMatU.ExtractTranslation(),
                    LocalRotation = localMatU.ExtractRotation(),
                    InverseBindMatrix = invBindU
                };

                model.Bones.Add(bone);
            }
        }

        public static List<(string Filename, byte[] Data)> ExtractFbxEmbeddedTextures(string fbxPath)
        {
            var result = new List<(string, byte[])>();
            if (string.IsNullOrEmpty(fbxPath) || !File.Exists(fbxPath)) return result;

            try
            {
                byte[] fbx = File.ReadAllBytes(fbxPath);
                byte[] pngSig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                byte[] iendSig = new byte[] { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };
                byte[] jpgSig = new byte[] { 0xFF, 0xD8, 0xFF };
                byte[] jendSig = new byte[] { 0xFF, 0xD9 };

                int pos = 0;
                while (pos < fbx.Length - 8)
                {
                    int pngStart = IndexOfBytes(fbx, pngSig, pos);
                    int jpgStart = IndexOfBytes(fbx, jpgSig, pos);

                    int start = -1;
                    bool isPng = false;

                    if (pngStart >= 0 && (jpgStart < 0 || pngStart < jpgStart))
                    {
                        start = pngStart;
                        isPng = true;
                    }
                    else if (jpgStart >= 0)
                    {
                        start = jpgStart;
                        isPng = false;
                    }
                    else
                    {
                        break;
                    }

                    int end = isPng ? IndexOfBytes(fbx, iendSig, start + 8) : IndexOfBytes(fbx, jendSig, start + 3);
                    if (end < 0)
                    {
                        pos = start + (isPng ? 8 : 3);
                        continue;
                    }

                    end += isPng ? 8 : 2;
                    int length = end - start;
                    if (length <= 0 || start + length > fbx.Length)
                    {
                        pos = start + 8;
                        continue;
                    }

                    byte[] data = new byte[length];
                    Array.Copy(fbx, start, data, 0, length);

                    string ext = isPng ? ".png" : ".jpg";
                    string fn = $"embedded_tex_{result.Count}{ext}";

                    int scanBack = Math.Max(0, start - 500);
                    string snippet = System.Text.Encoding.ASCII.GetString(fbx, scanBack, start - scanBack);
                    var matches = System.Text.RegularExpressions.Regex.Matches(snippet, @"[\w\.-]+\.(png|jpg|jpeg|tga|dds|bmp)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (matches.Count > 0)
                    {
                        fn = matches[matches.Count - 1].Value;
                    }

                    result.Add((fn, data));
                    pos = end;
                }
            }
            catch { }

            return result;
        }

        private static int IndexOfBytes(byte[] src, byte[] pat, int start)
        {
            int max = src.Length - pat.Length;
            for (int i = start; i <= max; i++)
            {
                bool match = true;
                for (int k = 0; k < pat.Length; k++)
                {
                    if (src[i + k] != pat[k]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
