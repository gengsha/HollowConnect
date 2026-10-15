using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Networking.Packet;
using Hkmp.Game.Client.Entity.Component;

internal static class PacketEncodingProbe {
    public static void WireFormat() {
        var packet = new Packet();
        var expected = new List<byte>();
        void Compare() {
            if (!packet.ToArray().SequenceEqual(expected)) throw new Exception("Scalar encoding changed the existing wire bytes");
        }
        foreach (var value in new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue }) {
            packet.Write(value); expected.AddRange(BitConverter.GetBytes(value));
            packet.Write((ulong)value); expected.AddRange(BitConverter.GetBytes((ulong)value));
            packet.Write((int)value); expected.AddRange(BitConverter.GetBytes((int)value));
            packet.Write((uint)value); expected.AddRange(BitConverter.GetBytes((uint)value));
            packet.Write((short)value); expected.AddRange(BitConverter.GetBytes((short)value));
            packet.Write((ushort)value); expected.AddRange(BitConverter.GetBytes((ushort)value));
            packet.Write((sbyte)value); expected.AddRange(BitConverter.GetBytes((sbyte)value));
        }
        foreach (var value in new[] { false, true }) { packet.Write(value); expected.AddRange(BitConverter.GetBytes(value)); }
        var random = new Random(917);
        var bytes = new byte[8];
        for (var i = 0; i < 2048; i++) {
            random.NextBytes(bytes);
            // Compare bits, including NaNs, signed zero and subnormal values.
            var single = BitConverter.ToSingle(bytes, 0); var number = BitConverter.ToDouble(bytes, 0);
            packet.Write(single); expected.AddRange(BitConverter.GetBytes(single));
            packet.Write(number); expected.AddRange(BitConverter.GetBytes(number));
        }
        foreach (var bits in new[] { 0U, 0x80000000U, 0x7f800000U, 0xff800000U, 0x7fc00001U }) {
            var value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
            packet.Write(value); expected.AddRange(BitConverter.GetBytes(value));
        }
        Compare();
        packet.WriteLength(); expected.InsertRange(0, BitConverter.GetBytes((ushort)expected.Count)); Compare();
        packet.WriteDatagramLength(); expected.InsertRange(0, BitConverter.GetBytes((uint)expected.Count)); Compare();
    }

    public static void ScratchOwnership() {
        var sent = new Packet(); sent.Write("snapshot"); sent.Write(-123.5f);
        var expected = sent.ToArray();
        var sample = new Packet(expected);
        sample.ReadString(); sample.ReadFloat();
        sample.Clear(); sample.Write("snapshot"); sample.Write(-123.5f);
        if (!sample.ContentEquals(sent)) throw new Exception("Identical scratch snapshot was treated as changed");
        sample.Clear(); sample.Write("changed"); sample.Write(1f);
        if (sample.ContentEquals(sent) || !sent.ToArray().SequenceEqual(expected))
            throw new Exception("Scratch reuse mutated a queued snapshot or missed a change");
        sample.Clear(); sample.Write((byte)1);
        if (sample.ContentEquals(sent) || sample.Length != 1) throw new Exception("Scratch retained a longer previous payload");
    }

    public static void CompleteCombatPayload() {
        foreach (var size in new[] { 20, 4000, 65000 }) {
            foreach (var repetitive in new[] { false, true }) {
                var raw = new Packet(); raw.Write((ushort)3);
                var bytes = new byte[size];
                if (!repetitive) new Random(71).NextBytes(bytes);
                raw.Write(bytes);
                var encoded = CombatStatePayload.Encode(raw);
                if (encoded.Length > raw.Length) throw new Exception("Combat compression enlarged an update");
                if (repetitive && size > 512 && encoded.Length >= raw.Length / 2) throw new Exception("Repetitive combat payload was not compressed");
                // Each snapshot decodes on its own, as in a late join, resend or
                // authority handoff; no base packet is available to the decoder.
                var decoded = CombatStatePayload.Decode(new Packet(encoded.ToArray()), out var count);
                if (count != 3 || !decoded.ReadBytes(size).SequenceEqual(bytes)) throw new Exception("Combat compression lost full-snapshot state");
                if (!raw.ToArray().Skip(2).SequenceEqual(bytes)) throw new Exception("Encoding mutated the comparison snapshot");
            }
        }
        var full = new Packet(); full.Write((ushort)1); full.Write(new byte[2000]);
        var wire = CombatStatePayload.Encode(full).ToArray();
        foreach (var invalidLength in new ushort[] { 1, 2, 2100 }) {
            var malformed = (byte[])wire.Clone();
            Array.Copy(BitConverter.GetBytes(invalidLength), 0, malformed, 2, 2);
            var rejected = false;
            try { CombatStatePayload.Decode(new Packet(malformed), out _); }
            catch (FormatException) { rejected = true; }
            if (!rejected) throw new Exception("Malformed compressed combat length was accepted");
        }
    }
}
