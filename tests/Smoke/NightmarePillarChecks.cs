using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Networking.Packet.Data;
using HollowConnect;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;
using Targeting = Hkmp.Game.Client.Entity.BossTargeting;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private sealed class PillarSample { public int Number, Player; public float X; }
    private IEnumerator NightmarePillarChecks() {
        var authority = role == Environment.GetEnvironmentVariable("HOLLOWCONNECT_FLEX_TARGET_AUTHORITY");
        var other = host ? "guest" : "host";
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        BossSequenceController.Reset(); BossSceneController.SetupEvent = self => self.BossLevel = 0;
        if (!authority) yield return Until(() => Has("nkg.first"), "NKG first native entrant");
        HeroController.instance.takeNoDamage = true;
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = "GG_Grimm_Nightmare", EntryGateName = "door_dreamEnter",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => runtime.Playable && CoopContext.LocalScene == "GG_Grimm_Nightmare" && engine.ClientManager.SceneSnapshotReady &&
            manager.IsSceneHostDetermined && manager.IsSceneHost == authority && entities.Values.Any(e => e.Type == EntityType.NightmareKingGrimm), "NKG native authority and root");
        var boss = entities.Values.Single(e => e.Type == EntityType.NightmareKingGrimm);
        var hero = HeroController.instance; hero.takeNoDamage = true;
        using var noContact = IgnoreExternalCombatDamage();
        hero.AffectedByGravity(false); var body = hero.GetComponent<Rigidbody2D>(); body.velocity = Vector2.zero;
        hero.transform.position = new Vector3(authority ? 76 : 90, 8, 0);
        if (authority) Flag("nkg.first");
        Flag(role + ".nkg-ready"); yield return Until(() => Has(other + ".nkg-ready") && runtime.Peers.Count == 1, "NKG two living entrants");
        Check(boss.BossTargetControl.Name == "Control" && boss.BossTargetControl.States.Any(s => s.Name == "Move Choice") &&
            boss.BossTargetControl.States.Any(s => s.Name == "Pillar") && boss.BossTargetControl.States.Any(s => s.Name == "Repeat?"), "preflight native NKG decision and four-pillar repeat states");
        var native = new List<PillarSample>(); var replica = new List<float>();
        var cycles = 0; var remoteCycles = 0; var movedCycles = 0; var expiredCycles = 0;
        var badPositions = 0; var splitTargets = 0; var inherited = 0; var badInheritance = 0;
        var cyclePlayer = -1; var firstX = 0f;
        using var nativeSpawn = new Hook(typeof(SpawnObjectFromGlobalPool).GetMethod("OnEnter"),
            new Action<Action<SpawnObjectFromGlobalPool>, SpawnObjectFromGlobalPool>((orig, action) => {
                orig(action);
                if (!authority || action.Fsm != boss.BossTargetControl || action.State.Name != "Pillar") return;
                var obj = action.storeObject.Value;
                var choice = BossTargetCommitment.Decode(boss.BossTargetReceipt.Value, runtime.Session, CoopContext.Epoch);
                var target = CoopContext.BossTargetObject(choice.Player);
                if (obj == null || target == null) { badPositions++; return; }
                var index = native.Count % 4;
                if (index == 0) { cyclePlayer = choice.Player; firstX = obj.transform.position.x; }
                if (choice.Player != cyclePlayer) splitTargets++;
                if (Math.Abs(obj.transform.position.x - target.transform.position.x - action.position.Value.x) > .05f) badPositions++;
                var sample = new PillarSample { Number = native.Count + 1, Player = choice.Player, X = obj.transform.position.x };
                native.Add(sample);
                File.AppendAllText(Path.Combine(root, "nkg-native.jsonl"), JsonConvert.SerializeObject(sample) + "\n");
                File.WriteAllText(Path.Combine(root, "nkg.move"), choice.Player + "|" + (index + 1));
                Log("NKG PILLAR " + sample.Number + " player=" + choice.Player + " actualX=" + sample.X + " targetX=" + target.transform.position.x +
                    " holdExpired=" + (CoopRuntime.Now >= choice.Until));
                if (index == 3) {
                    cycles++; if (cyclePlayer != runtime.LocalId) remoteCycles++;
                    if (Math.Abs(sample.X - firstX) > 1) movedCycles++;
                    if (CoopRuntime.Now >= choice.Until) expiredCycles++;
                }
            }));
        var replayMethod = typeof(EntityFsmActions).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "ApplyNetworkDataFromAction" && m.GetParameters()[1].ParameterType == typeof(SpawnObjectFromGlobalPool));
        using var replay = new Hook(replayMethod, new Action<Action<EntityNetworkData, SpawnObjectFromGlobalPool>, EntityNetworkData, SpawnObjectFromGlobalPool>((orig, data, action) => {
            orig(data, action);
            if (authority || action.State.Name != "Pillar" || EntityReferences.Owner(action.Fsm.GameObject) != boss) return;
            var obj = action.storeObject.Value;
            if (obj != null) { replica.Add(obj.transform.position.x); Log("NKG REPLICA PILLAR " + replica.Count + " actualX=" + obj.transform.position.x); }
        }));
        using var generation = new Hook(typeof(Targeting).GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Static),
            new Action<Action<Entity, PlayMakerFSM[], PlayMakerFSM[], Entity>, Entity, PlayMakerFSM[], PlayMakerFSM[], Entity>((orig, e, h, c, source) => {
                orig(e, h, c, source);
                if (!authority || source != boss || !e.InheritedBossTarget || e.BossTargetReceipt == null) return;
                inherited++; if (e.BossTargetReceipt.Value != source.BossTargetReceipt.Value) badInheritance++;
            }));
        var end = Time.realtimeSinceStartup + 150;
        while (Time.realtimeSinceStartup < end) {
            var x = authority ? 76f : 90f;
            if (Has("nkg.move")) {
                try {
                    var command = File.ReadAllText(Path.Combine(root, "nkg.move")).Split('|');
                    if (command.Length == 2 && int.TryParse(command[0], out var player) && int.TryParse(command[1], out var step))
                        x = runtime.LocalId == player ? x + step * 1.2f : Mathf.Clamp((manager.IsSceneHost ? boss.Object.Host : boss.Object.Client).transform.position.x, 72, 98);
                } catch (IOException) { }
            }
            hero.transform.position = new Vector3(x, 8, 0); body.velocity = Vector2.zero; body.gravityScale = 0;
            if (authority && cycles >= 3 && remoteCycles > 0 && movedCycles > 0 && expiredCycles > 0 && inherited > 0) { Flag("nkg.native-complete"); break; }
            if (!authority && Has("nkg.native-complete")) break;
            yield return null;
        }
        if (authority) {
            Check(cycles >= 3 && remoteCycles > 0, "three complete natural four-pillar attacks include targeting living remote teammate");
            Check(movedCycles > 0 && badPositions == 0, "each pillar follows committed player's current moving position");
            Check(expiredCycles > 0 && splitTargets == 0, "four-pillar attack never switches players after hold expires or rival moves closer");
            Check(inherited > 0 && badInheritance == 0, "natural generated NKG attacks inherit their source commitment");
            Check(boss.BossTargetControl.States.Single(s => s.Name == "Pillar").Actions.OfType<SpawnObjectFromGlobalPool>().Single().spawnPoint.Value == hero.gameObject,
                "native spawnPoint remains Knight after temporary target substitution");
        } else {
            yield return Until(() => Has("nkg.native-complete") && File.Exists(Path.Combine(root, "nkg-native.jsonl")) &&
                replica.Count >= File.ReadAllLines(Path.Combine(root, "nkg-native.jsonl")).Length, "all authoritative pillar spawns arrive on replica");
            var samples = File.ReadAllLines(Path.Combine(root, "nkg-native.jsonl")).Select(JsonConvert.DeserializeObject<PillarSample>).ToArray();
            Check(replica.Count == samples.Length && samples.Length >= 12, "replica creates exactly the same four-pillar sequences without duplicates");
            Check(samples.Select((s, i) => Math.Abs(s.X - replica[i]) < .05f).All(v => v), "replica pillar positions match authority when target is local or remote");
        }
        Flag(role + ".nkg-observed"); yield return Until(() => Has(other + ".nkg-observed"), "both NKG pillar checks complete");
    }
}
