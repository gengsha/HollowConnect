using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Logging;
using Hkmp.Networking.Packet.Data;

namespace Hkmp.Networking.Packet.Update;

/// <summary>
/// Abstract base class for the update packet.
/// </summary>
/// <typeparam name="TPacketId"></typeparam>
internal abstract class UpdatePacket<TPacketId> : BasePacket<TPacketId> where TPacketId : Enum {
    /// <summary>
    /// The sequence number of this packet.
    /// </summary>
    public ushort Sequence { get; set; }

    /// <summary>
    /// The acknowledgement number of this packet.
    /// </summary>
    public ushort Ack { get; set; }

    /// <summary>
    /// An array containing booleans that indicate whether sequence number (Ack - x) is also acknowledged
    /// for the x-th value in the array.
    /// </summary>
    public bool[] AckField { get; private set; }
    
    /// <summary>
    /// Resend packet data indexed by sequence number it originates from.
    /// </summary>
    protected readonly Dictionary<ushort, Dictionary<TPacketId, IPacketData>> ResendPacketData;
    
    /// <summary>
    /// Resend addon packet data indexed by sequence number it originates from.
    /// </summary>
    protected readonly Dictionary<ushort, Dictionary<byte, AddonPacketData>> ResendAddonPacketData;
    
    protected UpdatePacket() {
        AckField = new bool[UdpUpdateManager.AckSize];
        
        ResendPacketData = new Dictionary<ushort, Dictionary<TPacketId, IPacketData>>();
        ResendAddonPacketData = new Dictionary<ushort, Dictionary<byte, AddonPacketData>>();
    }
    
    /// <summary>
    /// Write header info into the given packet (sequence number, acknowledgement number and ack field).
    /// </summary>
    /// <param name="packet">The packet to write the header info into.</param>
    private void WriteHeaders(Packet packet) {
        packet.Write(Sequence);
        packet.Write(Ack);

        ulong ackFieldInt = 0;
        ulong currentFieldValue = 1;
        for (var i = 0; i < UdpUpdateManager.AckSize; i++) {
            if (AckField[i]) {
                ackFieldInt |= currentFieldValue;
            }

            currentFieldValue *= 2;
        }

        packet.Write(ackFieldInt);
    }

    /// <summary>
    /// Read header info from the given packet (sequence number, acknowledgement number and ack field).
    /// </summary>
    /// <param name="packet">The packet to read header info from.</param>
    private void ReadHeaders(Packet packet) {
        Sequence = packet.ReadUShort();
        Ack = packet.ReadUShort();

        // Initialize the AckField array
        AckField = new bool[UdpUpdateManager.AckSize];

        var ackFieldInt = packet.ReadULong();
        ulong currentFieldValue = 1;
        for (var i = 0; i < UdpUpdateManager.AckSize; i++) {
            AckField[i] = (ackFieldInt & currentFieldValue) != 0;

            currentFieldValue *= 2;
        }
    }

    /// <inheritdoc />
    public override void CreatePacket(Packet packet) {
        WriteHeaders(packet);
        
        base.CreatePacket(packet);
        
        // Put the length of the resend data as an ushort in the packet
        var resendLength = (ushort) ResendPacketData.Count;
        if (ResendPacketData.Count > ushort.MaxValue) {
            resendLength = ushort.MaxValue;

            Logger.Error("Length of resend packet data dictionary does not fit in ushort");
        }

        packet.Write(resendLength);

        // Add each entry of lost data to resend to the packet
        foreach (var seqPacketDataPair in ResendPacketData) {
            var seq = seqPacketDataPair.Key;
            var packetData = seqPacketDataPair.Value;

            // Make sure to not put more resend data in the packet than we specified
            if (resendLength-- == 0) {
                break;
            }

            // First write the sequence number it belongs to
            packet.Write(seq);

            // Then write the reliable packet data and note that this packet now contains reliable data
            WritePacketData(packet, packetData);
            ContainsReliableData = true;
        }
        
        // Put the length of the addon resend data as an ushort in the packet
        resendLength = (ushort) ResendAddonPacketData.Count;
        if (ResendAddonPacketData.Count > ushort.MaxValue) {
            resendLength = ushort.MaxValue;

            Logger.Error("Length of addon resend packet data dictionary does not fit in ushort");
        }

        packet.Write(resendLength);

        // Add each entry of lost addon data to resend to the packet
        foreach (var seqAddonDictPair in ResendAddonPacketData) {
            var seq = seqAddonDictPair.Key;
            var addonDataDict = seqAddonDictPair.Value;

            // Make sure to not put more resend data in the packet than we specified
            if (resendLength-- == 0) {
                break;
            }

            // First write the sequence number it belongs to
            packet.Write(seq);

            // Then write the reliable addon data for all addons and note that this packet
            // now contains reliable data
            WriteAddonDataDict(packet, addonDataDict);
            ContainsReliableData = true;
        }
        
        packet.WriteDatagramLength();
    }

    /// <inheritdoc />
    public override bool ReadPacket(Packet packet) {
        // TODO: maybe get rid of exception catching in packet reading and rely on bool return values for all
        // packet reading methods (including Packet class)
        try {
            ReadHeaders(packet);
        } catch (Exception e) {
            Logger.Debug($"Exception while reading headers of packet:\n{e}");
            return false;
        }

        if (!base.ReadPacket(packet)) {
            return false;
        }

        try {
            // Read the length of the resend data
            var resendLength = packet.ReadUShort();

            while (resendLength-- > 0) {
                // Read the sequence number of the packet it was lost from
                var seq = packet.ReadUShort();

                // Create a new dictionary for the packet data and read the data from the packet into it
                var packetData = new Dictionary<TPacketId, IPacketData>();
                ReadPacketData(packet, packetData);

                // Input the data into the resend dictionary keyed by its sequence number
                ResendPacketData[seq] = packetData;
            }

            // Read the length of the addon resend data
            resendLength = packet.ReadUShort();

            while (resendLength-- > 0) {
                // Read the sequence number of the packet it was lost from
                var seq = packet.ReadUShort();

                // Create a new dictionary for the addon data and read the data from the packet into it
                var addonDataDict = new Dictionary<byte, AddonPacketData>();
                ReadAddonDataDict(packet, addonDataDict);

                // Input the dictionary into the resend dictionary keyed by its sequence number
                ResendAddonPacketData[seq] = addonDataDict;
            }
        } catch (Exception e) {
            Logger.Debug($"Exception while reading update packet resend data:\n{e}");
            return false;
        }

        return true;
    }
    
    /// <summary>
    /// Set the reliable packet data contained in the lost packet as resend data in this one.
    /// </summary>
    /// <param name="lostPacket">The update packet instance that was lost.</param>
    public void SetLostReliableData(UpdatePacket<TPacketId> lostPacket) {
        // A retransmission can itself be lost. Keep each batch under its original
        // sequence rather than assigning the merged payload to the carrier. The
        // same origin may also occur in several timed-out carriers in one send.
        foreach (var pair in lostPacket.ResendPacketData) {
            CopyOrigin(pair.Key, pair.Value);
        }
        CopyOrigin(lostPacket.Sequence, lostPacket.NormalPacketData);

        foreach (var pair in lostPacket.ResendAddonPacketData) {
            CopyAddonOrigin(pair.Key, pair.Value);
        }
        CopyAddonOrigin(lostPacket.Sequence, lostPacket.AddonPacketData);
        IsAllPacketDataCached = false;
    }

    private void CopyOrigin(ushort sequence, Dictionary<TPacketId, IPacketData> data) {
        var reliable = CopyReliableDataDict(data, NormalPacketData.ContainsKey);
        if (reliable.Count != 0) ResendPacketData[sequence] = reliable;
    }

    private void CopyAddonOrigin(ushort sequence, Dictionary<byte, AddonPacketData> data) {
        // Create a new dictionary of addon data in which we store all reliable data from the lost packet
        // for all addons in the dictionary
        var toResendAddonData = new Dictionary<byte, AddonPacketData>();

        foreach (var idLostDataPair in data) {
            var addonId = idLostDataPair.Key;
            var addonPacketData = idLostDataPair.Value;

            // Construct a new AddonPacketData instance that holds the reliable data only
            var newAddonPacketData = addonPacketData.GetEmptyCopy();
            newAddonPacketData.PacketData = CopyReliableDataDict(
                addonPacketData.PacketData,
                rawPacketId =>
                    AddonPacketData.TryGetValue(addonId, out var existingAddonData)
                    && existingAddonData.PacketData.ContainsKey(rawPacketId));

            if (newAddonPacketData.PacketData.Count != 0) toResendAddonData[addonId] = newAddonPacketData;
        }

        // Put the addon data dictionary in the resend dictionary keyed by its sequence number
        if (toResendAddonData.Count != 0) ResendAddonPacketData[sequence] = toResendAddonData;
    }
    
    /// <summary>
    /// Copy all reliable data in the given dictionary of lost packet data into a new dictionary.
    /// </summary>
    /// <param name="lostPacketData">The dictionary containing all packet data from a lost packet.</param>
    /// <param name="reliabilityCheck">Function that checks whether for a given key there is newer data
    /// available. If it returns true, lost data will be dropped.</param>
    /// <typeparam name="TKey">The key parameter of the dictionaries to copy.</typeparam>
    /// <returns>A new dictionary containing only the reliable data.</returns>
    private Dictionary<TKey, IPacketData> CopyReliableDataDict<TKey>(
        Dictionary<TKey, IPacketData> lostPacketData,
        Func<TKey, bool> reliabilityCheck
    ) {
        // Create a new dictionary of packet data in which we store all reliable data
        var reliablePacketData = new Dictionary<TKey, IPacketData>();

        foreach (var keyDataPair in lostPacketData) {
            var key = keyDataPair.Key;
            var data = keyDataPair.Value;

            // Check if the packet data is supposed to be reliable
            if (!data.IsReliable) {
                continue;
            }

            // Check whether we can drop it since a newer version of that data already exists
            if (data.DropReliableDataIfNewerExists && reliabilityCheck(key)) {
                continue;
            }

            // Logger.Info($"  Resending {data.GetType()} data");
            reliablePacketData[key] = data;
        }

        return reliablePacketData;
    }

    /// <inheritdoc />
    protected override void CacheAllPacketData() {
        CachedAllPacketData = new Dictionary<TPacketId, IPacketData>();
        CachedAllAddonData = new Dictionary<byte, AddonPacketData>();
        
        void AddResendData<TKey>(
            Dictionary<TKey, IPacketData> dataDict,
            Dictionary<TKey, IPacketData> cachedData
        ) {
            foreach (var packetIdDataPair in dataDict) {
                // Get the ID and the data itself
                var packetId = packetIdDataPair.Key;
                var packetData = packetIdDataPair.Value;

                if (packetData is RawPacketDataCollection collection) {
                    if (!cachedData.TryGetValue(packetId, out var existingPacketData)) {
                        existingPacketData = collection.EmptyCopy();
                        cachedData[packetId] = existingPacketData;
                    }
                    ((RawPacketDataCollection)existingPacketData).DataInstances.AddRange(collection.DataInstances);
                } else {
                    // Batches arrive oldest first; the newest scalar replaces it.
                    cachedData[packetId] = packetData;
                }
            }
        }
        
        // Reliable collections contain both snapshots and events. Keep every
        // event, but apply snapshots in their original sequence order, with the
        // current packet last. Unsigned distance also handles ushort wrap.
        // Copy collection containers so caching cannot alter the original wire
        // payload and cause its next serialization to duplicate resent events.
        foreach (var pair in ResendPacketData.OrderByDescending(p => unchecked((ushort)(Sequence - p.Key)))) {
            AddResendData(pair.Value, CachedAllPacketData);
        }
        AddResendData(NormalPacketData, CachedAllPacketData);
        
        void AddAddonData(Dictionary<byte, AddonPacketData> addons) {
            foreach (var addonIdDataPair in addons) {
                var addonId = addonIdDataPair.Key;
                var addonPacketData = addonIdDataPair.Value;

                if (!CachedAllAddonData.TryGetValue(addonId, out var existingAddonPacketData)) {
                    existingAddonPacketData = addonPacketData.GetEmptyCopy();
                    CachedAllAddonData[addonId] = existingAddonPacketData;
                }
                AddResendData(addonPacketData.PacketData, existingAddonPacketData.PacketData);
            }
        }
        foreach (var pair in ResendAddonPacketData.OrderByDescending(p => unchecked((ushort)(Sequence - p.Key)))) {
            AddAddonData(pair.Value);
        }
        AddAddonData(AddonPacketData);
        
        IsAllPacketDataCached = true;
    }
    
    /// <summary>
    /// Consume each original payload once, whether it arrived directly or in a
    /// retransmission. Built-in and addon data share an origin, so test both
    /// dictionaries before marking that origin as consumed.
    /// </summary>
    internal void FilterReceivedData(SequenceWindow consumedOrigins) {
        var origins = ResendPacketData.Keys.Union(ResendAddonPacketData.Keys)
            .OrderByDescending(seq => unchecked((ushort)(Sequence - seq))).ToArray();
        foreach (var sequence in origins) {
            if (!consumedOrigins.TryAdd(sequence)) {
                ResendPacketData.Remove(sequence);
                ResendAddonPacketData.Remove(sequence);
            }
        }
        if (!consumedOrigins.TryAdd(Sequence)) {
            NormalPacketData.Clear();
            AddonPacketData.Clear();
        }
        IsAllPacketDataCached = false;
    }

    /// <summary>
    /// Drops resend data that is duplicate, i.e. that we already received in an earlier packet.
    /// </summary>
    /// <param name="receivedSequenceNumbers">A queue containing sequence numbers that were already
    /// received.</param>
    public void DropDuplicateResendData(Queue<ushort> receivedSequenceNumbers) {
        // For each key in the resend dictionary, we check whether it is contained in the
        // queue of sequence numbers that we already received. If so, we remove it from the dictionary
        // because it is duplicate data that we already handled
        foreach (var resendSequence in new List<ushort>(ResendPacketData.Keys)) {
            if (receivedSequenceNumbers.Contains(resendSequence)) {
                // Logger.Info("Dropping resent data due to duplication");
                ResendPacketData.Remove(resendSequence);
            }
        }

        // Do the same for addon data
        foreach (var resendSequence in new List<ushort>(ResendAddonPacketData.Keys)) {
            if (receivedSequenceNumbers.Contains(resendSequence)) {
                // Logger.Info("Dropping resent data due to duplication");
                ResendAddonPacketData.Remove(resendSequence);
            }
        }
    }
}
