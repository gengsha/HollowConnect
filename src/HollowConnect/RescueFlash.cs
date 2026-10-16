using System.Collections;
using UnityEngine;

namespace HollowConnect;

// Remote avatars have no HeroController. Reuse only the native pulse visuals;
// the rescued player's own native Invulnerable coroutine owns damage immunity.
internal sealed class RescueFlash : MonoBehaviour {
    private InvulnerablePulse pulse;
    private tk2dSprite sprite;
    private Color normal;

    internal static void Play(GameObject avatar) {
        if (avatar == null || !avatar.activeInHierarchy) return;
        var previous = avatar.GetComponent<RescueFlash>();
        if (previous != null) previous.enabled = false;
        avatar.AddComponent<RescueFlash>();
    }

    private IEnumerator Start() {
        var hero = HeroController.instance;
        var native = hero.GetComponent<InvulnerablePulse>();
        sprite = GetComponent<tk2dSprite>(); normal = sprite.color;
        pulse = gameObject.AddComponent<InvulnerablePulse>();
        pulse.invulColor = native.invulColor; pulse.pulseDuration = native.pulseDuration;
        // Let the native component capture this avatar's own normal colour.
        yield return null;
        yield return new WaitForSeconds(hero.DAMAGE_FREEZE_DOWN);
        pulse.startInvulnerablePulse();
        yield return new WaitForSeconds(hero.INVUL_TIME);
        Destroy(this);
    }

    private void OnDisable() {
        StopAllCoroutines();
        if (pulse != null) { pulse.enabled = false; Destroy(pulse); pulse = null; }
        if (sprite != null) sprite.color = normal;
        Destroy(this);
    }
}
