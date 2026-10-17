using System;
using Hkmp.Game.Client;
using UnityEngine;

namespace HollowConnect;

// The game excludes player layer 9 from dream-hitbox layer 17 collisions.
// Check the actual local polygon only while it is active, without changing
// the global collision matrix or adding receivers to pooled remote players.
internal sealed class TeammateDreamNail : IDisposable {
    private readonly CoopRuntime runtime;
    private TeammateDreamSwing swing;
    private bool extra;
    internal TeammateDreamNail(CoopRuntime runtime) {
        this.runtime = runtime;
        CustomHooks.HeroControllerStartAction += Bind;
    }
    internal void Reset() => extra = false;
    public void Dispose() {
        CustomHooks.HeroControllerStartAction -= Bind;
        if (swing != null) UnityEngine.Object.Destroy(swing);
    }
    private void Bind() {
        var hero = HeroController.instance;
        var hitbox = hero == null ? null : hero.transform.Find("Dream Effects/Hitbox");
        if (hitbox == null) return;
        swing = hitbox.GetComponent<TeammateDreamSwing>() ?? hitbox.gameObject.AddComponent<TeammateDreamSwing>();
        swing.Owner = this;
    }
    internal bool Hit(Collider2D hitbox) {
        if (hitbox == null || !hitbox.enabled) return false;
        foreach (var pair in runtime.Peers) {
            if (!runtime.CanDreamNail(pair.Key)) continue;
            var target = runtime.PlayerObject(pair.Key);
            var body = target.GetComponent<Collider2D>();
            if (body == null || !body.enabled || !hitbox.Distance(body).isOverlapped) continue;
            // Ordinary native Focus costs 33, while soul is integral.
            HeroController.instance.AddMPCharge(extra ? 17 : 16);
            extra = !extra;
            target.GetComponent<SpriteFlash>()?.flashDreamImpact();
            return true;
        }
        return false;
    }
}

internal sealed class TeammateDreamSwing : MonoBehaviour {
    internal TeammateDreamNail Owner;
    internal bool Spent;
    private Collider2D hitbox;
    private void Awake() => hitbox = GetComponent<Collider2D>();
    private void FixedUpdate() {
        if (!Spent && Owner != null) Spent = Owner.Hit(hitbox);
    }
    private void OnDisable() => Spent = false;
}
