using System;
using System.Collections;
using Hkmp.Util;
using Modding;
using MonoMod.Cil;
using UnityEngine.Video;

namespace Hkmp.Animation;

// Custom Knight chooses its URL after the native constructor returns. Starting
// the embedded decoder inside that constructor prepares two sources in quick
// succession. Let source selection finish before preparing the native fallback.
internal static class SkinCinematicCompatibility {
    private static bool initialized;

    internal static void Initialize() {
        if (initialized) return;
        IL.XB1CinematicVideoPlayer.ctor += DeferEmbeddedPreparation;
        initialized = true;
    }

    private static void DeferEmbeddedPreparation(ILContext il) {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(i => i.MatchCallvirt<VideoPlayer>(nameof(VideoPlayer.Prepare))))
            throw new InvalidOperationException("Native cinematic preparation call was not found");
        cursor.Remove();
        cursor.EmitDelegate<Action<VideoPlayer>>(PrepareAfterSourceSelection);
    }

    private static void PrepareAfterSourceSelection(VideoPlayer player) {
        if (ModHooks.GetMod("Custom Knight") == null || MonoBehaviourUtil.Instance == null) {
            player.Prepare();
            return;
        }
        MonoBehaviourUtil.Instance.StartCoroutine(PrepareEmbeddedFallback(player));
    }

    private static IEnumerator PrepareEmbeddedFallback(VideoPlayer player) {
        yield return null;
        // The skin loader already prepares URL sources itself. Also tolerate
        // disposal/scene exit before this next-frame fallback gets to run.
        if (player != null && player.source == VideoSource.VideoClip && !player.isPrepared)
            player.Prepare();
    }
}
