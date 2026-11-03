using System;
using System.Collections;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ColosseumProbe {
    private void SaveGoldActors(string state) {
        var actors = Object.FindObjectsOfType<HealthManager>(true).Where(h => h.gameObject.scene.name == ActiveScene)
            .Select(h => new {
                path = WallPath(h.transform), active = h.gameObject.activeInHierarchy, h.hp, h.isDead,
                replica = EntityReferences.IsReplica(h.gameObject), owner = EntityReferences.Owner(h.gameObject)?.Type.ToString(),
                x = h.transform.position.x, y = h.transform.position.y,
                fsms = h.GetComponents<PlayMakerFSM>().Select(f => new { name = f.FsmName, state = f.ActiveStateName }),
                damage = h.GetComponentsInChildren<DamageHero>(true).Select(d => new { d.name, d.enabled, d.damageDealt })
            });
        File.WriteAllText(Path.Combine(root, role + ".actors-" + state.Replace(' ', '-') + ".json"),
            JsonConvert.SerializeObject(actors, Formatting.Indented));
    }

    private IEnumerator ColosseumNetworkChecks() {
        Require(trial == "Gold", "audit uses actual third trial");
        using var presentation=Environment.GetEnvironmentVariable("HOLLOWCONNECT_PRESENTATION_ARENA")=="1"
            ?new PresentationWitness(e=>e.Type==EntityType.ColosseumSpike,Log):null;
        var metrics = GetComponent<ColosseumNetworkMetrics>();
        var hero = HeroController.instance;
        hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        hero.transform.position = new Vector3(108, 20, 0);
        Flag(role + ".gold-start");
        yield return Until(() => Has((host ? "guest" : "host") + ".gold-start"), "both gold audit entrants ready", 60);
        if (ArenaManager.IsSceneHost)
            ArenaEntity.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Battle Control").Fsm.ManualUpdate = false;
        var followup = Environment.GetEnvironmentVariable("HOLLOWCONNECT_GOLD_AUDIT_FOLLOWUP") == "1";
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_NATIVE_NAIL_ARTS") == "1") yield return ColosseumNativeNailArtChecks();
        else if (followup) yield return ColosseumAttackChecks();
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_GOLD_AUDIT_ATTACK_ONLY") == "1") {
            Log("GOLD AUDIT ATTACK-ONLY COMPLETE; native animation playback and real remote colliders, not player input progression");
            yield break;
        }
        metrics.LossEnabled = Environment.GetEnvironmentVariable("HOLLOWCONNECT_GOLD_AUDIT_LOSS") == "1";
        var last = int.Parse(Environment.GetEnvironmentVariable("HOLLOWCONNECT_GOLD_AUDIT_LAST_WAVE") ?? "34");
        var previous = ""; var changed = Time.realtimeSinceStartup; var deadline = changed + 360; var savedMawlek = false;
        HealthManager mawlek = null; var mawlekDeaths = 0; var checkedHealth = false;
        while (true) {
            if(presentation?.Failure!=null)throw new Exception(presentation.Failure);
            var state = ArenaState("Battle Control"); metrics.Phase = state;
            if (state != previous) {
                previous = state; changed = Time.realtimeSinceStartup;
                Log("GOLD AUDIT NATIVE " + state);
                if (state.StartsWith("Wave ") && int.TryParse(state.Substring(5), out var wave)) wallWave = wave;
                SaveGoldActors(state);
            }
            if (Time.realtimeSinceStartup > deadline || Time.realtimeSinceStartup - changed > 45) {
                SaveGoldActors("stalled-" + state); metrics.Sample();
                throw new TimeoutException("Gold audit native wave stalled: " + state);
            }
            if (wallWave >= last && state == "Wave " + last) break;
            ApproachCurrentWallWave();
            if (wallWave == 33 && !savedMawlek && Time.realtimeSinceStartup - changed >= 6) {
                savedMawlek = true; SaveGoldActors("live-Mawlek");
                var live = Object.FindObjectsOfType<HealthManager>().Where(h => EntityReferences.Owner(h.gameObject)?.Type == EntityType.ColosseumMawlek && h.gameObject.activeInHierarchy).ToArray();
                Require(live.Length == 1, "one active Mawlek on each endpoint: " + live.Length);
                var owner = EntityReferences.Owner(live[0].gameObject);
                Require(owner?.Type == EntityType.ColosseumMawlek, "native arena Mawlek has its own registered identity");
                Require(EntityReferences.IsReplica(live[0].gameObject) != ArenaManager.IsSceneHost, "Mawlek authority matches arena authority");
                Require(!BossDefeatSignals.IsBoss(owner.Type), "arena Mawlek cannot publish story boss defeat");
                mawlek = live[0]; mawlek.OnDeath += () => mawlekDeaths++;
                var children = mawlek.GetComponentsInChildren<Transform>(true);
                foreach (var name in new[] { "Mawlek Arm L", "Mawlek Arm R", "Mawlek Head" }) {
                    var child = children.SingleOrDefault(t => t.name.Replace("(Clone)", "") == name);
                    Require(child != null, "native Mawlek child exists: " + name);
                    var childOwner = EntityReferences.Owner(child.gameObject);
                    var expected = name == "Mawlek Head" ? EntityType.ColosseumMawlekHead : EntityType.ColosseumMawlekArm;
                    Require(childOwner?.Type == expected, "Mawlek child has dedicated identity: " + name + "=" + childOwner?.Type);
                    Require(EntityReferences.IsReplica(child.gameObject) != ArenaManager.IsSceneHost, "Mawlek child authority follows arena: " + name);
                }
                if (ArenaManager.IsSceneHost) {
                    var hp = mawlek.hp;
                    mawlek.ApplyExtraDamage(7);
                    Require(mawlek.hp == hp - 7, "native Mawlek nonlethal damage applied once");
                    File.WriteAllText(Path.Combine(root, "mawlek.expected-hp"), mawlek.hp.ToString());
                }
                Flag(role + ".mawlek-verified");
                var cageFsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.name.StartsWith("Colosseum Cage"))
                    .SelectMany(f => f.FsmStates.SelectMany(s => s.Actions.Select(a => new { f, s, a })))
                    .Where(p => p.a is HutongGames.PlayMaker.Actions.CreateObject create && create.gameObject.Value != null && create.gameObject.Value.name.Contains("Mawlek"))
                    .Select(p => new { path = WallPath(p.f.transform), fsm = p.f.FsmName, state = p.s.Name,
                        p.a.Enabled, fields = p.a.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .ToDictionary(f => f.Name, f => Describe(f.GetValue(p.a))) });
                File.WriteAllText(Path.Combine(root, role + ".mawlek-cage.json"), JsonConvert.SerializeObject(cageFsms, Formatting.Indented));
            }
            if (wallWave == 33 && savedMawlek && !checkedHealth && Time.realtimeSinceStartup - changed >= 8) {
                Require(mawlek != null && mawlek.hp == int.Parse(File.ReadAllText(Path.Combine(root, "mawlek.expected-hp"))), "Mawlek absolute HP converges on both endpoints");
                checkedHealth = true; Flag(role + ".mawlek-health");
            }
            var dwell = wallWave == 33 ? 12 : 1.5f;
            if (Time.realtimeSinceStartup - changed >= dwell) {
                if (wallWave == 33 && !(Has("host.mawlek-health") && Has("guest.mawlek-health"))) {
                    yield return null; continue;
                }
                DamageCurrentWallWave();
            }
            yield return new WaitForSecondsRealtime(.1f);
        }
        yield return new WaitForSecondsRealtime(4);
        Require(Has("host.mawlek-verified") && Has("guest.mawlek-verified"), "both native Mawlek observations passed before its death");
        Require(mawlekDeaths == 1, "Mawlek native death occurs once per endpoint: " + mawlekDeaths);
        Require(engine.NetClient.UpdateManager.PendingSentCount < 64, "lost carriers do not accumulate across the trial");
        if(presentation!=null&&!ArenaManager.IsSceneHost) {
            Require(presentation.Failure==null&&presentation.Snapshots>=20,"native spike display matches complete wire snapshots: "+presentation.Snapshots);
            Require(presentation.Clips.Count>=2&&presentation.RootStops>0,"native spike extension/retraction and terminal frames observed: "+string.Join(",",presentation.Clips));
        }
        SaveGoldActors("final-" + last); metrics.Sample();
        Flag(role + ".gold-complete");
        yield return Until(() => Has((host ? "guest" : "host") + ".gold-complete"), "both native gold wave observations completed", 60);
        Log("GOLD AUDIT COMPLETE native waves 1-" + last + "; HealthManager damage accelerates progression; not a full combat clear");
    }
}
