using UnityEngine;

namespace Hkmp.Fsm;

// Presentation follows a short history of authoritative positions. Never run
// a second AI, extrapolate a projectile through a wall, or smooth a teleport.
internal sealed class PositionInterpolation : MonoBehaviour {
    internal bool Smooth;
    private readonly PositionHistory history = new();
    private void LateUpdate() {
        if (Smooth && history.HasSamples) transform.localPosition = history.Evaluate(Time.time);
    }
    public void SetNewPosition(Vector3 position, bool immediate = false) {
        if (!Smooth || immediate || !gameObject.activeInHierarchy) {
            history.Clear(); transform.localPosition = position; return;
        }
        if (history.Add(position, Time.time)) transform.localPosition = position;
    }
    internal void Flush() {
        if (history.HasSamples) transform.localPosition = history.Latest;
        history.Clear();
    }
    private void OnDisable() => Flush();
}

// Fixed storage; several updates dispatched in one frame share one sample.
internal sealed class PositionHistory {
    internal const float Delay = .05f;
    private readonly Vector3[] positions = new Vector3[8];
    private readonly float[] times = new float[8];
    private int count;
    internal bool HasSamples => count != 0;
    internal Vector3 Latest => positions[count - 1];
    internal void Clear() => count = 0;
    internal bool Add(Vector3 position, float time) {
        if (count == 0 || time - times[count - 1] > .25f || (position - Latest).sqrMagnitude > 64f) {
            count = 1; positions[0] = position; times[0] = time; return true;
        }
        if (time <= times[count - 1]) { positions[count - 1] = position; return false; }
        if (count == positions.Length) RemoveFirst();
        positions[count] = position; times[count++] = time; return false;
    }
    internal Vector3 Evaluate(float now) {
        var time = now - Delay;
        while (count > 1 && times[1] <= time) RemoveFirst();
        if (count == 1 || time <= times[0]) return positions[0];
        return Vector3.Lerp(positions[0], positions[1], (time - times[0]) / (times[1] - times[0]));
    }
    private void RemoveFirst() {
        for (var i = 1; i < count; i++) { positions[i - 1] = positions[i]; times[i - 1] = times[i]; }
        count--;
    }
}
