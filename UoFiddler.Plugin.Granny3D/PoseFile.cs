using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenTK.Mathematics;

namespace UoFiddler.Plugin.Granny3D
{
    // A saved character pose (.pose). Stores, for every bone of the host skeleton,
    // its ABSOLUTE local transform (quaternion rotation + local translation), so
    // reloading reproduces exactly what was on screen regardless of the animation
    // currently playing or the bind pose. A global root displacement (offset + yaw)
    // moves the whole character against a fixed imported equipment mesh. The mesh
    // itself is never modified, only the skeleton driving the skinning.
    //
    // Version history:
    //   1 = legacy additive Euler-degree deltas (still readable)
    //   2 = absolute local pose (quaternion + translation per bone)
    //   3 = bind-relative local pose: stores both the captured LOCAL pose and the
    //       bind/base LOCAL it was captured against, so the applied pose is
    //       delta = posed * bind^-1, re-applied as delta * currentBase. This makes
    //       a pose reproduce correctly on any mesh whose bind/hierarchy differs
    //       (root rest orientation preserved, no cross-mesh scrambling).
    public class PoseFile
    {
        public const string Extension = ".pose";
        public const int Version = 3;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("UOPOSE\0");

        public int StoredVersion = Version;

        // Global root displacement (world units / degrees).
        public Vector3 RootOffset;
        public float RootYaw;

        // bone name -> pose
        public Dictionary<string, PoseBone> Bones =
            new Dictionary<string, PoseBone>(StringComparer.OrdinalIgnoreCase);

        public struct PoseBone
        {
            public Quaternion Rot;   // captured LOCAL pose rotation
            public Vector3 Pos;      // captured LOCAL pose translation (parent space)
            // Bind/base local the pose was captured against (v3). When the pose is
            // applied, delta = posed * bind^-1 is composed onto the target's current
            // base local, so the result is mesh-independent. For v2 files these are
            // left at identity -> delta == posed (absolute replacement, old behavior).
            public Quaternion BindRot;
            public Vector3 BindPos;
            public bool HasBind;
            // Legacy (v1) additive data, retained so old files still load.
            public bool Legacy;
            public Vector3 LegacyRotDeg;
            public Vector3 LegacyPos;
        }

        public static bool IsPoseFile(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
        }

        public static bool Save(PoseFile pose, string path)
        {
            if (pose == null || string.IsNullOrEmpty(path)) return false;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                int version = pose.StoredVersion;
                if (version < 1 || version > Version) version = Version;

                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using var w = new BinaryWriter(fs, Encoding.UTF8);
                w.Write(Magic);
                w.Write(version);
                w.Write(pose.RootOffset.X); w.Write(pose.RootOffset.Y); w.Write(pose.RootOffset.Z);
                w.Write(pose.RootYaw);
                w.Write(pose.Bones.Count);
                foreach (var kv in pose.Bones)
                {
                    w.Write(kv.Key ?? string.Empty);
                    if (version >= 2)
                    {
                        var r = kv.Value.Rot;
                        w.Write(r.X); w.Write(r.Y); w.Write(r.Z); w.Write(r.W);
                        w.Write(kv.Value.Pos.X); w.Write(kv.Value.Pos.Y); w.Write(kv.Value.Pos.Z);
                        if (version >= 3)
                        {
                            // v3: bind/base local the pose was captured against.
                            w.Write(kv.Value.HasBind ? 1 : 0);
                            var br = kv.Value.BindRot;
                            w.Write(br.X); w.Write(br.Y); w.Write(br.Z); w.Write(br.W);
                            w.Write(kv.Value.BindPos.X); w.Write(kv.Value.BindPos.Y); w.Write(kv.Value.BindPos.Z);
                        }
                    }
                    else
                    {
                        // v1: additive Euler-degree delta + parent-space offset.
                        w.Write(kv.Value.LegacyRotDeg.X);
                        w.Write(kv.Value.LegacyRotDeg.Y);
                        w.Write(kv.Value.LegacyRotDeg.Z);
                        w.Write(kv.Value.LegacyPos.X);
                        w.Write(kv.Value.LegacyPos.Y);
                        w.Write(kv.Value.LegacyPos.Z);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool Load(string path, out PoseFile pose, out string error)
        {
            pose = null;
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "Fichier introuvable.";
                return false;
            }
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var r = new BinaryReader(fs, Encoding.UTF8);

                var magic = r.ReadBytes(Magic.Length);
                if (magic.Length != Magic.Length)
                {
                    error = "Fichier tronqué.";
                    return false;
                }
                for (int i = 0; i < Magic.Length; i++)
                    if (magic[i] != Magic[i])
                    {
                        error = "Ce n'est pas un fichier .pose (signature invalide).";
                        return false;
                    }

                int version = r.ReadInt32();
                if (version < 1 || version > Version)
                {
                    error = $"Version non supportée ({version}).";
                    return false;
                }

                pose = new PoseFile
                {
                    StoredVersion = version,
                    RootOffset = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    RootYaw = r.ReadSingle()
                };

                int count = r.ReadInt32();
                if (count < 0 || count > 10000)
                {
                    error = "Nombre d'os invalide.";
                    pose = null;
                    return false;
                }
                for (int i = 0; i < count; i++)
                {
                    string name = r.ReadString();
                    PoseBone pb;
                    if (version >= 2)
                    {
                        pb = new PoseBone
                        {
                            Rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                            Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle())
                        };
                        if (version >= 3)
                        {
                            pb.HasBind = r.ReadInt32() != 0;
                            pb.BindRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            pb.BindPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        }
                    }
                    else
                    {
                        pb = new PoseBone
                        {
                            Legacy = true,
                            LegacyRotDeg = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                            LegacyPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle())
                        };
                    }
                    if (!string.IsNullOrEmpty(name))
                        pose.Bones[name] = pb;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                pose = null;
                return false;
            }
        }
    }
}
