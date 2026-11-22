using UnityEngine;
using UnityEngine.UI;

namespace HollowConnect;

// Render the original HUD sprite geometry and atlas UVs in the overlay. Only
// the display size changes. The game still owns the texture and its HUD FSMs.
internal sealed class NativeMaskGraphic : MaskableGraphic {
    internal tk2dSpriteDefinition Frame { get; private set; }
    public override Texture mainTexture => Frame?.material?.mainTexture ?? base.mainTexture;

    internal static tk2dSpriteAnimator FindHudAnimator() {
        var hud = GameCameras.instance?.hudCanvas;
        return hud == null ? null : hud.transform.Find("Health/Health 1/Idle")?.GetComponent<tk2dSpriteAnimator>() ??
            System.Array.Find(hud.GetComponentsInChildren<PlayMakerFSM>(true), fsm => fsm.FsmName == "health_display")
                ?.transform.Find("Idle")?.GetComponent<tk2dSpriteAnimator>();
    }

    internal static tk2dSpriteDefinition ReadFrame(tk2dSpriteAnimator animator, string clipName) {
        var clip = animator == null ? null : animator.GetClipByName(clipName);
        if (clip == null || clip.frames.Length == 0) return null;
        var frame = clip.frames[0];
        return frame.spriteCollection.inst.spriteDefinitions[frame.spriteId];
    }

    internal void SetFrame(tk2dSpriteDefinition frame) {
        if (Frame == frame) return;
        Frame = frame;
        SetAllDirty();
    }

    protected override void OnPopulateMesh(VertexHelper mesh) {
        mesh.Clear();
        if (Frame == null) return;
        float left = float.PositiveInfinity, right = float.NegativeInfinity;
        float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
        foreach (var point in Frame.positions) {
            left = Mathf.Min(left, point.x); right = Mathf.Max(right, point.x);
            bottom = Mathf.Min(bottom, point.y); top = Mathf.Max(top, point.y);
        }
        var rect = GetPixelAdjustedRect();
        if (right <= left || top <= bottom) return;
        var scale = Mathf.Min(rect.width / (right - left), rect.height / (top - bottom));
        var center = new Vector2((left + right) * .5f, (bottom + top) * .5f);
        for (var i = 0; i < Frame.positions.Length; i++) {
            var point = (Vector2)Frame.positions[i];
            mesh.AddVert(rect.center + (point - center) * scale, color, Frame.uvs[i]);
        }
        for (var i = 0; i < Frame.indices.Length; i += 3)
            mesh.AddTriangle(Frame.indices[i], Frame.indices[i + 1], Frame.indices[i + 2]);
    }
}
