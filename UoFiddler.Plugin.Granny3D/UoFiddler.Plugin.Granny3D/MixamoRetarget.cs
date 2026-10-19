using System;
using System.Collections.Generic;
using Assimp;
using OpenTK.Mathematics;
using Quaternion = OpenTK.Mathematics.Quaternion;

namespace UoFiddler.Plugin.Granny3D
{
    // Offline retarget of a standard Mixamo FBX clip onto the UO Biped skeleton.
    //
    // Method: for every mapped bone we take the WORLD rotation delta between the
    // clip frame and the clip's own bind pose, rotate the delta from the Mixamo
    // basis into the UO basis, then post-multiply the UO bone's bind world rotation.
    // Local rotations are recovered by dividing out the parent world rotation, so
    // the very same row-vector convention as the plugin's FK is used throughout.
    public static class MixamoRetarget
    {
        // Mixamo node -> UO Biped bone name. Covers the core human joints plus thumb and index fingers.
        public static readonly (string Mix, string Uo)[] BoneMap =
        {
            ("mixamorig:Hips",         "Bip01 Pelvis"),
            ("mixamorig:Spine",        "Bip01 Spine"),
            ("mixamorig:Spine1",       "Bip01 Spine1"),
            ("mixamorig:Spine2",       "Bip01 Spine2"),
            ("mixamorig:Neck",         "Bip01 Neck"),
            ("mixamorig:Head",         "Bip01 Head"),
            ("mixamorig:LeftShoulder", "Bip01 L Clavicle"),
            ("mixamorig:LeftArm",      "Bip01 L UpperArm"),
            ("mixamorig:LeftForeArm",  "Bip01 L Forearm"),
            ("mixamorig:LeftHand",     "Bip01 L Hand"),
            // Left Hand Fingers (Mixamo 5 fingers -> UO Finger0/Finger1):
            ("mixamorig:LeftHandThumb1",  "Bip01 L Finger0"),
            ("mixamorig:LeftHandThumb2",  "Bip01 L Finger01"),
            ("mixamorig:LeftHandThumb3",  "Bip01 L Finger01"),
            ("mixamorig:LeftHandThumb4",  "Bip01 L Finger01"),
            ("mixamorig:LeftHandIndex1",   "Bip01 L Finger1"),
            ("mixamorig:LeftHandIndex2",   "Bip01 L Finger11"),
            ("mixamorig:LeftHandIndex3",   "Bip01 L Finger11"),
            ("mixamorig:LeftHandIndex4",   "Bip01 L Finger11"),
            ("mixamorig:LeftHandMiddle1",  "Bip01 L Finger1"),
            ("mixamorig:LeftHandMiddle2",  "Bip01 L Finger11"),
            ("mixamorig:LeftHandMiddle3",  "Bip01 L Finger11"),
            ("mixamorig:LeftHandMiddle4",  "Bip01 L Finger11"),
            ("mixamorig:LeftHandRing1",    "Bip01 L Finger1"),
            ("mixamorig:LeftHandRing2",    "Bip01 L Finger11"),
            ("mixamorig:LeftHandRing3",    "Bip01 L Finger11"),
            ("mixamorig:LeftHandRing4",    "Bip01 L Finger11"),
            ("mixamorig:LeftHandPinky1",   "Bip01 L Finger1"),
            ("mixamorig:LeftHandPinky2",   "Bip01 L Finger11"),
            ("mixamorig:LeftHandPinky3",   "Bip01 L Finger11"),
            ("mixamorig:LeftHandPinky4",   "Bip01 L Finger11"),

            ("mixamorig:RightShoulder","Bip01 R Clavicle"),
            ("mixamorig:RightArm",     "Bip01 R UpperArm"),
            ("mixamorig:RightForeArm", "Bip01 R Forearm"),
            ("mixamorig:RightHand",    "Bip01 R Hand"),

            // Right Hand Fingers (Mixamo 5 fingers -> UO Finger0/Finger1):
            ("mixamorig:RightHandThumb1", "Bip01 R Finger0"),
            ("mixamorig:RightHandThumb2", "Bip01 R Finger01"),
            ("mixamorig:RightHandThumb3", "Bip01 R Finger01"),
            ("mixamorig:RightHandThumb4", "Bip01 R Finger01"),
            ("mixamorig:RightHandIndex1",  "Bip01 R Finger1"),
            ("mixamorig:RightHandIndex2",  "Bip01 R Finger11"),
            ("mixamorig:RightHandIndex3",  "Bip01 R Finger11"),
            ("mixamorig:RightHandIndex4",  "Bip01 R Finger11"),
            ("mixamorig:RightHandMiddle1", "Bip01 R Finger1"),
            ("mixamorig:RightHandMiddle2", "Bip01 R Finger11"),
            ("mixamorig:RightHandMiddle3", "Bip01 R Finger11"),
            ("mixamorig:RightHandMiddle4", "Bip01 R Finger11"),
            ("mixamorig:RightHandRing1",   "Bip01 R Finger1"),
            ("mixamorig:RightHandRing2",   "Bip01 R Finger11"),
            ("mixamorig:RightHandRing3",   "Bip01 R Finger11"),
            ("mixamorig:RightHandRing4",   "Bip01 R Finger11"),
            ("mixamorig:RightHandPinky1",  "Bip01 R Finger1"),
            ("mixamorig:RightHandPinky2",  "Bip01 R Finger11"),
            ("mixamorig:RightHandPinky3",  "Bip01 R Finger11"),
            ("mixamorig:RightHandPinky4",  "Bip01 R Finger11"),
            ("mixamorig:LeftUpLeg",    "Bip01 L Thigh"),
            ("mixamorig:LeftLeg",      "Bip01 L Calf"),
            ("mixamorig:LeftFoot",     "Bip01 L Foot"),
            ("mixamorig:LeftToeBase",  "Bip01 L Toe0"),
            ("mixamorig:RightUpLeg",   "Bip01 R Thigh"),
            ("mixamorig:RightLeg",     "Bip01 R Calf"),
            ("mixamorig:RightFoot",    "Bip01 R Foot"),
            ("mixamorig:RightToeBase", "Bip01 R Toe0"),

            // Standard Unreal / Unity Humanoid rig aliases:
            ("pelvis",        "Bip01 Pelvis"),
            ("spine_01",      "Bip01 Spine"),
            ("spine_02",      "Bip01 Spine1"),
            ("spine_03",      "Bip01 Spine2"),
            ("neck_01",       "Bip01 Neck"),
            ("head",          "Bip01 Head"),
            ("clavicle_l",    "Bip01 L Clavicle"),
            ("upperarm_l",    "Bip01 L UpperArm"),
            ("lowerarm_l",    "Bip01 L Forearm"),
            ("hand_l",        "Bip01 L Hand"),
            ("thigh_l",       "Bip01 L Thigh"),
            ("calf_l",        "Bip01 L Calf"),
            ("foot_l",        "Bip01 L Foot"),
            ("ball_l",        "Bip01 L Toe0"),
            ("clavicle_r",    "Bip01 R Clavicle"),
            ("upperarm_r",    "Bip01 R UpperArm"),
            ("lowerarm_r",    "Bip01 R Forearm"),
            ("hand_r",        "Bip01 R Hand"),
            ("thigh_r",       "Bip01 R Thigh"),
            ("calf_r",        "Bip01 R Calf"),
            ("foot_r",        "Bip01 R Foot"),
            ("ball_r",        "Bip01 R Toe0"),

            // Standard finger bones (Thumb -> Finger0, Other fingers -> Finger1):
            ("thumb_01_l",    "Bip01 L Finger0"),
            ("thumb_02_l",    "Bip01 L Finger01"),
            ("thumb_03_l",    "Bip01 L Finger01"),
            ("index_01_l",    "Bip01 L Finger1"),
            ("index_02_l",    "Bip01 L Finger11"),
            ("index_03_l",    "Bip01 L Finger11"),
            ("middle_01_l",   "Bip01 L Finger1"),
            ("middle_02_l",   "Bip01 L Finger11"),
            ("middle_03_l",   "Bip01 L Finger11"),
            ("ring_01_l",     "Bip01 L Finger1"),
            ("ring_02_l",     "Bip01 L Finger11"),
            ("ring_03_l",     "Bip01 L Finger11"),
            ("pinky_01_l",    "Bip01 L Finger1"),
            ("pinky_02_l",    "Bip01 L Finger11"),
            ("pinky_03_l",    "Bip01 L Finger11"),

            ("thumb_01_r",    "Bip01 R Finger0"),
            ("thumb_02_r",    "Bip01 R Finger01"),
            ("thumb_03_r",    "Bip01 R Finger01"),
            ("index_01_r",    "Bip01 R Finger1"),
            ("index_02_r",    "Bip01 R Finger11"),
            ("index_03_r",    "Bip01 R Finger11"),
            ("middle_01_r",   "Bip01 R Finger1"),
            ("middle_02_r",   "Bip01 R Finger11"),
            ("middle_03_r",   "Bip01 R Finger11"),
            ("ring_01_r",     "Bip01 R Finger1"),
            ("ring_02_r",     "Bip01 R Finger11"),
            ("ring_03_r",     "Bip01 R Finger11"),
            ("pinky_01_r",    "Bip01 R Finger1"),
            ("pinky_02_r",    "Bip01 R Finger11"),
            ("pinky_03_r",    "Bip01 R Finger11"),
        };

        // Mixamo is Y-up, UO models are Z-up: +/-90deg about X bridges the two when
        // a world-frame rotation delta is expressed in each basis.
        static readonly Matrix4 C = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(90f));
        static readonly Matrix4 Cinv = Matrix4.Invert(C);

        public static int MappedBoneCount(IReadOnlyList<GrannyBone> refBones)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in refBones) names.Add(b.Name);
            int n = 0;
            foreach (var m in BoneMap) if (names.Contains(m.Uo)) n++;
            return n;
        }

        public static bool TryGetBoneAlias(string name, out string alias)
        {
            alias = null;
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf("_$AssimpFbx$_", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            string cleanName = name;
            int colon = cleanName.IndexOf(':');
            if (colon >= 0) cleanName = cleanName.Substring(colon + 1);

            foreach (var (mix, uo) in BoneMap)
            {
                if (string.Equals(name, mix, StringComparison.OrdinalIgnoreCase)) { alias = uo; return true; }
                if (string.Equals(name, uo, StringComparison.OrdinalIgnoreCase)) { alias = mix; return true; }

                string cleanMix = mix;
                int cMix = cleanMix.IndexOf(':');
                if (cMix >= 0) cleanMix = cleanMix.Substring(cMix + 1);

                if (string.Equals(cleanName, cleanMix, StringComparison.OrdinalIgnoreCase))
                {
                    alias = uo;
                    return true;
                }
            }

            // Pattern-based humanoid resolver
            string lower = cleanName.ToLowerInvariant();
            if (lower == "pelvis" || lower == "hips" || lower == "hip") { alias = "Bip01 Pelvis"; return true; }
            if (lower == "spine_01" || lower == "spine1" || lower == "spine") { alias = "Bip01 Spine"; return true; }
            if (lower == "spine_02" || lower == "spine2" || lower == "chest") { alias = "Bip01 Spine1"; return true; }
            if (lower == "spine_03" || lower == "spine3" || lower == "upperchest" || lower == "upper_chest") { alias = "Bip01 Spine2"; return true; }
            if (lower == "neck_01" || lower == "neck") { alias = "Bip01 Neck"; return true; }
            if (lower == "head") { alias = "Bip01 Head"; return true; }

            bool isLeft = cleanName.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          cleanName.StartsWith("L_", StringComparison.OrdinalIgnoreCase) ||
                          cleanName.StartsWith("L-", StringComparison.OrdinalIgnoreCase) ||
                          cleanName.StartsWith("L.", StringComparison.OrdinalIgnoreCase) ||
                          cleanName.StartsWith("L ", StringComparison.OrdinalIgnoreCase) ||
                          cleanName.EndsWith("_L", StringComparison.OrdinalIgnoreCase) ||
                          cleanName.EndsWith(".L", StringComparison.OrdinalIgnoreCase) ||
                          cleanName.EndsWith("-L", StringComparison.OrdinalIgnoreCase) ||
                          cleanName.EndsWith("_left", StringComparison.OrdinalIgnoreCase);

            bool isRight = cleanName.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           cleanName.StartsWith("R_", StringComparison.OrdinalIgnoreCase) ||
                           cleanName.StartsWith("R-", StringComparison.OrdinalIgnoreCase) ||
                           cleanName.StartsWith("R.", StringComparison.OrdinalIgnoreCase) ||
                           cleanName.StartsWith("R ", StringComparison.OrdinalIgnoreCase) ||
                           cleanName.EndsWith("_R", StringComparison.OrdinalIgnoreCase) ||
                           cleanName.EndsWith(".R", StringComparison.OrdinalIgnoreCase) ||
                           cleanName.EndsWith("-R", StringComparison.OrdinalIgnoreCase) ||
                           cleanName.EndsWith("_right", StringComparison.OrdinalIgnoreCase);

            string side = isLeft ? "L" : (isRight ? "R" : null);
            if (side != null)
            {
                if (lower.Contains("clavicle") || lower.Contains("shoulder")) { alias = $"Bip01 {side} Clavicle"; return true; }
                if (lower.Contains("upperarm") || lower.Contains("upper_arm") || lower.Contains("uparm") || (lower.Contains("arm") && !lower.Contains("lower") && !lower.Contains("fore"))) { alias = $"Bip01 {side} UpperArm"; return true; }
                if (lower.Contains("lowerarm") || lower.Contains("lower_arm") || lower.Contains("forearm")) { alias = $"Bip01 {side} Forearm"; return true; }
                if (lower.Contains("hand") || lower.Contains("wrist")) { alias = $"Bip01 {side} Hand"; return true; }
                if (lower.Contains("thigh") || lower.Contains("upperleg") || lower.Contains("upper_leg") || lower.Contains("upleg")) { alias = $"Bip01 {side} Thigh"; return true; }
                if (lower.Contains("calf") || lower.Contains("lowerleg") || lower.Contains("lower_leg") || lower.Contains("shin") || (lower.Contains("leg") && !lower.Contains("up") && !lower.Contains("upper"))) { alias = $"Bip01 {side} Calf"; return true; }
                if (lower.Contains("foot") || lower.Contains("ankle")) { alias = $"Bip01 {side} Foot"; return true; }
                if (lower.Contains("ball") || lower.Contains("toe")) { alias = $"Bip01 {side} Toe0"; return true; }

                if (lower.Contains("thumb"))
                {
                    alias = (lower.Contains("01") || lower.Contains("thumb1") || lower.EndsWith("_1") || lower.EndsWith(".1")) ? $"Bip01 {side} Finger0" : $"Bip01 {side} Finger01";
                    return true;
                }
                if (lower.Contains("index") || lower.Contains("middle") || lower.Contains("ring") || lower.Contains("pinky") || lower.Contains("little") || lower.Contains("finger"))
                {
                    bool isBase = lower.Contains("01") || lower.Contains("index1") || lower.Contains("middle1") || lower.Contains("ring1") || lower.Contains("pinky1") || lower.Contains("little1") || lower.Contains("finger1") || lower.EndsWith("_1") || lower.EndsWith(".1");
                    alias = isBase ? $"Bip01 {side} Finger1" : $"Bip01 {side} Finger11";
                    return true;
                }
            }

            return false;
        }

        public static bool TryImport(string fbxPath, IReadOnlyList<GrannyBone> refBones, string clipName, out GrannyAnimation anim, out string error, int animIndex = 0)
        {
            try
            {
                anim = Import(fbxPath, refBones, clipName, animIndex);
                error = anim == null ? "import échoué" : null;
                return anim != null;
            }
            catch (Exception ex)
            {
                anim = null;
                error = ex.Message;
                return false;
            }
        }

        public static GrannyAnimation Import(string fbxPath, IReadOnlyList<GrannyBone> refBones, string clipName, int animIndex = 0)
        {
            if (refBones == null || refBones.Count == 0) return null;

            int nb = refBones.Count;
            var localT = new Vector3[nb];
            var parent = new int[nb];
            var nameToUo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nb; i++)
            {
                localT[i] = refBones[i].LocalPosition;
                parent[i] = refBones[i].ParentId;
                if (!string.IsNullOrEmpty(refBones[i].Name)) nameToUo[refBones[i].Name] = i;
            }

            // UO bind world (same convention as the plugin FK).
            var bindRot = new Quaternion[nb][];
            for (int i = 0; i < nb; i++) bindRot[i] = new[] { refBones[i].LocalRotation };
            var wbindU = Fk(bindRot, localT, parent, nb, 0);

            using var ctx = new AssimpContext();
            var scene = ctx.ImportFile(fbxPath, PostProcessSteps.None);
            if (scene == null || scene.AnimationCount == 0 || scene.RootNode == null) return null;

            var nodes = new List<Node>();
            var nodeParent = new List<int>();
            void Collect(Node n, int par)
            {
                int idx = nodes.Count; nodes.Add(n); nodeParent.Add(par);
                foreach (var c in n.Children) Collect(c, idx);
            }
            Collect(scene.RootNode, -1);

            var nameToNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nodes.Count; i++)
                if (!nameToNode.ContainsKey(nodes[i].Name)) nameToNode[nodes[i].Name] = i;

            if (animIndex < 0 || animIndex >= scene.AnimationCount) animIndex = 0;
            var anim = scene.Animations[animIndex];
            var chanByNode = new Dictionary<int, NodeAnimationChannel>();
            foreach (var ch in anim.NodeAnimationChannels)
                if (nameToNode.TryGetValue(ch.NodeName, out int ni)) chanByNode[ni] = ch;

            float fps = anim.TicksPerSecond > 0 ? (float)anim.TicksPerSecond : 30f;

            int nn = nodes.Count;
            var s0 = new Vector3[nn]; var q0 = new Quaternion[nn]; var p0 = new Vector3[nn];
            for (int i = 0; i < nn; i++)
            {
                nodes[i].Transform.Decompose(out var sc, out var qq, out var tt);
                s0[i] = new Vector3(sc.X, sc.Y, sc.Z);
                q0[i] = new Quaternion(qq.X, qq.Y, qq.Z, qq.W);
                p0[i] = new Vector3(tt.X, tt.Y, tt.Z);
            }

            var wBindMix = WorldStatic(q0, p0, s0, nodeParent, nn);

            // Determine if the source animation skeleton is Z-up or Y-up:
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < nn; i++)
            {
                var p = wBindMix[i].Row3;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }
            bool isZUp = (maxZ - minZ) > (maxY - minY) * 1.5f;
            Matrix4 cAnim = isZUp ? Matrix4.Identity : C;
            Matrix4 cInvAnim = isZUp ? Matrix4.Identity : Cinv;

            // Which UO bone each mix node maps to.
            var uo2mix = new int[nb];
            for (int i = 0; i < nb; i++) uo2mix[i] = -1;

            // Pass 1: Try exact or prefix-normalized BoneMap matches (e.g. mixamorig1:LeftArm or LeftArm)
            foreach (var m in BoneMap)
            {
                if (!nameToUo.TryGetValue(m.Uo, out int uo)) continue;
                if (uo2mix[uo] >= 0) continue;

                if (nameToNode.TryGetValue(m.Mix, out int mi))
                {
                    if (nodes[mi].Name.IndexOf("_$AssimpFbx$_", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        uo2mix[uo] = mi;
                        continue;
                    }
                }

                string cleanMix = m.Mix;
                int cMix = cleanMix.IndexOf(':');
                if (cMix >= 0) cleanMix = cleanMix.Substring(cMix + 1);

                for (int ni = 0; ni < nn; ni++)
                {
                    string nName = nodes[ni].Name;
                    if (nName.IndexOf("_$AssimpFbx$_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    string cNode = nName;
                    int cCol = cNode.IndexOf(':');
                    if (cCol >= 0) cNode = cNode.Substring(cCol + 1);
                    if (string.Equals(cNode, cleanMix, StringComparison.OrdinalIgnoreCase))
                    {
                        uo2mix[uo] = ni;
                        break;
                    }
                }
            }

            // Pass 2: Humanoid and finger alias resolvers
            for (int ni = 0; ni < nn; ni++)
            {
                string nName = nodes[ni].Name;
                if (nName.IndexOf("_$AssimpFbx$_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (TryGetBoneAlias(nName, out string uoName) && nameToUo.TryGetValue(uoName, out int uo))
                    if (uo2mix[uo] < 0) uo2mix[uo] = ni;
            }

            int frames = (int)Math.Round(anim.DurationInTicks) + 1;
            if (frames < 2) frames = 2;

            var rotTracks = new Quaternion[nb][];
            for (int i = 0; i < nb; i++) rotTracks[i] = new Quaternion[frames];

            for (int f = 0; f < frames; f++)
            {
                double tick = f;
                var wMix = WorldAt(chanByNode, q0, p0, s0, nodeParent, nn, tick);

                var wU = new Matrix4[nb];
                for (int i = 0; i < nb; i++)
                {
                    Matrix4 dR = Matrix4.Identity;
                    if (uo2mix[i] >= 0)
                    {
                        int mi = uo2mix[i];
                        var dmix = Matrix4.Invert(Rot(wBindMix[mi])) * Rot(wMix[mi]);
                        dR = cInvAnim * dmix * cAnim;
                    }
                    wU[i] = Rot(wbindU[i]) * dR;
                }

                for (int i = 0; i < nb; i++)
                {
                    int p = parent[i];
                    var lr = (p >= 0 && p < nb && p != i) ? wU[i] * Matrix4.Invert(wU[p]) : wU[i];
                    rotTracks[i][f] = Rot(lr).ExtractRotation();
                }
            }

            string name = string.IsNullOrEmpty(clipName) ? "Mixamo" : clipName;
            return GrannyAnimFile.BuildAnimation(name, refBones, rotTracks, fps);
        }

        static Matrix4[] WorldStatic(Quaternion[] q0, Vector3[] p0, Vector3[] s0, List<int> nodeParent, int nn)
        {
            var local = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
                local[i] = Matrix4.CreateFromQuaternion(q0[i]) * Matrix4.CreateTranslation(p0[i]) * Matrix4.CreateScale(s0[i]);
            return Compose(local, nodeParent, nn);
        }

        static Matrix4[] WorldAt(Dictionary<int, NodeAnimationChannel> chanByNode,
            Quaternion[] q0, Vector3[] p0, Vector3[] s0, List<int> nodeParent, int nn, double time)
        {
            var local = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                var q = q0[i]; var p = p0[i];
                if (chanByNode.TryGetValue(i, out var ch))
                {
                    if (ch.RotationKeyCount > 0) { var k = SampleQ(ch.RotationKeys, time); q = new Quaternion(k.X, k.Y, k.Z, k.W); }
                    if (ch.PositionKeyCount > 0) { var k = SampleV(ch.PositionKeys, time); p = new Vector3(k.X, k.Y, k.Z); }
                }
                local[i] = Matrix4.CreateFromQuaternion(q) * Matrix4.CreateTranslation(p) * Matrix4.CreateScale(s0[i]);
            }
            return Compose(local, nodeParent, nn);
        }

        static Matrix4[] Compose(Matrix4[] local, List<int> nodeParent, int nn)
        {
            var world = new Matrix4[nn];
            var children = new List<int>[nn];
            for (int i = 0; i < nn; i++) children[i] = new List<int>();
            for (int i = 0; i < nn; i++) if (nodeParent[i] >= 0) children[nodeParent[i]].Add(i);

            var stack = new Stack<int>();
            for (int i = 0; i < nn; i++) if (nodeParent[i] < 0) stack.Push(i);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int par = nodeParent[i];
                world[i] = par >= 0 ? local[i] * world[par] : local[i];
                foreach (var c in children[i]) stack.Push(c);
            }
            return world;
        }

        static Vector3 SampleV(List<VectorKey> keys, double t)
        {
            if (keys == null || keys.Count == 0) return Vector3.Zero;
            if (t <= keys[0].Time) return new Vector3(keys[0].Value.X, keys[0].Value.Y, keys[0].Value.Z);
            if (t >= keys[keys.Count - 1].Time) { var l = keys[keys.Count - 1].Value; return new Vector3(l.X, l.Y, l.Z); }
            int i = 0;
            while (i < keys.Count - 1 && keys[i + 1].Time < t) i++;
            double span = keys[i + 1].Time - keys[i].Time;
            float f = span > 1e-9 ? (float)((t - keys[i].Time) / span) : 0f;
            var a = keys[i].Value; var b = keys[i + 1].Value;
            return new Vector3(
                a.X + (b.X - a.X) * f,
                a.Y + (b.Y - a.Y) * f,
                a.Z + (b.Z - a.Z) * f);
        }

        static Quaternion SampleQ(List<QuaternionKey> keys, double t)
        {
            if (keys == null || keys.Count == 0) return Quaternion.Identity;
            if (t <= keys[0].Time) { var k = keys[0].Value; return new Quaternion(k.X, k.Y, k.Z, k.W); }
            if (t >= keys[keys.Count - 1].Time) { var k = keys[keys.Count - 1].Value; return new Quaternion(k.X, k.Y, k.Z, k.W); }
            int i = 0;
            while (i < keys.Count - 1 && keys[i + 1].Time < t) i++;
            double span = keys[i + 1].Time - keys[i].Time;
            float f = span > 1e-9 ? (float)((t - keys[i].Time) / span) : 0f;
            var a = keys[i].Value; var b = keys[i + 1].Value;
            return Quaternion.Slerp(
                new Quaternion(a.X, a.Y, a.Z, a.W),
                new Quaternion(b.X, b.Y, b.Z, b.W), f);
        }

        static Matrix4 Rot(Matrix4 m)
        {
            m.M41 = m.M42 = m.M43 = 0;
            m.M14 = m.M24 = m.M34 = 0;
            m.M44 = 1;
            return m;
        }

        static Matrix4[] Fk(Quaternion[][] lr, Vector3[] t, int[] par, int nb, int f)
        {
            var local = new Matrix4[nb];
            var world = new Matrix4[nb];
            var children = new List<int>[nb];
            for (int i = 0; i < nb; i++) children[i] = new List<int>();
            for (int i = 0; i < nb; i++) if (par[i] >= 0 && par[i] < nb && par[i] != i) children[par[i]].Add(i);

            for (int i = 0; i < nb; i++) local[i] = Matrix4.CreateFromQuaternion(lr[i][f]) * Matrix4.CreateTranslation(t[i]);

            var stack = new Stack<int>();
            for (int i = 0; i < nb; i++) if (par[i] < 0 || par[i] >= nb || par[i] == i) stack.Push(i);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int p = par[i];
                world[i] = (p >= 0 && p < nb && p != i) ? local[i] * world[p] : local[i];
                foreach (var c in children[i]) stack.Push(c);
            }
            return world;
        }
    }
}
