using Hkmp.Networking.Client;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace Hkmp.Game.Client.Entity;

// Moving arena boundaries push the local knight inward. A replica position
// can skip across the knight between packets, so ordinary separation may
// otherwise leave them on the ceiling or outside a contracting side wall.
[DefaultExecutionOrder(1000)]
internal sealed class ColosseumBoundary : MonoBehaviour {
    private enum Edge { Ceiling, Left, Right }
    private NetClient netClient;
    private Collider2D wall;
    private Edge edge;

    internal static void Prepare(GameObject root, NetClient client) {
        if (root.scene.name is not ("Room_Colosseum_Bronze" or "Room_Colosseum_Silver" or "Room_Colosseum_Gold")) return;
        Edge side;
        switch (root.name.Replace("(Clone)", "").Trim()) {
            case "Colosseum Wall C": side = Edge.Ceiling; break;
            case "Colosseum Wall L": side = Edge.Left; break;
            case "Colosseum Wall R": side = Edge.Right; break;
            default: return;
        }
        var slab = root.transform.Find("Wall")?.GetComponent<Collider2D>();
        if (slab == null) return;
        var local = slab.GetComponent<ColosseumBoundary>() ?? slab.gameObject.AddComponent<ColosseumBoundary>();
        local.netClient = client;
        local.wall = slab;
        local.edge = side;
    }

    private void FixedUpdate() {
        var hero = HeroController.instance;
        var gm = global::GameManager.UnsafeInstance;
        if (netClient == null || !netClient.IsConnected || wall == null || !wall.enabled || wall.isTrigger ||
            hero == null || gm == null || UnitySceneManager.GetActiveScene() != gameObject.scene || gm.IsInSceneTransition ||
            !hero.isHeroInPosition || hero.cState.dead || hero.cState.hazardDeath || hero.cState.hazardRespawning ||
            CoopContext.Resetting || CoopContext.LocalRescueDown?.Invoke() == true) return;
        var body = hero.GetComponent<Rigidbody2D>();
        var collider = hero.GetComponent<Collider2D>();
        if (body == null || !body.simulated || collider == null || !collider.enabled || collider.isTrigger ||
            // The left wall deliberately ignores an entrant until their
            // native arena trigger is crossed. Do not pull them through it.
            Physics2D.GetIgnoreCollision(collider, wall)) return;
        Physics2D.SyncTransforms();
        var limit = wall.bounds;
        var bounds = collider.bounds;
        var position = hero.transform.position;
        var velocity = body.velocity;
        if (edge == Edge.Ceiling) {
            if (bounds.max.x <= limit.min.x || bounds.min.x >= limit.max.x || bounds.max.y <= limit.min.y) return;
            position.y -= bounds.max.y - limit.min.y + .02f;
            velocity.y = Mathf.Min(velocity.y, 0);
        } else {
            if (bounds.max.y <= limit.min.y || bounds.min.y >= limit.max.y) return;
            // Leave ordinary edge contact to native collision so wall slides
            // and wall jumps still receive their real contact callbacks.
            if (edge == Edge.Left) {
                if (bounds.center.x >= limit.max.x) return;
                position.x += limit.max.x - bounds.min.x + .02f;
                velocity.x = Mathf.Max(velocity.x, 0);
            } else {
                if (bounds.center.x <= limit.min.x) return;
                position.x -= bounds.max.x - limit.min.x + .02f;
                velocity.x = Mathf.Min(velocity.x, 0);
            }
        }
        hero.transform.position = position;
        body.position = position;
        body.velocity = velocity;
    }
}
