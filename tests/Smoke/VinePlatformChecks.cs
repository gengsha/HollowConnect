using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Save;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private bool VinePlatformCheck => Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_VINE_CHECK") == "1";
    private IEnumerator LocalVinePlatformChecks() {
        var other = host ? "guest" : "host";
        var hero = HeroController.instance;
        if (PlayerData.instance.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "native bench ready before vine room");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => !PlayerData.instance.GetBool("atBench") && hero.acceptingInput, "native bench get-up before vine room");
        }
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        var vines = Array.Empty<VinePlatformCut>();
        foreach (var scene in new[] { "Fungus1_06" }) {
            hero.AffectedByGravity(true);
            GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = "left1",
                Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
            yield return Until(() => GameManager.instance.sceneName == scene && Object.FindObjectsOfType<TransitionPoint>(true).Any(g => g.gameObject.scene.name == scene), "Greenpath scene assets loaded");
            Log("VINE NATIVE GATES " + string.Join(",", Object.FindObjectsOfType<TransitionPoint>(true).Where(g => g.gameObject.scene.name == scene).Select(g => g.name)));
            yield return Until(() => runtime.Playable && GameManager.instance.sceneName == scene &&
                !GameManager.instance.IsInSceneTransition && hero.acceptingInput && engine.ClientManager.SceneSnapshotReady &&
                manager.IsSceneHostDetermined && runtime.Peers.Count == 1, "native Greenpath room and UDP snapshot ready " + scene);
            hero.takeNoDamage = true; hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            var all = Object.FindObjectsOfType<VinePlatformCut>(true).Where(v => v.gameObject.scene.name == scene).ToArray();
            Log("VINE SCENE " + scene + " all=" + all.Length);
            foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene &&
                (f.name.IndexOf("vine", StringComparison.OrdinalIgnoreCase) >= 0 || f.name.IndexOf("plat", StringComparison.OrdinalIgnoreCase) >= 0))) {
                Log("VINE FSM " + EntityIdentity.Path(fsm.transform) + "/" + fsm.FsmName + "=" + fsm.ActiveStateName +
                    " components=" + string.Join(",", fsm.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name)));
                foreach (var state in fsm.FsmStates) Log("VINE FSM STATE " + state.Name + " -> " +
                    string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)) + " actions=" +
                    string.Join(",", state.Actions.Select(a => a.GetType().Name)));
            }
            vines = all.Where(v => v.gameObject.activeInHierarchy && !Field<bool>(v, "activated")).OrderBy(v => EntityIdentity.Path(v.transform)).ToArray();
            Flag(role + ".vine.scanned." + scene);
            yield return Until(() => Has(other + ".vine.scanned." + scene), "both inspected same Greenpath scene " + scene);
            if (vines.Length >= 2) break;
        }
        foreach (var vine in vines) Log("VINE NATIVE " + EntityIdentity.Path(vine.transform) + " bounds=" + vine.GetComponent<Collider2D>().bounds);
        Check(vines.Length >= 2, "real Greenpath scene contains two uncut native vine platforms");
        Check(runtime.Shared, "vine isolation checked with permanent progress sharing enabled");
        Check(vines.All(v => entities.Values.All(e => e.Object.Host == null || !v.transform.IsChildOf(e.Object.Host.transform))),
            "native vine platforms are absent from synchronized entity hierarchies");
        var players = Field<PlayerManager>(engine.ClientManager, "_playerManager");
        yield return Until(() => players.GetPlayerObject(runtime.Peers.Single().Key) != null, "real remote player body ready for synchronized slashes");
        var anchor = hero.gameObject.AddComponent<VineProbeAnchor>();
        anchor.Position = hero.transform.position;
        VinePlatformCut target = null; var remoteContacts = 0;
        On.VinePlatformCut.hook_OnTriggerEnter2D observe = (orig, self, collision) => {
            if (self == target && collision != null && collision.CompareTag("Nail Attack") &&
                !collision.transform.IsChildOf(hero.transform)) {
                remoteContacts++;
                Log("VINE REMOTE CONTACT " + collision.name + " y=" + collision.transform.position.y + " bounds=" + self.GetComponent<Collider2D>().bounds);
            }
            orig(self, collision);
        };
        On.VinePlatformCut.OnTriggerEnter2D += observe;
        try {
            for (var round = 0; round < 2; round++) {
                target = vines[round]; remoteContacts = 0;
                var attacker = round == 0 ? host : !host;
                var platform = target.GetComponentInParent<VinePlatform>();
                var body = platform.GetComponent<Rigidbody2D>();
                var before = platform.transform.position;
                Check(!Field<bool>(target, "activated") && body.isKinematic, "round " + round + " starts with local suspended platform");
                if (attacker) {
                    anchor.Position = target.GetComponent<Collider2D>().bounds.center + new Vector3(-1.75f, -.25f, 0);
                } else {
                    anchor.Position = platform.GetComponent<Collider2D>().bounds.center + new Vector3(0, 3, 0);
                }
                hero.transform.position = anchor.Position;
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.RegainControl(); hero.StartAnimationControl(); Physics2D.SyncTransforms();
                Flag(role + ".vine.ready." + round);
                yield return Until(() => Has(other + ".vine.ready." + round), "both prepared for native vine strike " + round);
                yield return new WaitForSecondsRealtime(.6f);
                if (attacker) {
                    var end = Time.realtimeSinceStartup + 8;
                    while (!Field<bool>(target, "activated") && Time.realtimeSinceStartup < end) {
                        hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                        yield return new WaitForSecondsRealtime(.5f);
                    }
                    Check(Field<bool>(target, "activated"), "local native nail severs vine " + round);
                    yield return Until(() => Vector3.Distance(platform.transform.position, before) > .5f,
                        "local platform physically falls after local nail " + round);
                    Flag(role + ".vine.cut." + round);
                } else {
                    var remote = players.GetPlayerObject(runtime.Peers.Single().Key);
                    var position = target.GetComponent<Collider2D>().bounds.center + new Vector3(-1.75f, -.25f, 0);
                    yield return Until(() => Vector3.Distance(remote.transform.position, position) < .5f, "remote player converges at vine strike position " + round);
                    // Exercise the receiver boundary with HKMP's real remote slash
                    // prefab, independently of transport/animation timing.
                    var effect = new Hkmp.Animation.Effects.Slash();
                    effect.SetServerSettings(new Hkmp.Game.Settings.ServerSettings()); effect.Play(remote, new bool[6]);
                    var slash = remote.GetComponentsInChildren<Collider2D>().First(c => c.CompareTag("Nail Attack") &&
                        c.gameObject.LocateMyFSM("damages_enemy") != null);
                    Log("VINE RECEIVER FIXTURE layer=" + slash.gameObject.layer + " vineLayer=" + target.gameObject.layer +
                        " ignored=" + Physics2D.GetIgnoreLayerCollision(slash.gameObject.layer, target.gameObject.layer));
                    target.gameObject.SendMessage("OnTriggerEnter2D", slash, SendMessageOptions.RequireReceiver);
                    Check(remoteContacts > 0, "real HKMP remote slash reaches native vine receiver fixture " + round);
                    yield return Until(() => Has(other + ".vine.cut." + round), "teammate actually cuts their own vine " + round);
                    yield return new WaitForSecondsRealtime(.6f);
                    Check(!Field<bool>(target, "activated") && body.isKinematic && Vector3.Distance(platform.transform.position, before) < .05f,
                        "remote nail cannot sever or drop this endpoint's platform " + round);
                }
                Flag(role + ".vine.checked." + round);
                yield return Until(() => Has(other + ".vine.checked." + round), "both checked independently local vine " + round);
            }
            var mapping = SaveDataMapping.Instance;
            var localEntries = mapping.PersistentBoolVarProperties.Where(p => p.Key.Id == "Vine Platform" || p.Key.Id.StartsWith("Vine Platform (")).ToArray();
            Check(localEntries.Length == 19 && localEntries.All(p => !p.Value.Sync), "all 19 native vine persistence entries keep indices but disable outbound sharing");
            var untouched = vines[2].GetComponentInParent<VinePlatform>();
            var key = new PersistentItemKey { Id = untouched.name, SceneName = untouched.gameObject.scene.name };
            Check(mapping.PersistentBoolIndices.TryGetValue(key, out var index), "untouched vine retains original save network index");
            var save = Field<SaveManager>(engine.ClientManager, "_saveManager");
            var state = new PersistentBoolData { id = key.Id, sceneName = key.SceneName };
            var previous = SceneData.instance.FindMyState(state)?.activated;
            typeof(SaveManager).GetMethod("UpdateSaveWithData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(ushort), typeof(byte[]) }, null).Invoke(save, new object[] { index, new byte[] { 1 } });
            Check(!Field<bool>(untouched, "activated") && SceneData.instance.FindMyState(state)?.activated == previous,
                "historical or delayed vine save replay cannot change local platform or local scene state");
        } finally {
            On.VinePlatformCut.OnTriggerEnter2D -= observe;
            Object.Destroy(anchor);
            hero.AffectedByGravity(true);
        }
    }
}

internal sealed class VineProbeAnchor : MonoBehaviour {
    internal Vector3 Position;
    private void FixedUpdate() => Hold();
    private void LateUpdate() => Hold();
    private void Hold() {
        transform.position = Position;
        var body = GetComponent<Rigidbody2D>(); body.velocity = Vector2.zero; body.gravityScale = 0;
    }
}
