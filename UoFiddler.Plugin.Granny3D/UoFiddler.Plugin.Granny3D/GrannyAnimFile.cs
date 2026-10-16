using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenTK.Mathematics;

namespace UoFiddler.Plugin.Granny3D
{
    // Simple standalone animation container (".anim") for locally retargeted
    // clips (e.g. Mixamo FBX baked onto the UO Biped skeleton). It is deliberately
    // dumb: per bone a name + rest pose + rotation/position keyframe arrays, which
    // maps 1:1 onto GrannyAnimation/GrannyTrack so the existing player (name-based
    // track binding) can consume it with no changes.
    public static class GrannyAnimFile
    {
        static readonly byte[] Signature = Encoding.ASCII.GetBytes("UOANIM\0"); // 7 bytes
        const int Version = 1;

        public const string Extension = ".anim";

        public static bool IsAnimFile(string path) =>
            !string.IsNullOrEmpty(path) &&
            path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        // Build a GrannyAnimation from a plain set of per-bone local rotation tracks.
        // positions[i] is the constant local position used for bone i (usually the
        // reference bind position). rotations[i] holds one quaternion per frame.
        public static GrannyAnimation BuildAnimation(
            string name,
            IReadOnlyList<GrannyBone> bones,
            IReadOnlyList<Quaternion[]> rotations,
            float fps)
        {
            var anim = new GrannyAnimation { Name = name };
            int n = bones.Count;
            float dur = 0f;
            for (int i = 0; i < n; i++)
            {
                var rot = rotations[i];
                int keys = rot?.Length ?? 1;
                if (keys > 1) dur = Math.Max(dur, (keys - 1) / Math.Max(0.0001f, fps));

                var track = new GrannyTrack
                {
                    BoneId = bones[i].Id,
                    Name = bones[i].Name,
                    RestPosition = bones[i].LocalPosition,
                    RestRotation = bones[i].LocalRotation,
                    TimesP = new float[] { 0f },
                    Pos = new Vector3[] { bones[i].LocalPosition },
                    TimesR = new float[keys],
                    Rot = new Quaternion[keys]
                };
                for (int k = 0; k < keys; k++)
                {
                    track.TimesR[k] = k / Math.Max(0.0001f, fps);
                    track.Rot[k] = (rot != null && k < rot.Length) ? rot[k] : bones[i].LocalRotation;
                }
                anim.Tracks.Add(track);
                anim.TracksByName[track.Name] = track;
                anim.Skeleton.Add(bones[i]);
                anim.SkeletonByName[bones[i].Name] = bones[i];
            }
            anim.Duration = dur;
            return anim;
        }

        public static bool Save(GrannyAnimation anim, string path)
        {
            if (anim == null || string.IsNullOrEmpty(path)) return false;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
                using var bw = new BinaryWriter(fs, Encoding.UTF8);
                bw.Write(Signature);
                bw.Write(Version);
                WriteString(bw, anim.Name);
                bw.Write(anim.Duration);
                bw.Write(anim.Tracks.Count);

                foreach (var tr in anim.Tracks)
                {
                    WriteString(bw, tr.Name);
                    bw.Write(tr.RestPosition.X); bw.Write(tr.RestPosition.Y); bw.Write(tr.RestPosition.Z);
                    bw.Write(tr.RestRotation.X); bw.Write(tr.RestRotation.Y); bw.Write(tr.RestRotation.Z); bw.Write(tr.RestRotation.W);

                    int np = tr.TimesP?.Length ?? 0;
                    bw.Write(np);
                    for (int i = 0; i < np; i++)
                    {
                        bw.Write(tr.TimesP[i]);
                        var p = (tr.Pos != null && i < tr.Pos.Length) ? tr.Pos[i] : Vector3.Zero;
                        bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Z);
                    }

                    int nr = tr.TimesR?.Length ?? 0;
                    bw.Write(nr);
                    for (int i = 0; i < nr; i++)
                    {
                        bw.Write(tr.TimesR[i]);
                        var q = (tr.Rot != null && i < tr.Rot.Length) ? tr.Rot[i] : Quaternion.Identity;
                        bw.Write(q.X); bw.Write(q.Y); bw.Write(q.Z); bw.Write(q.W);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        public static GrannyAnimation Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs, Encoding.UTF8);

                var sig = br.ReadBytes(Signature.Length);
                if (sig.Length != Signature.Length) return null;
                for (int i = 0; i < Signature.Length; i++) if (sig[i] != Signature[i]) return null;

                int version = br.ReadInt32();
                if (Version != version) return null;

                var anim = new GrannyAnimation { Name = ReadString(br) };
                anim.Duration = br.ReadSingle();
                int trackCount = br.ReadInt32();

                for (int t = 0; t < trackCount; t++)
                {
                    var tr = new GrannyTrack { Name = ReadString(br) };
                    tr.RestPosition = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    tr.RestRotation = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                    int np = br.ReadInt32();
                    tr.TimesP = new float[np];
                    tr.Pos = new Vector3[np];
                    for (int i = 0; i < np; i++)
                    {
                        tr.TimesP[i] = br.ReadSingle();
                        tr.Pos[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    }

                    int nr = br.ReadInt32();
                    tr.TimesR = new float[nr];
                    tr.Rot = new Quaternion[nr];
                    for (int i = 0; i < nr; i++)
                    {
                        tr.TimesR[i] = br.ReadSingle();
                        tr.Rot[i] = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    }

                    anim.Tracks.Add(tr);
                    anim.TracksByName[tr.Name] = tr;
                }
                return anim;
            }
            catch
            {
                return null;
            }
        }

        static void WriteString(BinaryWriter bw, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? string.Empty);
            bw.Write(b.Length);
            bw.Write(b);
        }

        static string ReadString(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len <= 0) return string.Empty;
            byte[] b = br.ReadBytes(len);
            return Encoding.UTF8.GetString(b);
        }
    }
}
