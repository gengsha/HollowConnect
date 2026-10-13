using System;
using System.Collections.Generic;
using System.Linq;
using HollowConnect.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect;

// Copy only native visual assets. Never instantiate the shade's AI/FSM, which
// can chase the hero, recover geo, modify saves or damage its own teammate.
internal sealed class ShadeService {
    private readonly CoopRuntime runtime;
    private readonly Dictionary<ushort, ShadeView> views = new Dictionary<ushort, ShadeView>();
    private tk2dSpriteAnimation library;
    private float nextLookup;
    internal ShadeService(CoopRuntime runtime) { this.runtime = runtime; }
    internal ShadeView Get(ushort id) => views.TryGetValue(id, out var view) ? view : null;

    internal void Synchronize() {
        if (!runtime.RescueEnabled || runtime.Resetting || !runtime.Playable) { Clear(); return; }
        if (library == null && Time.unscaledTime >= nextLookup) {
            nextLookup = Time.unscaledTime + 2;
            var native = Resources.FindObjectsOfTypeAll<tk2dSpriteAnimator>().FirstOrDefault(a =>
                a.name == "Hollow Shade" && a.Library != null && a.GetClipByName("Idle") != null);
            if (native != null) library = native.Library;
        }
        var wanted = new HashSet<ushort>();
        if (runtime.Down) {
            wanted.Add(runtime.LocalId);
            Ensure(runtime.LocalId, runtime.DeathSerial, runtime.DeathPosition, HeroController.instance.gameObject);
        }
        foreach (var pair in runtime.Peers) {
            var peer = pair.Value; var state = peer.State;
            if (peer.Life != Life.Down || state == null || state.Scene != Hkmp.CoopContext.LocalScene || CoopRuntime.Now - peer.Seen >= 2) continue;
            wanted.Add(pair.Key);
            Ensure(pair.Key, peer.DownSerial, new Vector3(state.X, state.Y, 0), runtime.PlayerObject(pair.Key));
        }
        foreach (var id in views.Keys.Where(id => !wanted.Contains(id)).ToArray()) Remove(id);
    }

    private void Ensure(ushort id, uint serial, Vector3 anchor, GameObject avatar) {
        if (library == null) return;
        if (views.TryGetValue(id, out var existing) && (existing == null || existing.Serial != serial)) Remove(id);
        if (!views.TryGetValue(id, out var view)) {
            var go = new GameObject("HollowConnect shade " + id);
            go.transform.position = new Vector3(anchor.x, anchor.y, 0);
            // ENEMIES: the original local nail collider uses this collision layer.
            go.layer = (int)GlobalEnums.PhysLayers.ENEMIES;
            view = go.AddComponent<ShadeView>();
            view.Initialize(runtime, id, serial, library);
            views[id] = view;
        }
        view.HideAvatar(avatar);
    }

    internal void Remove(ushort id) {
        if (!views.TryGetValue(id, out var view)) return;
        views.Remove(id);
        if (view != null) { view.Retire(); Object.Destroy(view.gameObject); }
    }
    internal void Clear() { foreach (var id in views.Keys.ToArray()) Remove(id); }
}

internal sealed class ShadeView : MonoBehaviour, IHitResponder {
    private CoopRuntime runtime;
    private ushort id;
    internal uint Serial { get; private set; }
    private tk2dSpriteAnimator animator;
    private Transform visual;
    private Collider2D hitbox;
    private GameObject hiddenAvatar;
    private readonly List<(Renderer renderer, bool wasHidden)> hidden = new List<(Renderer, bool)>();
    private float nextHit;
    private bool broken;

    internal void Initialize(CoopRuntime owner, ushort player, uint serial, tk2dSpriteAnimation library) {
        runtime = owner; id = player; Serial = serial;
        var art = new GameObject("Native shade visual"); visual = art.transform; visual.SetParent(transform, false);
        var sprite = art.AddComponent<tk2dSprite>();
        var frame = library.GetClipByName("Idle").frames[0];
        sprite.SetSprite(frame.spriteCollection, frame.spriteId);
        animator = art.AddComponent<tk2dSpriteAnimator>(); animator.Library = library; animator.Play("Idle");
        var box = gameObject.AddComponent<BoxCollider2D>();
        box.size = new Vector2(1.2f, 1.8f); box.isTrigger = true; hitbox = box;
        var body = gameObject.AddComponent<Rigidbody2D>(); body.isKinematic = true; body.gravityScale = 0;
    }

    private void Update() {
        if (!broken && visual != null) visual.localPosition = new Vector3(0, .12f * Mathf.Sin(Time.time * 2.4f + id), 0);
        foreach (var entry in hidden) if (entry.renderer != null) entry.renderer.forceRenderingOff = true;
    }

    public void Hit(HitInstance hit) {
        if (broken || runtime == null || Time.unscaledTime < nextHit || hit.AttackType != AttackTypes.Nail || hit.DamageDealt <= 0) return;
        var hero = HeroController.instance;
        var source = hit.Source;
        // Remote HKMP slash clones and enemy projectiles must never send a
        // second local rescue request. Only the living local hero may hit it.
        if (hero == null || source == null || !source.activeInHierarchy || !source.transform.IsChildOf(hero.transform)) return;
        // Original nail swings use the damages_enemy FSM; some attack objects
        // use DamageEnemies instead. Both reach IHitResponder via HitTaker.
        var damage = source.GetComponent<DamageEnemies>();
        if (source.GetComponent<NailSlash>() == null &&
            (damage == null || !damage.enabled || damage.attackType != AttackTypes.Nail) &&
            !source.GetComponents<PlayMakerFSM>().Any(f => f.enabled && f.FsmName == "damages_enemy")) return;
        if (runtime.RequestShadeHit(id, Serial)) nextHit = Time.unscaledTime + .25f;
    }

    internal void HideAvatar(GameObject avatar) {
        if (avatar == hiddenAvatar) return;
        RestoreAvatar(); hiddenAvatar = avatar;
        if (avatar == null) return;
        foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true)) {
            hidden.Add((renderer, renderer.forceRenderingOff)); renderer.forceRenderingOff = true;
        }
    }
    internal void RestoreAvatar() {
        foreach (var entry in hidden) if (entry.renderer != null) entry.renderer.forceRenderingOff = entry.wasHidden;
        hidden.Clear(); hiddenAvatar = null;
    }
    internal void Retire() { broken = true; if (hitbox != null) hitbox.enabled = false; RestoreAvatar(); }
    private void OnDestroy() { RestoreAvatar(); }
}
