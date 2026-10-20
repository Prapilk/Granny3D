using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenTK.Mathematics;

namespace UoFiddler.Plugin.Granny3D
{
    // Container for imported 3D character models (e.g. Mixamo FBX converted to UO Z-up).
    // Stores meshes, materials/colors, UVs, skeleton bones and vertex skin weights.
    public class GrannyModelPart
    {
        public string Name { get; set; } = string.Empty;
        public string MaterialName { get; set; } = string.Empty;
        public Vector3 Color { get; set; } = new Vector3(0.72f, 0.66f, 0.55f);
        public string TextureName { get; set; } = string.Empty;
        public Vector3[] Vertices { get; set; } = Array.Empty<Vector3>();
        public Vector3[] Normals { get; set; } = Array.Empty<Vector3>();
        public Vector2[] UVs { get; set; } = Array.Empty<Vector2>();
        public int[] Indices { get; set; } = Array.Empty<int>();
        public VertexInfluence[][] Skin { get; set; } = Array.Empty<VertexInfluence[]>();
    }

    public class GrannyModelFile
    {
        public const string Extension = ".char";
        static readonly byte[] Signature = Encoding.ASCII.GetBytes("UOCHAR\0"); // 7 bytes
        const int Version = 1;

        public string Name { get; set; } = string.Empty;
        public List<GrannyBone> Bones { get; set; } = new List<GrannyBone>();
        public List<GrannyModelPart> Parts { get; set; } = new List<GrannyModelPart>();

        public static bool IsModelFile(string path) =>
            !string.IsNullOrEmpty(path) &&
            path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        public static bool Save(GrannyModelFile model, string path)
        {
            if (model == null || string.IsNullOrEmpty(path)) return false;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                using var bw = new BinaryWriter(fs, Encoding.UTF8);

                bw.Write(Signature);
                bw.Write(Version);
                WriteString(bw, model.Name);

                // Bones
                int boneCount = model.Bones?.Count ?? 0;
                bw.Write(boneCount);
                for (int i = 0; i < boneCount; i++)
                {
                    var b = model.Bones![i];
                    bw.Write(b.Id);
                    bw.Write(b.ParentId);
                    WriteString(bw, b.Name);

                    bw.Write(b.LocalPosition.X);
                    bw.Write(b.LocalPosition.Y);
                    bw.Write(b.LocalPosition.Z);

                    bw.Write(b.LocalRotation.X);
                    bw.Write(b.LocalRotation.Y);
                    bw.Write(b.LocalRotation.Z);
                    bw.Write(b.LocalRotation.W);

                    WriteMatrix(bw, b.InverseBindMatrix);
                }

                // Parts
                int partCount = model.Parts?.Count ?? 0;
                bw.Write(partCount);
                for (int p = 0; p < partCount; p++)
                {
                    var part = model.Parts![p];
                    WriteString(bw, part.Name);
                    WriteString(bw, part.MaterialName);
                    bw.Write(part.Color.X);
                    bw.Write(part.Color.Y);
                    bw.Write(part.Color.Z);
                    WriteString(bw, part.TextureName);

                    int vertCount = part.Vertices?.Length ?? 0;
                    bw.Write(vertCount);
                    for (int v = 0; v < vertCount; v++)
                    {
                        var pt = part.Vertices![v];
                        bw.Write(pt.X); bw.Write(pt.Y); bw.Write(pt.Z);
                    }

                    int normCount = part.Normals?.Length ?? 0;
                    bw.Write(normCount);
                    for (int v = 0; v < normCount; v++)
                    {
                        var n = part.Normals![v];
                        bw.Write(n.X); bw.Write(n.Y); bw.Write(n.Z);
                    }

                    int uvCount = part.UVs?.Length ?? 0;
                    bw.Write(uvCount);
                    for (int v = 0; v < uvCount; v++)
                    {
                        var uv = part.UVs![v];
                        bw.Write(uv.X); bw.Write(uv.Y);
                    }

                    int idxCount = part.Indices?.Length ?? 0;
                    bw.Write(idxCount);
                    for (int i = 0; i < idxCount; i++)
                    {
                        bw.Write(part.Indices![i]);
                    }

                    int skinCount = part.Skin?.Length ?? 0;
                    bw.Write(skinCount);
                    for (int v = 0; v < skinCount; v++)
                    {
                        var inf = part.Skin![v];
                        int infCount = inf?.Length ?? 0;
                        bw.Write(infCount);
                        for (int k = 0; k < infCount; k++)
                        {
                            bw.Write(inf![k].BoneId);
                            bw.Write(inf[k].Weight);
                        }
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool Load(string path, out GrannyModelFile? model)
        {
            model = Load(path);
            return model != null;
        }

        public static GrannyModelFile? Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                using var br = new BinaryReader(fs, Encoding.UTF8);

                byte[] sig = br.ReadBytes(Signature.Length);
                for (int i = 0; i < Signature.Length; i++)
                    if (sig[i] != Signature[i]) return null;

                int ver = br.ReadInt32();
                if (ver != Version) return null;

                var model = new GrannyModelFile();
                model.Name = ReadString(br);

                // Bones
                int boneCount = br.ReadInt32();
                model.Bones = new List<GrannyBone>(boneCount);
                for (int i = 0; i < boneCount; i++)
                {
                    var b = new GrannyBone
                    {
                        Id = br.ReadInt32(),
                        ParentId = br.ReadInt32(),
                        Name = ReadString(br),
                        LocalPosition = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                        LocalRotation = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                        InverseBindMatrix = ReadMatrix(br)
                    };
                    model.Bones.Add(b);
                }

                // Parts
                int partCount = br.ReadInt32();
                model.Parts = new List<GrannyModelPart>(partCount);
                for (int p = 0; p < partCount; p++)
                {
                    var part = new GrannyModelPart
                    {
                        Name = ReadString(br),
                        MaterialName = ReadString(br),
                        Color = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                        TextureName = ReadString(br)
                    };

                    int vertCount = br.ReadInt32();
                    part.Vertices = new Vector3[vertCount];
                    for (int v = 0; v < vertCount; v++)
                        part.Vertices[v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                    int normCount = br.ReadInt32();
                    part.Normals = new Vector3[normCount];
                    for (int v = 0; v < normCount; v++)
                        part.Normals[v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                    int uvCount = br.ReadInt32();
                    part.UVs = new Vector2[uvCount];
                    for (int v = 0; v < uvCount; v++)
                        part.UVs[v] = new Vector2(br.ReadSingle(), br.ReadSingle());

                    int idxCount = br.ReadInt32();
                    part.Indices = new int[idxCount];
                    for (int i = 0; i < idxCount; i++)
                        part.Indices[i] = br.ReadInt32();

                    int skinCount = br.ReadInt32();
                    part.Skin = new VertexInfluence[skinCount][];
                    for (int v = 0; v < skinCount; v++)
                    {
                        int infCount = br.ReadInt32();
                        var inf = new VertexInfluence[infCount];
                        for (int k = 0; k < infCount; k++)
                        {
                            inf[k] = new VertexInfluence
                            {
                                BoneId = br.ReadInt32(),
                                Weight = br.ReadSingle()
                            };
                        }
                        part.Skin[v] = inf;
                    }

                    model.Parts.Add(part);
                }

                return model;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteString(BinaryWriter bw, string s)
        {
            s ??= string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            bw.Write(bytes.Length);
            if (bytes.Length > 0) bw.Write(bytes);
        }

        private static string ReadString(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len <= 0) return string.Empty;
            byte[] bytes = br.ReadBytes(len);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteMatrix(BinaryWriter bw, Matrix4 m)
        {
            bw.Write(m.M11); bw.Write(m.M12); bw.Write(m.M13); bw.Write(m.M14);
            bw.Write(m.M21); bw.Write(m.M22); bw.Write(m.M23); bw.Write(m.M24);
            bw.Write(m.M31); bw.Write(m.M32); bw.Write(m.M33); bw.Write(m.M34);
            bw.Write(m.M41); bw.Write(m.M42); bw.Write(m.M43); bw.Write(m.M44);
        }

        private static Matrix4 ReadMatrix(BinaryReader br)
        {
            return new Matrix4(
                br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
                br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
                br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
                br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        }
    }
}
