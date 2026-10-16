using UnityEngine;

namespace Hkmp.Fsm;

// Presentation follows a short history of authoritative positions. Only remote
// players opt into bounded prediction; entities never extrapolate.
internal sealed class PositionInterpolation : MonoBehaviour {
    internal bool Smooth;
    internal bool WorldSpace;
    internal bool PredictPlayer;
    private readonly PositionHistory history = new();
    private void Apply(Vector3 position) {
        if (WorldSpace) transform.position = position;
        else transform.localPosition = position;
    }
    private void LateUpdate() {
        if (Smooth && history.HasSamples) Apply(history.Evaluate(Time.time, PredictPlayer));
    }
    public void SetNewPosition(Vector3 position, bool immediate = false) {
        if (!Smooth || immediate || !gameObject.activeInHierarchy) {
            history.Clear();
            if (PredictPlayer) history.Add(position, Time.time);
            Apply(position); return;
        }
        if (history.Add(position, Time.time)) Apply(position);
    }
    internal void Flush() {
        if (history.HasSamples) Apply(history.Latest);
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
    internal Vector3 Evaluate(float now, bool predictPlayer = false) {
        var time = now - Delay;
        while (count > (predictPlayer ? 2 : 1) && times[1] <= time) RemoveFirst();
        if (count == 1 || time <= times[0]) return positions[0];
        if (predictPlayer && time > times[1]) {
            // Bridge ordinary packet jitter for avatars only. There is no
            // simulation timestamp or explicit stop packet: fade the forecast
            // back to the last received position if updates stop altogether.
            var age = time - times[1];
            var offset = (positions[1] - positions[0]) / (times[1] - times[0]) * Mathf.Min(age, .075f);
            offset = Vector3.ClampMagnitude(offset, 2f);
            return positions[1] + offset * (1 - Mathf.SmoothStep(0, 1, (age - .075f) / .125f));
        }
        return Vector3.Lerp(positions[0], positions[1], (time - times[0]) / (times[1] - times[0]));
    }
    private void RemoveFirst() {
        for (var i = 1; i < count; i++) { positions[i - 1] = positions[i]; times[i - 1] = times[i]; }
        count--;
    }
}
