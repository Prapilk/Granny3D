using System;
using System.IO;
using System.Collections.Generic;
using OpenTK.Mathematics;
using UoFiddler.Plugin.Granny3D;

class TestActualImport {
    static void Main() {
        string[] files = { @"c:\Users\User\Downloads\T-Pose.fbx", @"c:\Users\User\Downloads\T-Pose(1).fbx", @"c:\Users\User\Downloads\T-Pose(2).fbx" };
        string uoRef = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn";
        string animPath = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn";
        string tempOut = Path.Combine(Path.GetTempPath(), "GrannyTestImport");
        if (!Directory.Exists(tempOut)) Directory.CreateDirectory(tempOut);

        var animParser = new GrannyFormatParser();
        animParser.Load(animPath);
        var anim = animParser.Animation;

        foreach (var fbx in files) {
            if (!File.Exists(fbx)) continue;
            Console.WriteLine($"\n========================================");
            Console.WriteLine($"Testing ACTUAL TryImport: {Path.GetFileName(fbx)}");
            Console.WriteLine($"========================================");

            bool ok = MixamoCharacterImport.TryImport(fbx, tempOut, uoRef, out var model, out string err);
            if (!ok || model == null) {
                Console.WriteLine($"TryImport FAILED: {err}");
                continue;
            }

            Console.WriteLine($"Import OK: Parts={model.Parts.Count}, Bones={model.Bones.Count}");
            int nb = model.Bones.Count;

            // 1. Verify Rest Pose (T-pose) skinning matrices are all Identity!
            var restLocal = new Matrix4[nb];
            for (int i = 0; i < nb; i++) {
                var b = model.Bones[i];
                restLocal[i] = Matrix4.CreateFromQuaternion(b.LocalRotation) * Matrix4.CreateTranslation(b.LocalPosition);
            }
            var restWorld = new Matrix4[nb];
            for (int i = 0; i < nb; i++) {
                int p = model.Bones[i].ParentId;
                restWorld[i] = (p >= 0 && p < nb && p != i) ? restLocal[i] * restWorld[p] : restLocal[i];
            }
            float maxRestError = 0f;
            for (int i = 0; i < nb; i++) {
                var skinMat = model.Bones[i].InverseBindMatrix * restWorld[i];
                var diff = skinMat - Matrix4.Identity;
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        maxRestError = Math.Max(maxRestError, Math.Abs(diff[r, c]));
            }
            Console.WriteLine($"T-Pose Rest Skin Matrix Max Diff from Identity: {maxRestError:E6}");

            // 2. Animate with H_Male_Walk_01.grn
            int lForearmIdx = model.Bones.FindIndex(b => b.Name == "Bip01 L Forearm");
            int lHandIdx    = model.Bones.FindIndex(b => b.Name == "Bip01 L Hand");
            int rForearmIdx = model.Bones.FindIndex(b => b.Name == "Bip01 R Forearm");
            int rHandIdx    = model.Bones.FindIndex(b => b.Name == "Bip01 R Hand");
            int lThumb0Idx  = model.Bones.FindIndex(b => b.Name == "Bip01 L Finger0");
            int lThumb1Idx  = model.Bones.FindIndex(b => b.Name == "Bip01 L Finger01");
            int lIndex0Idx  = model.Bones.FindIndex(b => b.Name == "Bip01 L Finger1");
            int lIndex1Idx  = model.Bones.FindIndex(b => b.Name == "Bip01 L Finger11");

            var animLocal = new Matrix4[nb];
            for (int i = 0; i < nb; i++) {
                var b = model.Bones[i];
                var tr = anim.Tracks.Find(t => t.Name == b.Name);
                if (tr != null) {
                    int p = b.ParentId;
                    bool isRoot = p < 0 || p >= nb || p == i;
                    bool hasPosMotion = tr.Pos != null && tr.Pos.Length > 1 &&
                                        (tr.Pos[0] - tr.Pos[tr.Pos.Length - 1]).LengthSquared > 1e-4f;
                    Vector3 pos = (isRoot || hasPosMotion) ? tr.EvaluatePos(0) : b.LocalPosition;
                    animLocal[i] = Matrix4.CreateFromQuaternion(tr.EvaluateRot(0)) * Matrix4.CreateTranslation(pos);
                } else {
                    animLocal[i] = Matrix4.CreateFromQuaternion(b.LocalRotation) * Matrix4.CreateTranslation(b.LocalPosition);
                }
            }

            var animWorld = new Matrix4[nb];
            for (int i = 0; i < nb; i++) {
                int p = model.Bones[i].ParentId;
                animWorld[i] = (p >= 0 && p < nb && p != i) ? animLocal[i] * animWorld[p] : animLocal[i];
            }

            var skinMats = new Matrix4[nb];
            for (int i = 0; i < nb; i++)
                skinMats[i] = model.Bones[i].InverseBindMatrix * animWorld[i];

            Console.WriteLine($"L Hand Anim Pos: {animWorld[lHandIdx].Row3.Xyz}");
            Console.WriteLine($"R Hand Anim Pos: {animWorld[rHandIdx].Row3.Xyz}");
            Console.WriteLine($"L Arm Length: {(animWorld[lHandIdx].Row3.Xyz - animWorld[lForearmIdx].Row3.Xyz).Length:F3} m");
            Console.WriteLine($"R Arm Length: {(animWorld[rHandIdx].Row3.Xyz - animWorld[rForearmIdx].Row3.Xyz).Length:F3} m");

            // Evaluate gap at joints in skinned space
            var fittedWorld = new Matrix4[nb];
            for (int i = 0; i < nb; i++) fittedWorld[i] = Matrix4.Invert(model.Bones[i].InverseBindMatrix);

            Vector3 wristL = fittedWorld[lHandIdx].Row3.Xyz;
            Vector3 vLForearm = Vector3.TransformPosition(wristL, skinMats[lForearmIdx]);
            Vector3 vLHand    = Vector3.TransformPosition(wristL, skinMats[lHandIdx]);
            Console.WriteLine($"GAP at Left Wrist:       {(vLForearm - vLHand).Length:F6}");

            Vector3 wristR = fittedWorld[rHandIdx].Row3.Xyz;
            Vector3 vRForearm = Vector3.TransformPosition(wristR, skinMats[rForearmIdx]);
            Vector3 vRHand    = Vector3.TransformPosition(wristR, skinMats[rHandIdx]);
            Console.WriteLine($"GAP at Right Wrist:      {(vRForearm - vRHand).Length:F6}");

            Vector3 thumb0L = fittedWorld[lThumb0Idx].Row3.Xyz;
            Vector3 vLH = Vector3.TransformPosition(thumb0L, skinMats[lHandIdx]);
            Vector3 vLT = Vector3.TransformPosition(thumb0L, skinMats[lThumb0Idx]);
            Console.WriteLine($"GAP at Left Thumb Base:  {(vLH - vLT).Length:F6}");

            Vector3 thumb1L = fittedWorld[lThumb1Idx].Row3.Xyz;
            Vector3 vLT0 = Vector3.TransformPosition(thumb1L, skinMats[lThumb0Idx]);
            Vector3 vLT1 = Vector3.TransformPosition(thumb1L, skinMats[lThumb1Idx]);
            Console.WriteLine($"GAP at Left Thumb Mid:   {(vLT0 - vLT1).Length:F6}");

            Vector3 index0L = fittedWorld[lIndex0Idx].Row3.Xyz;
            Vector3 vLHI = Vector3.TransformPosition(index0L, skinMats[lHandIdx]);
            Vector3 vLI0 = Vector3.TransformPosition(index0L, skinMats[lIndex0Idx]);
            Console.WriteLine($"GAP at Left Index Base:  {(vLHI - vLI0).Length:F6}");
        }
    }
}
