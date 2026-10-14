using System;

namespace Hkmp.Networking;

/// <summary>
/// Remembers consumed payload origins within the unambiguous half of the ushort
/// sequence space. Unlike ACK history, retransmission history must outlive 64
/// subsequent datagrams and must also include origins received inside carriers.
/// </summary>
internal sealed class SequenceWindow {
    internal const int Size = 32768;
    private readonly bool[] _seen = new bool[Size];
    private ushort _latest;
    private bool _initialized;
    public long LatestRevision { get; private set; }

    public bool TryAdd(ushort sequence) => TryAdd(sequence, out _);

    public bool TryAdd(ushort sequence, out long revision) {
        if (!_initialized) {
            _initialized = true;
            _latest = sequence;
            LatestRevision = sequence;
        } else {
            var ahead = unchecked((ushort)(sequence - _latest));
            if (ahead > 0 && ahead < Size) {
                // Clear slots as they enter the new window, including on wrap.
                for (var i = 1; i <= ahead; i++) _seen[(_latest + i) % Size] = false;
                _latest = sequence;
                LatestRevision += ahead;
            } else if (unchecked((ushort)(_latest - sequence)) >= Size) {
                revision = 0;
                return false;
            }
        }

        revision = LatestRevision - unchecked((ushort)(_latest - sequence));
        var slot = sequence % Size;
        if (_seen[slot]) return false;
        _seen[slot] = true;
        return true;
    }

    public void Clear() {
        Array.Clear(_seen, 0, _seen.Length);
        _initialized = false;
        LatestRevision = 0;
    }
}
