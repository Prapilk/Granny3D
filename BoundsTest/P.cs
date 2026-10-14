using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenTK.Mathematics;

class TestParserByName {
    static List<string> ParseStrings(BinaryReader reader, List<(uint id, uint offset, uint size)> chunks) {
        var strChunk = chunks.Where(c => c.id == 0xCA5E0200 && c.offset > 0x1000).OrderByDescending(c => c.size).FirstOrDefault();
        if (strChunk.id == 0)
            strChunk = chunks.Where(c => c.id == 0xCA5E0200).OrderByDescending(c => c.size).FirstOrDefault();
        List<string> strings = new List<string>();
        if (strChunk.id != 0) {
            reader.BaseStream.Seek(strChunk.offset, SeekOrigin.Begin);
            int count = reader.ReadInt32();
            int totalBytes = reader.ReadInt32();
            for (int i = 0; i < count; i++) {
                List<byte> bList = new List<byte>();
                byte b;
                while ((b = reader.ReadByte()) != 0) bList.Add(b);
                strings.Add(Encoding.ASCII.GetString(bList.ToArray()));
            }
        }
        return strings;
    }

    static void Main() {
        Console.WriteLine("Testing bone name matching implementation...");
    }
}
