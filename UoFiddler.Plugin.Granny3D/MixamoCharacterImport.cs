using System;
using System.Collections.Generic;
using System.IO;
using Assimp;
using OpenTK.Mathematics;
using System.Text.RegularExpressions;
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

        public static bool TryImport(
            string fbxPath,
            string? outFolder,
            string? uoRefPath,
            out GrannyModelFile? model,
            out string? error,
            IReadOnlyDictionary<string, Vector3>? customJointPositions = null,
            bool forceAutoSkin = false,
            bool asStaticAccessory = false)
        {
            try
            {
                model = Import(fbxPath, null, outFolder, uoRefPath, customJointPositions, forceAutoSkin, asStaticAccessory);
                if (model == null || model.Parts.Count == 0)
                {
                    error = "Aucun mesh valide trouvé dans le fichier 3D.";
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

        public static GrannyModelFile? Import(
            string fbxPath,
            string? characterName = null,
            string? outFolder = null,
            string? uoRefPath = null,
            IReadOnlyDictionary<string, Vector3>? customJointPositions = null,
            bool forceAutoSkin = false,
            bool asStaticAccessory = false)
        {
            if (string.IsNullOrEmpty(fbxPath) || !File.Exists(fbxPath)) return null;

            if (string.IsNullOrEmpty(outFolder))
                outFolder = Path.GetDirectoryName(fbxPath) ?? string.Empty;

            // 1) Find and load reference skeleton (only if not an unrigged static accessory)
            var refBones = asStaticAccessory ? null : LoadReferenceBones(uoRefPath, outFolder);

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
                string cleanFn = Path.GetFileName(rawFn);
                string savedName = $"{name}_{cleanFn}";
                if (!string.IsNullOrEmpty(outFolder))
                {
                    if (!Directory.Exists(outFolder)) Directory.CreateDirectory(outFolder);
                    File.WriteAllBytes(Path.Combine(outFolder, savedName), data);
                }
                extractedMap[rawFn] = savedName;
                extractedMap[cleanFn] = savedName;
                extractedMap[$"*{i}"] = savedName;
            }

            if (scene.TextureCount > 0)
            {
                for (int ti = 0; ti < scene.TextureCount; ti++)
                {
                    if (extractedMap.ContainsKey($"*{ti}")) continue;
                    var emb = scene.Textures[ti];
                    if (emb != null && emb.IsCompressed && emb.CompressedData != null)
                    {
                        string ext = (emb.CompressedData.Length >= 3 && emb.CompressedData[0] == 0xFF && emb.CompressedData[1] == 0xD8 && emb.CompressedData[2] == 0xFF) ? ".jpg" : ".png";
                        string embFn = $"tex_{ti}{ext}";
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

            // 2) Index all scene nodes to allow hierarchy traversal
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

            // 2b) Compute unscaled world matrices of FBX scene nodes in FBX space
            var localMix = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                nodes[i].Transform.Decompose(out var sc, out var qq, out var tt);
                localMix[i] = Matrix4.CreateScale(sc.X, sc.Y, sc.Z) *
                              Matrix4.CreateFromQuaternion(new Quaternion(qq.X, qq.Y, qq.Z, qq.W)) *
                              Matrix4.CreateTranslation(tt.X, tt.Y, tt.Z);
            }

            var wMix = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                int p = nodeParent[i];
                wMix[i] = (p >= 0 && p < nn && p != i) ? localMix[i] * wMix[p] : localMix[i];
            }

            // Determine unit scale automatically (Mixamo uses cm ~180, 3ds Max often uses mm ~1800-2500, or dm ~25)
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int m = 0; m < scene.MeshCount; m++)
            {
                var mesh = scene.Meshes[m];
                Matrix4 meshMat = Matrix4.Identity;
                for (int ni = 0; ni < nn; ni++)
                {
                    if (nodes[ni].MeshIndices != null && nodes[ni].MeshIndices.Contains(m))
                    {
                        meshMat = wMix[ni];
                        break;
                    }
                }
                foreach (var v in mesh.Vertices)
                {
                    var wv = new Vector4(v.X, v.Y, v.Z, 1.0f) * meshMat;
                    if (wv.X < minX) minX = wv.X; if (wv.X > maxX) maxX = wv.X;
                    if (wv.Y < minY) minY = wv.Y; if (wv.Y > maxY) maxY = wv.Y;
                    if (wv.Z < minZ) minZ = wv.Z; if (wv.Z > maxZ) maxZ = wv.Z;
                }
            }

            if (minX == float.MaxValue)
            {
                for (int ni = 0; ni < nn; ni++)
                {
                    var p = wMix[ni].Row3;
                    if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                    if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
                }
            }

            float dy = (minY != float.MaxValue) ? (maxY - minY) : 0f;
            float dz = (minZ != float.MaxValue) ? (maxZ - minZ) : 0f;
            bool isZUp = dz > dy * 1.5f;
            float rawHeight = isZUp ? dz : dy;

            float currentScale = Scale; // default 0.01f
            if (rawHeight > 500f) currentScale = 0.001f;      // mm -> meters
            else if (rawHeight > 50f) currentScale = 0.01f;   // cm -> meters
            else if (rawHeight > 5f) currentScale = 0.1f;     // dm -> meters
            else if (rawHeight > 0.5f) currentScale = 1.0f;   // m -> meters

            Matrix4 C = isZUp ? Matrix4.Identity : Matrix4.CreateRotationX(MathHelper.DegreesToRadians(90f));
            Matrix4 Cinv = Matrix4.Invert(C);

            var wU = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                var mat = isZUp ? wMix[i] : (Cinv * wMix[i] * C);
                mat.Row3.X *= currentScale;
                mat.Row3.Y *= currentScale;
                mat.Row3.Z *= currentScale;
                wU[i] = mat;
            }





            // Adapt skeleton in model.Bones to match the character's exact proportions:
            var uoBoneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (refBones != null && refBones.Count > 0)
            {
                int nb = refBones.Count;
                for (int i = 0; i < nb; i++)
                    uoBoneByName[refBones[i].Name] = i;

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
                    string normBase = NormalizeBipedBoneName(baseName);
                    foreach (var kvp in nameToNode)
                    {
                        string k = kvp.Key;
                        int c = k.IndexOf(':');
                        if (c >= 0) k = k.Substring(c + 1);
                        if (string.Equals(k, clean, StringComparison.OrdinalIgnoreCase))
                            return kvp.Value;
                        if (string.Equals(NormalizeBipedBoneName(k), normBase, StringComparison.OrdinalIgnoreCase))
                            return kvp.Value;
                    }
                    if (MixamoRetarget.TryGetBoneAlias(baseName, out string alias) && nameToNode.TryGetValue(alias, out idx))
                        return idx;

                    int targetUo = uoBoneByName.TryGetValue(baseName, out int u) ? u : (MixamoRetarget.TryGetBoneAlias(baseName, out string uoA) && uoBoneByName.TryGetValue(uoA, out int u2) ? u2 : -1);

                    for (int nidx = 0; nidx < nn; nidx++)
                    {
                        if (nodes[nidx].Name.IndexOf("_$AssimpFbx$_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        int res = ResolveBoneToUo(nodes[nidx].Name, uoBoneByName, null, null, null);
                        if (res >= 0 && refBones != null && res < refBones.Count)
                        {
                            if (res == targetUo || string.Equals(refBones[res].Name, baseName, StringComparison.OrdinalIgnoreCase))
                                return nidx;
                        }
                    }

                    return -1;
                }

                // Pass 1: Determine initial fitted world positions
                var fittedWorld = new Matrix4[nb];
                for (int i = 0; i < nb; i++)
                {
                    string uoName = refBones[i].Name;
                    fittedWorld[i] = uoBindWorld[i];

                    if (string.Equals(uoName, "__Root", StringComparison.OrdinalIgnoreCase) ||
                        uoName.StartsWith("master_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Find corresponding Mixamo or Biped node.
                    int ni = -1;
                    if (string.Equals(uoName, "Bip01 L Finger0", StringComparison.OrdinalIgnoreCase))
                    {
                        ni = FindMixNode("LeftHandThumb1");
                        if (ni < 0) ni = FindMixNode("thumb_01_l");
                        if (ni < 0) ni = FindMixNode("thumb_l");
                    }
                    else if (string.Equals(uoName, "Bip01 L Finger01", StringComparison.OrdinalIgnoreCase))
                    {
                        ni = FindMixNode("LeftHandThumb2");
                        if (ni < 0) ni = FindMixNode("thumb_02_l");
                    }
                    else if (string.Equals(uoName, "Bip01 L Finger1", StringComparison.OrdinalIgnoreCase))
                    {
                        ni = FindMixNode("LeftHandIndex1");
                        if (ni < 0) ni = FindMixNode("index_01_l");
                        if (ni < 0) ni = FindMixNode("LeftHandMiddle1");
                        if (ni < 0) ni = FindMixNode("middle_01_l");
                    }
                    else if (string.Equals(uoName, "Bip01 L Finger11", StringComparison.OrdinalIgnoreCase))
                    {
                        ni = FindMixNode("LeftHandIndex2");
                        if (ni < 0) ni = FindMixNode("index_02_l");
                        if (ni < 0) ni = FindMixNode("LeftHandMiddle2");
                        if (ni < 0) ni = FindMixNode("middle_02_l");
                    }
                    else if (string.Equals(uoName, "Bip01 R Finger0", StringComparison.OrdinalIgnoreCase))
                    {
                        ni = FindMixNode("RightHandThumb1");
                        if (ni < 0) ni = FindMixNode("thumb_01_r");
                        if (ni < 0) ni = FindMixNode("thumb_r");
                    }
                    else if (string.Equals(uoName, "Bip01 R Finger01", StringComparison.OrdinalIgnoreCase))
                    {
                        ni = FindMixNode("RightHandThumb2");
                        if (ni < 0) ni = FindMixNode("thumb_02_r");
                    }
                    else if (string.Equals(uoName, "Bip01 R Finger1", StringComparison.OrdinalIgnoreCase))
                    {
                        ni = FindMixNode("RightHandIndex1");
                        if (ni < 0) ni = FindMixNode("index_01_r");
                        if (ni < 0) ni = FindMixNode("RightHandMiddle1");
                        if (ni < 0) ni = FindMixNode("middle_01_r");
                    }
                    else if (string.Equals(uoName, "Bip01 R Finger11", StringComparison.OrdinalIgnoreCase))
                    {
                        ni = FindMixNode("RightHandIndex2");
                        if (ni < 0) ni = FindMixNode("index_02_r");
                        if (ni < 0) ni = FindMixNode("RightHandMiddle2");
                        if (ni < 0) ni = FindMixNode("middle_02_r");
                    }

                    if (ni < 0 && MixamoRetarget.TryGetBoneAlias(uoName, out string mixAlias))
                    {
                        ni = FindMixNode(mixAlias);
                    }
                    if (ni < 0)
                    {
                        ni = FindMixNode(uoName);
                    }

                    if (customJointPositions != null && customJointPositions.TryGetValue(uoName, out Vector3 customPos))
                    {
                        var rot = uoBindWorld[i].ExtractRotation();
                        fittedWorld[i] = Matrix4.CreateFromQuaternion(rot) * Matrix4.CreateTranslation(customPos);
                    }
                    else if (ni >= 0)
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

                // Pass 2: Aim directional bones towards their child joints
                for (int i = 0; i < nb; i++)
                {
                    string uoName = refBones[i].Name;
                    string? childName = GetAimChildBoneName(uoName);
                    if (childName != null && uoBoneByName.TryGetValue(childName, out int childIdx))
                    {
                        Vector3 pSelfRef = uoBindWorld[i].Row3.Xyz;
                        Vector3 pChildRef = uoBindWorld[childIdx].Row3.Xyz;
                        Vector3 vRef = pChildRef - pSelfRef;

                        Vector3 pSelfTgt = fittedWorld[i].Row3.Xyz;
                        Vector3 pChildTgt = fittedWorld[childIdx].Row3.Xyz;
                        Vector3 vTgt = pChildTgt - pSelfTgt;

                        if (vRef.LengthSquared > 1e-4f && vTgt.LengthSquared > 1e-4f)
                        {
                            vRef.Normalize();
                            vTgt.Normalize();
                            Quaternion qAim = RotationBetweenVectors(vRef, vTgt);
                            var rot = uoBindWorld[i].ExtractRotation();
                            Matrix4 baseRot = Matrix4.CreateFromQuaternion(rot) * Matrix4.CreateFromQuaternion(qAim);
                            fittedWorld[i] = baseRot * Matrix4.CreateTranslation(pSelfTgt);
                        }
                    }
                }

                // Pass 3: Distal bones without child joints inherit parent's rotation
                for (int i = 0; i < nb; i++)
                {
                    string uoName = refBones[i].Name;
                    bool hasDirectSource = (customJointPositions != null && customJointPositions.ContainsKey(uoName));
                    if (!hasDirectSource)
                    {
                        string? childName = GetAimChildBoneName(uoName);
                        if (childName == null)
                        {
                            int p = refBones[i].ParentId;
                            if (p >= 0 && p < nb && p != i)
                            {
                                var uoLocal = uoBindWorld[i] * Matrix4.Invert(uoBindWorld[p]);
                                fittedWorld[i] = uoLocal * fittedWorld[p];
                            }
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
                for (int i = 0; i < model.Bones.Count; i++)
                    uoBoneByName[model.Bones[i].Name] = i;
            }
            else if (asStaticAccessory)
            {
                model.Bones = new List<GrannyBone>();
            }
            else
            {
                BuildFallbackSkeleton(scene, nodes, nodeParent, nameToNode, model, wU);
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
                    string cleanMesh = !string.IsNullOrEmpty(mesh.Name) ? Granny3DControl.MakeSafeFilename(mesh.Name) : $"mesh_{m}";
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
                            // Check on disk next to FBX (direct or relative)
                            string fbxDir = Path.GetDirectoryName(fbxPath) ?? string.Empty;
                            string? foundCandidate = null;

                            // 1) Direct file next to FBX
                            if (File.Exists(Path.Combine(fbxDir, fn))) foundCandidate = Path.Combine(fbxDir, fn);
                            // 2) Exact relative path
                            else if (File.Exists(Path.Combine(fbxDir, fp))) foundCandidate = Path.Combine(fbxDir, fp);
                            else
                            {
                                // 3) Strip leading folder (e.g. "Texture\Spear\BaseColor.png" -> "Spear\BaseColor.png")
                                string normFp = fp.Replace('/', '\\');
                                int slash = normFp.IndexOf('\\');
                                if (slash >= 0)
                                {
                                    string stripped = normFp.Substring(slash + 1);
                                    if (File.Exists(Path.Combine(fbxDir, stripped))) foundCandidate = Path.Combine(fbxDir, stripped);
                                }
                            }

                            if (foundCandidate != null)
                            {
                                string ext = Path.GetExtension(foundCandidate);
                                string candParent = Path.GetFileName(Path.GetDirectoryName(foundCandidate));
                                string tag = (!string.IsNullOrEmpty(candParent) && !string.Equals(candParent, Path.GetFileName(fbxDir), StringComparison.OrdinalIgnoreCase))
                                    ? $"{cleanMesh}_{candParent}"
                                    : cleanMesh;
                                texName = $"{name}_{tag}{ext}";
                                if (!string.IsNullOrEmpty(outFolder))
                                {
                                    if (!Directory.Exists(outFolder)) Directory.CreateDirectory(outFolder);
                                    File.Copy(foundCandidate, Path.Combine(outFolder, texName), true);
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
                            }
                        }
                    }

                    // Check disk near FBX and in textures/ subfolders with smart scoring (prioritizing BaseColor/Diffuse and ignoring Normal/Spec/Roughness)
                    if (string.IsNullOrEmpty(texName) || (!string.IsNullOrEmpty(outFolder) && !File.Exists(Path.Combine(outFolder, texName))))
                    {
                        string? diskTex = FindMatchingTextureOnDisk(fbxPath, mesh.Name, mat?.Name, mat?.HasTextureDiffuse == true ? mat.TextureDiffuse.FilePath : null, outFolder, name);
                        if (!string.IsNullOrEmpty(diskTex))
                        {
                            texName = diskTex;
                        }
                    }


                    // Fallback if no texture found on disk or material but embedded textures exist:
                    // pick diffuse/albedo texture or first extracted texture
                    if (string.IsNullOrEmpty(texName) || (!string.IsNullOrEmpty(outFolder) && !File.Exists(Path.Combine(outFolder, texName))))
                    {
                        string bestDiffuse = null;
                        foreach (var kvp in extractedMap)
                        {
                            if (kvp.Key.StartsWith("*")) continue;
                            string kLower = kvp.Key.ToLowerInvariant();
                            if (kLower.Contains("normal") || kLower.Contains("spec") || kLower.Contains("rough") || kLower.Contains("metal") || kLower.Contains("ao")) continue;

                            if (kLower.Contains("diffuse") ||
                                kLower.Contains("albedo") ||
                                kLower.Contains("basecolor") ||
                                kLower.Contains("color"))
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

                // Find node containing this mesh to get its world transform
                Matrix4 meshNodeWorld = Matrix4.Identity;
                for (int ni = 0; ni < nn; ni++)
                {
                    if (nodes[ni].MeshIndices != null && nodes[ni].MeshIndices.Contains(m))
                    {
                        meshNodeWorld = wMix[ni];
                        break;
                    }
                }

                // Vertices & Normals (transformed to UO Z-up with unit scaling)
                int vcount = mesh.VertexCount;
                part.Vertices = new Vector3[vcount];
                part.Normals = new Vector3[vcount];
                for (int v = 0; v < vcount; v++)
                {
                    var pt = mesh.Vertices[v];
                    var vWorld = new Vector4(pt.X, pt.Y, pt.Z, 1.0f) * meshNodeWorld;
                    if (isZUp)
                    {
                        part.Vertices[v] = new Vector3(vWorld.X * currentScale, vWorld.Y * currentScale, vWorld.Z * currentScale);
                        if (mesh.HasNormals)
                        {
                            var n = mesh.Normals[v];
                            var nw = new Vector4(n.X, n.Y, n.Z, 0.0f) * meshNodeWorld;
                            var uoNorm = new Vector3(nw.X, nw.Y, nw.Z);
                            if (uoNorm.LengthSquared > 1e-12f) uoNorm = Vector3.Normalize(uoNorm);
                            part.Normals[v] = uoNorm;
                        }
                        else
                        {
                            part.Normals[v] = Vector3.UnitZ;
                        }
                    }
                    else
                    {
                        part.Vertices[v] = new Vector3(vWorld.X * currentScale, -vWorld.Z * currentScale, vWorld.Y * currentScale);
                        if (mesh.HasNormals)
                        {
                            var n = mesh.Normals[v];
                            var nw = new Vector4(n.X, n.Y, n.Z, 0.0f) * meshNodeWorld;
                            var uoNorm = new Vector3(nw.X, -nw.Z, nw.Y);
                            if (uoNorm.LengthSquared > 1e-12f) uoNorm = Vector3.Normalize(uoNorm);
                            part.Normals[v] = uoNorm;
                        }
                        else
                        {
                            part.Normals[v] = Vector3.UnitZ;
                        }
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
                    meshBoneToUo[bi] = ResolveBoneToUo(bname, uoBoneByName, nameToNode, nodes, nodeParent);
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



                    if (asStaticAccessory)
                    {
                        part.Skin = null;
                        part.CutoutRole = MeshCutoutRole.Accessory;
                    }
                    else
                    {
                        bool hasSkin = false;
                        for (int v = 0; v < part.Skin.Length; v++)
                        {
                            if (part.Skin[v] != null && part.Skin[v].Length > 0)
                            {
                                hasSkin = true;
                                break;
                            }
                        }

                        if (!hasSkin || forceAutoSkin)
                        {
                            ComputeVolumetricSkinning(part, model.Bones);
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

            candidates.Add(Path.Combine(Granny3DControl.BasePath, "Humans", "H_Male_Lod2.grn"));
            candidates.Add(Path.Combine(Granny3DControl.BasePath, "Humans", "H_Female_Lod2.grn"));

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

        private static void BuildFallbackSkeleton(Scene scene, List<Node> nodes, List<int> nodeParent, Dictionary<string, int> nameToNode, GrannyModelFile model, Matrix4[] wU)
        {

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

        /// <summary>
        /// Scanne le dossier du FBX et ses sous-dossiers dédiés (textures, Textures, tex, <fbxName>.fbm...) pour trouver
        /// la texture BaseColor / Diffuse la plus pertinente pour un mesh / matériau donné,
        /// en excluant rigoureusement les textures Normal, Specular, Roughness, Metallic, AO, etc.
        /// Ne scanne JAMAIS aveuglément tous les sous-dossiers de dossiers généraux (Downloads, Bureau...).
        /// </summary>
        public static string? FindMatchingTextureOnDisk(
            string fbxPath,
            string? meshName,
            string? matName,
            string? embeddedRefPath,
            string? outFolder,
            string charName)
        {
            if (string.IsNullOrEmpty(fbxPath) || !File.Exists(fbxPath)) return null;
            string fbxDir = Path.GetDirectoryName(fbxPath) ?? string.Empty;
            if (string.IsNullOrEmpty(fbxDir) || !Directory.Exists(fbxDir)) return null;

            var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".tga", ".jpg", ".jpeg", ".bmp", ".dds"
            };

            var candidateFiles = new List<string>();
            try
            {
                // 1) Fichiers situés directement dans le dossier du FBX (TopDirectoryOnly)
                foreach (var f in Directory.GetFiles(fbxDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (supportedExts.Contains(Path.GetExtension(f)))
                    {
                        candidateFiles.Add(f);
                    }
                }

                // 2) Sous-dossiers connus et dédiés aux textures
                string fbxBase = Path.GetFileNameWithoutExtension(fbxPath);
                string[] knownSubfolders = new[]
                {
                    "textures", "Textures", "tex", "Tex", "texture", "Texture", "images", "Images",
                    $"{fbxBase}.fbm", $"{fbxBase}_textures", $"{fbxBase}_Textures"
                };

                foreach (var sub in knownSubfolders)
                {
                    string subPath = Path.Combine(fbxDir, sub);
                    if (Directory.Exists(subPath))
                    {
                        foreach (var f in Directory.GetFiles(subPath, "*.*", SearchOption.TopDirectoryOnly))
                        {
                            if (supportedExts.Contains(Path.GetExtension(f)))
                            {
                                candidateFiles.Add(f);
                            }
                        }
                    }
                }

                // 2b) Dossier de destination outFolder (si des textures y ont été extraites ou converties)
                if (!string.IsNullOrEmpty(outFolder) && Directory.Exists(outFolder))
                {
                    foreach (var f in Directory.GetFiles(outFolder, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        if (supportedExts.Contains(Path.GetExtension(f)) && !candidateFiles.Contains(f))
                        {
                            candidateFiles.Add(f);
                        }
                    }
                }

                // 3) Chemins relatifs dérivés de embeddedRefPath (ex: "Texture\Spear\BaseColor.png" -> "Spear\BaseColor.png")
                if (!string.IsNullOrEmpty(embeddedRefPath))
                {
                    string p1 = Path.Combine(fbxDir, embeddedRefPath);
                    if (File.Exists(p1) && supportedExts.Contains(Path.GetExtension(p1)) && !candidateFiles.Contains(p1))
                        candidateFiles.Add(p1);

                    string normEmb = embeddedRefPath.Replace('/', '\\');
                    int slash = normEmb.IndexOf('\\');
                    if (slash >= 0)
                    {
                        string subRel = normEmb.Substring(slash + 1);
                        string p2 = Path.Combine(fbxDir, subRel);
                        if (File.Exists(p2) && supportedExts.Contains(Path.GetExtension(p2)) && !candidateFiles.Contains(p2))
                            candidateFiles.Add(p2);
                    }
                }

                // 4) Sous-dossiers immédiats si le dossier n'est pas un répertoire générique (ex: "1", "CrossBow", "Spear")
                string dirName = Path.GetFileName(fbxDir);
                bool isGenericDir = dirName.Equals("Downloads", StringComparison.OrdinalIgnoreCase) ||
                                    dirName.Equals("Desktop", StringComparison.OrdinalIgnoreCase) ||
                                    dirName.Equals("Bureau", StringComparison.OrdinalIgnoreCase) ||
                                    dirName.Equals("Téléchargements", StringComparison.OrdinalIgnoreCase) ||
                                    dirName.Equals("Temp", StringComparison.OrdinalIgnoreCase);
                if (!isGenericDir)
                {
                    foreach (var sub in Directory.GetDirectories(fbxDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        string sName = Path.GetFileName(sub);
                        if (sName.StartsWith(".") || sName.Equals("bin", StringComparison.OrdinalIgnoreCase) || sName.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var f in Directory.GetFiles(sub, "*.*", SearchOption.TopDirectoryOnly))
                        {
                            if (supportedExts.Contains(Path.GetExtension(f)) && !candidateFiles.Contains(f))
                            {
                                candidateFiles.Add(f);
                            }
                        }
                    }
                }
            }
            catch { }


            if (candidateFiles.Count == 0) return null;

            string? bestFile = null;
            int bestScore = 0;

            string? embFnWithoutExt = !string.IsNullOrEmpty(embeddedRefPath)
                ? Path.GetFileNameWithoutExtension(embeddedRefPath).ToLowerInvariant()
                : null;

            string mNameLower = (meshName ?? string.Empty).ToLowerInvariant();
            string matNameLower = (matName ?? string.Empty).ToLowerInvariant();
            string cNameLower = (charName ?? string.Empty).ToLowerInvariant();
            string fbxBaseLower = Path.GetFileNameWithoutExtension(fbxPath).ToLowerInvariant();

            bool nameHasAlpha = matNameLower.Contains("alpha") ||
                                mNameLower.Contains("alpha") ||
                                cNameLower.Contains("alpha") ||
                                fbxBaseLower.Contains("alpha");

            foreach (var filePath in candidateFiles)
            {
                string fn = Path.GetFileNameWithoutExtension(filePath);
                string fnLower = fn.ToLowerInvariant();

                // 1) EXCLUSIONS STRICTES (Normal, Specular, Roughness, Metallic, AO, Height, Bump, Opacity...)
                bool isExcluded =
                    fnLower.Contains("normal") || fnLower.Contains("_norm") || fnLower.Contains("_nor") ||
                    fnLower.Contains("_n.") || fnLower.EndsWith("_n") || fnLower.Contains("-normal") ||
                    fnLower.Contains("_n_") || fnLower.Contains("nrm") ||
                    fnLower.Contains("spec") || fnLower.Contains("specular") || fnLower.EndsWith("_s") ||
                    fnLower.Contains("_s_") ||
                    fnLower.Contains("rough") || fnLower.Contains("roughness") || fnLower.EndsWith("_r") ||
                    fnLower.Contains("_r_") ||
                    fnLower.Contains("metal") || fnLower.Contains("metallic") || fnLower.Contains("metalness") ||
                    fnLower.EndsWith("_m") || fnLower.Contains("_m_") ||
                    fnLower.Contains("ambientocclusion") || fnLower.Contains("ambient_occlusion") ||
                    fnLower.EndsWith("_ao") || fnLower.Contains("_ao_") || fnLower.Contains("occlusion") ||
                    fnLower.Contains("height") || fnLower.Contains("displacement") || fnLower.Contains("_disp") ||
                    fnLower.Contains("bump") ||
                    fnLower.Contains("opacity") ||
                    (!nameHasAlpha && (fnLower.EndsWith("_alpha") || fnLower.Contains("_alpha.") || fnLower.Contains("-alpha") || fnLower.EndsWith("_a") || fnLower.Contains("_a."))) ||
                    fnLower.Contains("transparency") ||
                    fnLower.Contains("emission") || fnLower.Contains("emissive") || fnLower.Contains("glow") ||
                    fnLower.Contains("gloss") || fnLower.Contains("glossiness");

                if (isExcluded) continue;

                int score = 0;
                bool hasContextMatch = false;

                var genericTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "mat", "material", "body", "mesh", "object", "surface", "default", "model",
                    "char", "part", "sub", "main", "base", "color", "tex", "texture", "lod"
                };
                if (!nameHasAlpha) genericTokens.Add("alpha");

                // Split target file name into tokens for exact word matching
                var fnTokens = new HashSet<string>(fnLower.Split(new[] { '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

                // Exact match between filename and charName + meshName (e.g. T-Pose_Alpha_Surface.png)
                if (!string.IsNullOrEmpty(cNameLower) && !string.IsNullOrEmpty(mNameLower) &&
                    fnLower.Contains(cNameLower) && fnLower.Contains(mNameLower))
                {
                    score += 700;
                    hasContextMatch = true;
                }

                // 2) CORRESPONDANCE AVEC LA RÉFÉRENCE ASSIMP (mat.TextureDiffuse)
                if (!string.IsNullOrEmpty(embFnWithoutExt))
                {
                    if (fnLower == embFnWithoutExt) { score += 600; hasContextMatch = true; }
                    else if (fnTokens.Contains(embFnWithoutExt) || fnLower.Contains(embFnWithoutExt)) { score += 350; hasContextMatch = true; }
                }

                // 3) CORRESPONDANCE AVEC LE NOM DU MESH
                if (!string.IsNullOrEmpty(mNameLower))
                {
                    if (fnLower == mNameLower || fnTokens.Contains(mNameLower)) { score += 300; hasContextMatch = true; }
                    var tokens = mNameLower.Split(new[] { '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var tok in tokens)
                    {
                        if (tok.Length >= 3 && !genericTokens.Contains(tok) && fnTokens.Contains(tok)) { score += 120; hasContextMatch = true; }
                    }
                }

                // 4) CORRESPONDANCE AVEC LE NOM DU MATÉRIAU
                if (!string.IsNullOrEmpty(matNameLower))
                {
                    if (fnLower == matNameLower || fnTokens.Contains(matNameLower)) { score += 300; hasContextMatch = true; }
                    var tokens = matNameLower.Split(new[] { '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var tok in tokens)
                    {
                        if (tok.Length >= 3 && !genericTokens.Contains(tok) && fnTokens.Contains(tok)) { score += 120; hasContextMatch = true; }
                    }
                }

                // 5) CORRESPONDANCE AVEC LE NOM DU CHARACTER OU DU FBX
                if (!string.IsNullOrEmpty(cNameLower) && !cNameLower.Equals("t-pose") && (fnTokens.Contains(cNameLower) || fnLower.Contains(cNameLower)))
                {
                    score += 150;
                    hasContextMatch = true;
                }
                if (!string.IsNullOrEmpty(fbxBaseLower) && !fbxBaseLower.Equals("t-pose") && (fnTokens.Contains(fbxBaseLower) || fnLower.Contains(fbxBaseLower)))
                {
                    score += 150;
                    hasContextMatch = true;
                }

                // 5b) Correspondance avec le sous-dossier (ex: dossier "Spear", "CrossBow", "1" qui correspond à embeddedRefPath ou meshName)
                string parentDir = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "";
                if (!string.IsNullOrEmpty(parentDir) && !string.IsNullOrEmpty(embeddedRefPath) &&
                    embeddedRefPath.IndexOf(parentDir, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 500;
                    hasContextMatch = true;
                }
                else if (!string.IsNullOrEmpty(parentDir) && !string.IsNullOrEmpty(mNameLower) &&
                         mNameLower.IndexOf(parentDir, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 350;
                    hasContextMatch = true;
                }

                string relPath = Path.GetRelativePath(fbxDir, filePath).ToLowerInvariant();
                bool isDedicatedSubfolder = relPath.Contains(Path.DirectorySeparatorChar) || relPath.Contains('/') || relPath.Contains('\\');

                // Si le fichier est à la racine et n'a AUCUN rapport de nom avec le modèle/matériau, ON L'IGNORE COMPLÈTEMENT
                // (évite d'associer des images inconnues téléchargées dans Downloads)
                if (!hasContextMatch && !isDedicatedSubfolder)
                {
                    continue;
                }

                // 6) MOTS-CLÉS DIFFUSE / BASECOLOR (ajoute du score mais ne suffit pas à lui seul sans contexte)
                if (fnLower.Contains("basecolor") || fnLower.Contains("base_color") ||
                    fnLower.Contains("basecolour") || fnLower.Contains("base_colour") ||
                    fnLower.EndsWith("_bc") || fnLower.Contains("_bc_"))
                {
                    score += 100;
                }
                else if (fnLower.Contains("diffuse") || fnLower.Contains("_diff") ||
                         fnLower.EndsWith("_d") || fnLower.Contains("_d_") || fnLower.Contains("_dif"))
                {
                    score += 80;
                }
                else if (fnLower.Contains("albedo") || fnLower.Contains("_alb"))
                {
                    score += 80;
                }
                else if (fnLower.Contains("color") || fnLower.Contains("colour") || fnLower.Contains("_col"))
                {
                    score += 50;
                }

                if (isDedicatedSubfolder) score += 30;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestFile = filePath;
                }
            }

            if (bestFile != null && bestScore >= 100)
            {
                string ext = Path.GetExtension(bestFile);
                string cleanMesh = !string.IsNullOrEmpty(meshName) ? Granny3DControl.MakeSafeFilename(meshName) : "mesh";
                string destName;
                if (!string.IsNullOrEmpty(outFolder) && string.Equals(Path.GetDirectoryName(bestFile), outFolder, StringComparison.OrdinalIgnoreCase))
                {
                    destName = Path.GetFileName(bestFile);
                }
                else
                {
                    string candParent = Path.GetFileName(Path.GetDirectoryName(bestFile));
                    bool isCandParentOutFolder = !string.IsNullOrEmpty(outFolder) && string.Equals(candParent, Path.GetFileName(outFolder), StringComparison.OrdinalIgnoreCase);
                    string tag = (!string.IsNullOrEmpty(candParent) && !string.Equals(candParent, Path.GetFileName(fbxDir), StringComparison.OrdinalIgnoreCase) && !isCandParentOutFolder)
                        ? $"{cleanMesh}_{candParent}"
                        : cleanMesh;
                    destName = $"{charName}_{tag}{ext}";
                }


                if (!string.IsNullOrEmpty(outFolder))
                {
                    if (!Directory.Exists(outFolder)) Directory.CreateDirectory(outFolder);
                    string destPath = Path.Combine(outFolder, destName);
                    if (!File.Exists(destPath) || !string.Equals(bestFile, destPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(bestFile, destPath, true);
                    }
                }

                return destName;
            }

            return null;
        }

        public static string NormalizeBipedBoneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int col = name.IndexOf(':');
            if (col >= 0) name = name.Substring(col + 1);
            // Replace Bip001 or Bip01 or Bip2 etc. with Bip01
            name = Regex.Replace(name, @"^Bip\d+[-_ ]", "Bip01 ", RegexOptions.IgnoreCase);
            // Replace "Bip01 L-" or "Bip01-L-" or "Bip01_L_" or "Bip01 L " with "Bip01 L "
            name = Regex.Replace(name, @"^Bip01\s+([LR])[-_ ]", "Bip01 $1 ", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"[-_]+", " ");
            return name.Trim();
        }

        public static int ResolveBoneToUo(
            string bname,
            Dictionary<string, int> uoBoneByName,
            Dictionary<string, int> nameToNode,
            List<Node> nodes,
            List<int> nodeParent)
        {
            if (string.IsNullOrEmpty(bname)) return -1;
            if (bname.IndexOf("_$AssimpFbx$_", StringComparison.OrdinalIgnoreCase) >= 0) return -1;

            // 1) Direct match in UO skeleton
            if (uoBoneByName.TryGetValue(bname, out int uoIdx)) return uoIdx;

            // 2) Clean prefix (mixamorig: or namespace)
            string clean = bname;
            int col = clean.IndexOf(':');
            if (col >= 0) clean = clean.Substring(col + 1);
            if (uoBoneByName.TryGetValue(clean, out uoIdx)) return uoIdx;

            // 3) Normalized Biped name (e.g. Bip001-Pelvis -> Bip01 Pelvis)
            string norm = NormalizeBipedBoneName(bname);
            if (uoBoneByName.TryGetValue(norm, out uoIdx)) return uoIdx;

            // 4) 3ds Max Biped finger mapping:
            // e.g. Bip001-L-Finger0, Bip001-L-Finger01, Bip001-L-Finger2, Bip001-L-Finger21...
            var fingerMatch = Regex.Match(bname, @"Bip\d*[-_ ]?([LR])[-_ ]?Finger(\d+)", RegexOptions.IgnoreCase);
            if (fingerMatch.Success)
            {
                string side = fingerMatch.Groups[1].Value.ToUpperInvariant();
                string numStr = fingerMatch.Groups[2].Value;
                string target = null;
                if (numStr == "0") target = $"Bip01 {side} Finger0";
                else if (numStr.StartsWith("0")) target = $"Bip01 {side} Finger01";
                else if (numStr.Length == 1) target = $"Bip01 {side} Finger1";
                else target = $"Bip01 {side} Finger11";

                if (target != null && uoBoneByName.TryGetValue(target, out uoIdx))
                    return uoIdx;
            }

            // 5) Twist bones (e.g. Bip001-L-ForeTwist -> Bip01 L Forearm)
            var twistMatch = Regex.Match(bname, @"Bip\d*[-_ ]?([LR])[-_ ]?(Fore|UpArm)Twist", RegexOptions.IgnoreCase);
            if (twistMatch.Success)
            {
                string side = twistMatch.Groups[1].Value.ToUpperInvariant();
                string part = twistMatch.Groups[2].Value.ToLowerInvariant();
                string target = part.Contains("fore") ? $"Bip01 {side} Forearm" : $"Bip01 {side} UpperArm";
                if (uoBoneByName.TryGetValue(target, out uoIdx))
                    return uoIdx;
            }

            // 6) Weapon / Prop bones
            if (bname.IndexOf("WPN_R", StringComparison.OrdinalIgnoreCase) >= 0 ||
                bname.IndexOf("Weapon_R", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (uoBoneByName.TryGetValue("Bip01 R Hand", out uoIdx)) return uoIdx;
            }
            if (bname.IndexOf("WPN_L", StringComparison.OrdinalIgnoreCase) >= 0 ||
                bname.IndexOf("Weapon_L", StringComparison.OrdinalIgnoreCase) >= 0 ||
                bname.IndexOf("Bow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (uoBoneByName.TryGetValue("Bip01 L Hand", out uoIdx)) return uoIdx;
            }

            // 7) Mixamo naming heuristics (Thumb, Index, Middle, Ring, Pinky, Hand, Foot)
            bool isLeft = clean.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          clean.StartsWith("L_", StringComparison.OrdinalIgnoreCase) ||
                          clean.StartsWith("L-", StringComparison.OrdinalIgnoreCase) ||
                          clean.StartsWith("L ", StringComparison.OrdinalIgnoreCase) ||
                          clean.StartsWith("L.", StringComparison.OrdinalIgnoreCase) ||
                          clean.EndsWith("_l", StringComparison.OrdinalIgnoreCase) ||
                          clean.EndsWith(".l", StringComparison.OrdinalIgnoreCase) ||
                          clean.EndsWith("-l", StringComparison.OrdinalIgnoreCase) ||
                          clean.EndsWith("_left", StringComparison.OrdinalIgnoreCase);
            bool isRight = clean.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           clean.StartsWith("R_", StringComparison.OrdinalIgnoreCase) ||
                           clean.StartsWith("R-", StringComparison.OrdinalIgnoreCase) ||
                           clean.StartsWith("R ", StringComparison.OrdinalIgnoreCase) ||
                           clean.StartsWith("R.", StringComparison.OrdinalIgnoreCase) ||
                           clean.EndsWith("_r", StringComparison.OrdinalIgnoreCase) ||
                           clean.EndsWith(".r", StringComparison.OrdinalIgnoreCase) ||
                           clean.EndsWith("-r", StringComparison.OrdinalIgnoreCase) ||
                           clean.EndsWith("_right", StringComparison.OrdinalIgnoreCase);

            if (clean.IndexOf("Thumb", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool isBase = clean.IndexOf("Thumb1", StringComparison.OrdinalIgnoreCase) >= 0 || clean.IndexOf("01", StringComparison.OrdinalIgnoreCase) >= 0 || clean.EndsWith("_1", StringComparison.OrdinalIgnoreCase) || clean.EndsWith(".1", StringComparison.OrdinalIgnoreCase);
                string targetUo = isBase
                    ? (isLeft ? "Bip01 L Finger0" : (isRight ? "Bip01 R Finger0" : null))
                    : (isLeft ? "Bip01 L Finger01" : (isRight ? "Bip01 R Finger01" : null));
                if (targetUo != null && uoBoneByName.TryGetValue(targetUo, out uoIdx))
                    return uoIdx;
            }

            if (clean.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("Middle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("Ring", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("Pinky", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("Little", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (clean.IndexOf("Finger", StringComparison.OrdinalIgnoreCase) >= 0 && clean.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) < 0))
            {
                bool isBase = clean.IndexOf("1", StringComparison.OrdinalIgnoreCase) >= 0 &&
                             clean.IndexOf("11", StringComparison.OrdinalIgnoreCase) < 0 &&
                             (clean.EndsWith("1", StringComparison.OrdinalIgnoreCase) ||
                              clean.IndexOf("01", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              clean.IndexOf("Index1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              clean.IndexOf("Middle1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              clean.IndexOf("Ring1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              clean.IndexOf("Pinky1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              clean.IndexOf("Little1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              clean.IndexOf("Finger1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              clean.EndsWith("_1", StringComparison.OrdinalIgnoreCase) ||
                              clean.EndsWith(".1", StringComparison.OrdinalIgnoreCase));
                string targetUo = isBase
                    ? (isLeft ? "Bip01 L Finger1" : (isRight ? "Bip01 R Finger1" : null))
                    : (isLeft ? "Bip01 L Finger11" : (isRight ? "Bip01 R Finger11" : null));
                if (targetUo != null && uoBoneByName.TryGetValue(targetUo, out uoIdx))
                    return uoIdx;
            }

            if (clean.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("Wrist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string targetUo = isLeft ? "Bip01 L Hand" : (isRight ? "Bip01 R Hand" : null);
                if (targetUo != null && uoBoneByName.TryGetValue(targetUo, out uoIdx))
                    return uoIdx;
            }

            // 8) Try MixamoRetarget alias table
            if (MixamoRetarget.TryGetBoneAlias(bname, out string aliasName) && uoBoneByName.TryGetValue(aliasName, out uoIdx))
                return uoIdx;
            if (MixamoRetarget.TryGetBoneAlias(clean, out aliasName) && uoBoneByName.TryGetValue(aliasName, out uoIdx))
                return uoIdx;

            // 9) Explicit Humanoid matching
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

            if (isLeft || isRight)
            {
                string s = isLeft ? "L" : "R";
                if (lower.Contains("clavicle") || lower.Contains("shoulder"))
                    if (uoBoneByName.TryGetValue($"Bip01 {s} Clavicle", out uoIdx)) return uoIdx;
                if (lower.Contains("upperarm") || lower.Contains("upper_arm") || lower.Contains("uparm") || (lower.Contains("arm") && !lower.Contains("lower") && !lower.Contains("fore")))
                    if (uoBoneByName.TryGetValue($"Bip01 {s} UpperArm", out uoIdx)) return uoIdx;
                if (lower.Contains("lowerarm") || lower.Contains("lower_arm") || lower.Contains("forearm"))
                    if (uoBoneByName.TryGetValue($"Bip01 {s} Forearm", out uoIdx)) return uoIdx;
                if (lower.Contains("hand") || lower.Contains("wrist"))
                    if (uoBoneByName.TryGetValue($"Bip01 {s} Hand", out uoIdx)) return uoIdx;
                if (lower.Contains("thigh") || lower.Contains("upperleg") || lower.Contains("upper_leg") || lower.Contains("upleg"))
                    if (uoBoneByName.TryGetValue($"Bip01 {s} Thigh", out uoIdx)) return uoIdx;
                if (lower.Contains("calf") || lower.Contains("lowerleg") || lower.Contains("lower_leg") || lower.Contains("shin") || (lower.Contains("leg") && !lower.Contains("up") && !lower.Contains("upper")))
                    if (uoBoneByName.TryGetValue($"Bip01 {s} Calf", out uoIdx)) return uoIdx;
                if (lower.Contains("foot") || lower.Contains("ankle"))
                    if (uoBoneByName.TryGetValue($"Bip01 {s} Foot", out uoIdx)) return uoIdx;
                if (lower.Contains("ball") || lower.Contains("toe"))
                    if (uoBoneByName.TryGetValue($"Bip01 {s} Toe0", out uoIdx)) return uoIdx;

                if (lower.Contains("thumb"))
                {
                    string target = (lower.Contains("01") || lower.Contains("thumb1") || lower.EndsWith("_1") || lower.EndsWith(".1")) ? $"Bip01 {s} Finger0" : $"Bip01 {s} Finger01";
                    if (uoBoneByName.TryGetValue(target, out uoIdx)) return uoIdx;
                }
                if (lower.Contains("index") || lower.Contains("middle") || lower.Contains("ring") || lower.Contains("pinky") || lower.Contains("little") || lower.Contains("finger"))
                {
                    bool isBase = lower.Contains("01") || lower.Contains("index1") || lower.Contains("middle1") || lower.Contains("ring1") || lower.Contains("pinky1") || lower.Contains("little1") || lower.Contains("finger1") || lower.EndsWith("_1") || lower.EndsWith(".1");
                    string target = isBase ? $"Bip01 {s} Finger1" : $"Bip01 {s} Finger11";
                    if (uoBoneByName.TryGetValue(target, out uoIdx)) return uoIdx;
                }
            }

            // 10) Fallback: Traverse ancestors up the node hierarchy until an ancestor maps to a UO bone
            if (nameToNode != null && nodes != null && nodeParent != null && nameToNode.TryGetValue(bname, out int ni))
            {
                int cur = nodeParent[ni];
                while (cur >= 0)
                {
                    string pName = nodes[cur].Name;
                    int pRes = ResolveBoneToUo(pName, uoBoneByName, null, null, null);
                    if (pRes >= 0) return pRes;
                    cur = nodeParent[cur];
                }
            }

            return -1;
        }

        private static Matrix4 ToMatrix4(Assimp.Matrix4x4 m)
        {
            return new Matrix4(
                m.A1, m.B1, m.C1, m.D1,
                m.A2, m.B2, m.C2, m.D2,
                m.A3, m.B3, m.C3, m.D3,
                m.A4, m.B4, m.C4, m.D4
            );
        }

        public static Quaternion RotationBetweenVectors(Vector3 vFrom, Vector3 vTo)
        {
            float dot = Vector3.Dot(vFrom, vTo);
            if (dot >= 0.999999f) return Quaternion.Identity;
            if (dot <= -0.999999f)
            {
                Vector3 ortho = Vector3.Cross(Vector3.UnitZ, vFrom);
                if (ortho.LengthSquared < 1e-4f)
                    ortho = Vector3.Cross(Vector3.UnitY, vFrom);
                ortho.Normalize();
                return Quaternion.FromAxisAngle(ortho, MathF.PI);
            }
            Vector3 cross = Vector3.Cross(vFrom, vTo);
            Quaternion q = new Quaternion(cross.X, cross.Y, cross.Z, 1.0f + dot);
            q.Normalize();
            return q;
        }

        public static string? GetAimChildBoneName(string boneName)
        {
            return boneName switch
            {
                "Bip01 L Clavicle" => "Bip01 L UpperArm",
                "Bip01 L UpperArm" => "Bip01 L Forearm",
                "Bip01 L Forearm" => "Bip01 L Hand",
                "Bip01 L Hand" => "Bip01 L Finger1",
                "Bip01 L Finger0" => "Bip01 L Finger01",
                "Bip01 L Finger1" => "Bip01 L Finger11",

                "Bip01 R Clavicle" => "Bip01 R UpperArm",
                "Bip01 R UpperArm" => "Bip01 R Forearm",
                "Bip01 R Forearm" => "Bip01 R Hand",
                "Bip01 R Hand" => "Bip01 R Finger1",
                "Bip01 R Finger0" => "Bip01 R Finger01",
                "Bip01 R Finger1" => "Bip01 R Finger11",

                "Bip01 L Thigh" => "Bip01 L Calf",
                "Bip01 L Calf" => "Bip01 L Foot",
                "Bip01 L Foot" => "Bip01 L Toe0",

                "Bip01 R Thigh" => "Bip01 R Calf",
                "Bip01 R Calf" => "Bip01 R Foot",
                "Bip01 R Foot" => "Bip01 R Toe0",

                "Bip01 Spine" => "Bip01 Spine1",
                "Bip01 Spine1" => "Bip01 Spine2",
                "Bip01 Spine2" => "Bip01 Neck",
                "Bip01 Neck" => "Bip01 Head",
                _ => null
            };
        }

        public static void ComputeVolumetricSkinning(GrannyModelPart part, List<GrannyBone> bones)
        {
            if (part == null || part.Vertices == null || part.Vertices.Length == 0 || bones == null || bones.Count == 0)
                return;

            int nb = bones.Count;
            var boneWorldPos = new Vector3[nb];
            for (int i = 0; i < nb; i++)
            {
                var inv = bones[i].InverseBindMatrix;
                var w = Matrix4.Invert(inv);
                boneWorldPos[i] = w.Row3.Xyz;
            }

            var boneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nb; i++)
                boneByName[bones[i].Name] = i;

            bool hasBipedChildren = bones.Any(b => b.Name.StartsWith("Bip01 ", StringComparison.OrdinalIgnoreCase));

            bool IsIgnoredBone(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return true;
                string n = name.ToLowerInvariant();
                if (n.StartsWith("__root")) return true;
                if (n.StartsWith("master_")) return true;
                if (n.StartsWith("cp_")) return true;
                if (n == "bip01" && hasBipedChildren) return true;
                if (n.Contains("_lod")) return true;
                if (n.StartsWith("object")) return true;
                if (n.EndsWith("nub")) return true;
                if (!string.IsNullOrEmpty(part.Name) && string.Equals(n, part.Name.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            // Find bone hierarchy children
            var children = new Dictionary<int, List<int>>();
            for (int i = 0; i < nb; i++) children[i] = new List<int>();
            for (int i = 0; i < nb; i++)
            {
                int pid = bones[i].ParentId;
                if (pid >= 0 && pid < nb && pid != i)
                    children[pid].Add(i);
            }

            // Universal segment generation covering all deformation bones
            var segments = new List<(int BoneId, Vector3 P0, Vector3 P1)>();
            for (int i = 0; i < nb; i++)
            {
                if (IsIgnoredBone(bones[i].Name)) continue;

                var myChildren = children[i].Where(c => !IsIgnoredBone(bones[c].Name) || bones[c].Name.EndsWith("Nub", StringComparison.OrdinalIgnoreCase)).ToList();

                if (myChildren.Count > 0)
                {
                    foreach (var cid in myChildren)
                    {
                        float len = (boneWorldPos[cid] - boneWorldPos[i]).Length;
                        if (len < 1.8f)
                            segments.Add((i, boneWorldPos[i], boneWorldPos[cid]));
                        else
                            segments.Add((i, boneWorldPos[i], boneWorldPos[i]));
                    }
                }
                else
                {
                    // Leaf bone (e.g. Tail2, Ponytail14, Head, Toe0)
                    int pid = bones[i].ParentId;
                    Vector3 p0 = boneWorldPos[i];
                    Vector3 p1 = p0;
                    if (pid >= 0 && pid < nb && pid != i)
                    {
                        Vector3 parentPos = boneWorldPos[pid];
                        Vector3 dir = p0 - parentPos;
                        if (dir.LengthSquared > 1e-4f)
                        {
                            float leafLen = Math.Min(dir.Length, 0.15f);
                            p1 = p0 + dir.Normalized() * leafLen;
                        }
                    }
                    segments.Add((i, p0, p1));
                }
            }

            // Humanoid vs Creature detection
            bool hasTail = bones.Any(b => b.Name.Contains("Tail", StringComparison.OrdinalIgnoreCase));
            bool hasHorseLink = bones.Any(b => b.Name.Contains("HorseLink", StringComparison.OrdinalIgnoreCase));
            bool hasPonytail = bones.Any(b => b.Name.Contains("Ponytail", StringComparison.OrdinalIgnoreCase));
            bool hasWing = bones.Any(b => b.Name.Contains("Wing", StringComparison.OrdinalIgnoreCase));
            bool isStandardHumanoid = !hasTail && !hasHorseLink && !hasPonytail && !hasWing;

            // Anatomical reference points for Torso vs Limb boundaries (only used for humanoid ribcage protection)
            Vector3 pelvisPos = boneByName.TryGetValue("Bip01 Pelvis", out int bPelvis) ? boneWorldPos[bPelvis] : new Vector3(0, 0.037f, 0.937f);
            Vector3 neckPos   = boneByName.TryGetValue("Bip01 Neck", out int bNeck) ? boneWorldPos[bNeck] : new Vector3(0, 0.032f, 1.369f);
            Vector3 lArmPos   = boneByName.TryGetValue("Bip01 L UpperArm", out int bLArm) ? boneWorldPos[bLArm] : new Vector3(0.16f, 0.038f, 1.336f);
            Vector3 rArmPos   = boneByName.TryGetValue("Bip01 R UpperArm", out int bRArm) ? boneWorldPos[bRArm] : new Vector3(-0.16f, 0.038f, 1.336f);

            float shoulderWidth = MathF.Max(0.12f, (MathF.Abs(lArmPos.X) + MathF.Abs(rArmPos.X)) * 0.5f);
            float shoulderZ = (lArmPos.Z + rArmPos.Z) * 0.5f;
            float pelvisZ = pelvisPos.Z;
            float ribcageRadiusX = shoulderWidth * 0.75f;

            if (part.Skin == null || part.Skin.Length != part.Vertices.Length)
                part.Skin = new VertexInfluence[part.Vertices.Length][];

            for (int v = 0; v < part.Vertices.Length; v++)
            {
                var p = part.Vertices[v];
                float absX = MathF.Abs(p.X);
                var candidates = new List<(int BoneId, float Weight)>();

                foreach (var seg in segments)
                {
                    string bName = bones[seg.BoneId].Name;
                    float segMidX = (seg.P0.X + seg.P1.X) * 0.5f;

                    // Hemisphere separation based on bone name and actual geometric coordinate
                    bool isLeft = bName.Contains(" L ") || bName.Contains("Ponytail2") || bName.Contains("Left") || (segMidX > 0.04f);
                    bool isRight = bName.Contains(" R ") || bName.Contains("Ponytail1") || bName.Contains("Right") || (segMidX < -0.04f);

                    if (isLeft && !isRight)
                    {
                        if (segMidX > 0.02f && p.X < -0.02f) continue;
                    }
                    else if (isRight && !isLeft)
                    {
                        if (segMidX < -0.02f && p.X > 0.02f) continue;
                    }

                    bool isUpperArm = bName.Contains("UpperArm");
                    bool isClavicle = bName.Contains("Clavicle");
                    bool isTorsoSpine = bName.Contains("Spine") || bName.Contains("Neck") || bName.Contains("Pelvis");

                    // 1. Torso Protection: ONLY for standard humanoid biped models
                    // (in quadrupeds/centaurs, UpperArm is the horse front leg and must NEVER be shielded!)
                    if (isStandardHumanoid)
                    {
                        if ((isUpperArm || isClavicle) && p.Z < shoulderZ - 0.02f && absX < ribcageRadiusX)
                        {
                            continue;
                        }
                        if (isClavicle && p.Z < shoulderZ - 0.05f)
                        {
                            continue;
                        }
                    }

                    // 2. Ribcage Volumetric Distance:
                    float dist;
                    if (isStandardHumanoid && isTorsoSpine && p.Z >= pelvisZ - 0.05f && p.Z <= neckPos.Z + 0.05f)
                    {
                        float lateralOffset = MathF.Max(0f, absX - ribcageRadiusX);
                        var pTorso = new Vector3(MathF.Sign(p.X) * MathF.Min(absX, ribcageRadiusX), p.Y, p.Z);
                        dist = DistanceToSegment(pTorso, seg.P0, seg.P1) + lateralOffset;
                    }
                    else
                    {
                        dist = DistanceToSegment(p, seg.P0, seg.P1);

                        if (isStandardHumanoid && isUpperArm && p.Z < shoulderZ && absX < ribcageRadiusX)
                        {
                            float pinchPen = (ribcageRadiusX - absX) * 3.0f;
                            dist += pinchPen;
                        }
                    }

                    float w = 1.0f / (MathF.Pow(dist, 4f) + 1e-4f);
                    candidates.Add((seg.BoneId, w));
                }

                candidates.Sort((a, b) => b.Weight.CompareTo(a.Weight));

                int count = Math.Min(4, candidates.Count);
                if (count == 0)
                {
                    int fallbackId = 0;
                    if (boneByName.TryGetValue("Bip01 Pelvis", out int pelv)) fallbackId = pelv;
                    else if (boneByName.TryGetValue("Bip01 Spine", out int sp)) fallbackId = sp;
                    else if (segments.Count > 0) fallbackId = segments[0].BoneId;

                    part.Skin[v] = new[] { new VertexInfluence { BoneId = fallbackId, Weight = 1.0f } };
                    continue;
                }

                float sum = 0f;
                for (int i = 0; i < count; i++) sum += candidates[i].Weight;

                var infs = new VertexInfluence[count];
                for (int i = 0; i < count; i++)
                {
                    infs[i] = new VertexInfluence
                    {
                        BoneId = candidates[i].BoneId,
                        Weight = sum > 1e-6f ? (candidates[i].Weight / sum) : (i == 0 ? 1f : 0f)
                    };
                }
                part.Skin[v] = infs;
            }
        }

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float l2 = ab.LengthSquared;
            if (l2 < 1e-6f) return (p - a).Length;
            float t = MathHelper.Clamp(Vector3.Dot(p - a, ab) / l2, 0f, 1f);
            Vector3 proj = a + t * ab;
            return (p - proj).Length;
        }
    }
}

