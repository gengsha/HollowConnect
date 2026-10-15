using System;
using System.IO;
using System.IO.Compression;
using Hkmp.Networking.Packet;

namespace Hkmp.Game.Client.Entity.Component;

// Keep each reliable update a complete snapshot, including in the server's
// late-join cache. Compression must not depend on a previously received packet.
internal static class CombatStatePayload {
    private const ushort Compressed = ushort.MaxValue;

    internal static Packet Encode(Packet snapshot) {
        if (snapshot.Length < 512) return snapshot;
        var raw = snapshot.ToArray();
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(raw, 0, raw.Length);
        if (output.Length + 4 >= raw.Length || raw.Length > ushort.MaxValue) return snapshot;
        var result = new Packet();
        result.Write(Compressed); result.Write((ushort)raw.Length); result.Write(output.ToArray());
        return result;
    }

    internal static Packet Decode(Packet payload, out ushort count) {
        count = payload.ReadUShort();
        if (count != Compressed) return payload;
        var length = payload.ReadUShort();
        if (length < 2) throw new FormatException("Invalid compressed combat length");
        var bytes = new byte[length];
        using var input = new MemoryStream(payload.ReadBytes(payload.Length - 4), false);
        using var zip = new DeflateStream(input, CompressionMode.Decompress);
        var offset = 0;
        while (offset < length) {
            var read = zip.Read(bytes, offset, length - offset);
            if (read == 0) throw new FormatException("Truncated compressed combat snapshot");
            offset += read;
        }
        if (zip.ReadByte() != -1) throw new FormatException("Compressed combat snapshot exceeds its declared length");
        var result = new Packet(bytes); count = result.ReadUShort(); return result;
    }
}
