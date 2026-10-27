using System;

namespace Hkmp.Api.Server.Networking;

// Distinguishes connection retirement from invalid addon IDs, channels and
// packet collection usage. Existing InvalidOperationException callers remain
// compatible, while callers tolerating disconnects can catch only this case.
internal sealed class AddonConnectionUnavailableException : InvalidOperationException {
    internal ushort PlayerId { get; }
    internal AddonConnectionUnavailableException(ushort playerId, string message) : base(message) {
        PlayerId = playerId;
    }
}
