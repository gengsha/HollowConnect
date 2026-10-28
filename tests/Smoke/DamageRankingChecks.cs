using System;
using System.Collections;
using System.IO;
using System.Linq;
using Hkmp.Animation;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class PantheonProbe {
    private bool DamageRankingCase => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_DAMAGE_RANKING"));
    private bool DamageRankingOn => Environment.GetEnvironmentVariable("HOLLOWCONNECT_DAMAGE_RANKING") == "on";
    private void DamageCheck(bool condition, string label) {
        if (!condition) throw new Exception("Damage ranking: " + label);
        Mark("PASS " + label);
    }
    private IEnumerator DamageRankingChecks() {
        DamageCheck(runtime.damageRankingEnabled == DamageRankingOn, "host rule overrides inverse guest preference");
        // Use a real full first Pantheon; every boss transition and final room
        // remains native. Only attack input and accepted HP damage are driven.
        var tier = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DAMAGE_FINAL_ONLY") == "1"
            ? Environment.GetEnvironmentVariable("HOLLOWCONNECT_DAMAGE_FINAL_TIER") ?? "1" : "1";
        var sequence = Resources.LoadAll<BossSequence>("GG").Single(s => s.name == "Boss Sequence Tier " + tier);
        BossSequenceController.Reset();
        BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier1");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DAMAGE_FINAL_ONLY") == "1") {
            yield return DamageFinalOnly(sequence); yield break;
        }
        if (!host) yield return Until(() => Has("damage.host.entered"), "host enters boss first");
        Enter(sequence.GetSceneAt(0)); yield return Until(() => In(sequence.GetSceneAt(0)), "damage first native boss"); SafeHero();
        if (host) { yield return Until(() => manager.IsSceneHostDetermined, "damage boss authority"); Flag("damage.host.entered"); }
        yield return Both("damage.first");
        yield return Until(() => engine.ClientManager.SceneSnapshotReady && runtime.Peers.Count == 1 &&
            runtime.PlayerObject(runtime.Peers.Keys.Single()) != null, "damage peer and native snapshot ready");
        yield return new WaitForSecondsRealtime(1);
        var boss = Entities.Values.First(e => e.Type == EntityType.VengeflyKing);
        var health = boss.Object.Host.GetComponent<HealthManager>();
        if (!DamageRankingOn) {
            if (manager.IsSceneHost) health.Hit(new HitInstance { Source = HeroController.instance.gameObject,
                AttackType = AttackTypes.Nail, DamageDealt = 10, Multiplier = 1, IgnoreInvulnerable = true });
            yield return new WaitForSecondsRealtime(1);
            DamageCheck(runtime.damageRanking == null && Object.FindObjectsOfType<Canvas>().All(c => c.name != "HollowConnect Damage Ranking"), "disabled room creates no board or overlay");
            Mark("FINAL PASS host disabled damage ranking"); yield return Finish(); yield break;
        }
        yield return Until(() => runtime.damageRanking != null && runtime.damageRanking.Players.Length == 2, "both participants on zero board");
        yield return Both("damage.zero");
        if (manager.IsSceneHost) {
            var before = health.hp;
            health.Hit(new HitInstance { Source = HeroController.instance.gameObject, AttackType = AttackTypes.Nail,
                DamageDealt = 21, Multiplier = 1, IgnoreInvulnerable = true });
            DamageCheck(health.hp == before - 21, "accepted local nail health debit");
            yield return new WaitForSecondsRealtime(.3f);
            var remote = runtime.PlayerObject(runtime.Peers.Keys.Single());
            GameObject detached;
            using (AttackOwnership.Creating(runtime.Peers.Keys.Single())) detached = RemotePlayerVisuals.Instantiate(new GameObject("Remote detached spell ownership fixture"));
            detached.layer = 17;
            health.Hit(new HitInstance { Source = detached, AttackType = AttackTypes.Spell,
                DamageDealt = 30, Multiplier = 1, IgnoreInvulnerable = true });
            Object.Destroy(detached); Object.Destroy(GameObject.Find("Remote detached spell ownership fixture"));
            Flag("damage.hits.sent");
        } else yield return Until(() => Has("damage.hits.sent"), "authority attacks delivered");
        yield return Until(() => runtime.damageRanking.Total == 51, "native damage attribution snapshots converge");
        DamageCheck(runtime.damageRanking.Players.Single(p => p.Name == "pantheonhost").Damage == 21 &&
            runtime.damageRanking.Players.Single(p => p.Name == "pantheonguest").Damage == 30, "detached remote spell belongs to caster");
        runtime.ToggleDamageRanking(); yield return null;
        DamageCheck(runtime.damageRankingHidden, "local toggle hides overlay");
        yield return Both("damage.hidden");
        if (manager.IsSceneHost) {
            yield return new WaitForSecondsRealtime(.3f);
            health.Hit(new HitInstance { Source = HeroController.instance.gameObject, AttackType = AttackTypes.Nail,
                DamageDealt = 10, Multiplier = 1, IgnoreInvulnerable = true });
        }
        yield return Until(() => runtime.damageRanking.Total == 61, "hidden overlay keeps counting");
        runtime.ToggleDamageRanking(); yield return null;
        var canvas = Object.FindObjectsOfType<Canvas>().Single(c => c.name == "HollowConnect Damage Ranking");
        DamageCheck(!canvas.GetComponent<CanvasGroup>().blocksRaycasts && canvas.GetComponentsInChildren<Text>().All(t => !t.raycastTarget), "overlay never captures battle input");
        Mark("NATIVE ORNAMENT " + string.Join(",", canvas.GetComponentsInChildren<Image>().Where(i => i.sprite != null).Select(i => i.sprite.name)));
        Mark("NATIVE FONTS " + string.Join(",", Resources.FindObjectsOfTypeAll<Font>().Select(f => f.name)));
        yield return Both("damage.checked");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DAMAGE_RECONNECT") == "1") {
            var originalBoard = runtime.damageRanking.Board;
            if (!host) {
                var oldId = runtime.LocalId;
                engine.ClientManager.Disconnect(); yield return Until(() => !runtime.Connected, "ranking guest disconnects");
                yield return new WaitForSecondsRealtime(1);
                ConnectLocal("pantheonguest"); yield return Until(() => runtime.Connected, "ranking guest rejoins with same name");
                Mark("DAMAGE RECONNECT id " + oldId + " -> " + runtime.LocalId);
            }
            yield return Until(() => runtime.damageRanking?.Board == originalBoard && runtime.damageRanking.Players.Length == 2 &&
                runtime.damageRanking.Players.Single(p => p.Name == "pantheonguest").Damage == 30 && runtime.Peers.Count == 1,
                "rejoined ranking merges old damage into one row", 20);
            yield return Both("damage.reconnected");
            if (host) {
                yield return Until(() => runtime.PlayerObject(runtime.Peers.Keys.Single()) != null, "rejoined damage source ready");
                health.Hit(new HitInstance { Source = runtime.PlayerObject(runtime.Peers.Keys.Single()), AttackType = AttackTypes.Nail,
                    DamageDealt = 20, Multiplier = 1, IgnoreInvulnerable = true });
            }
            yield return Until(() => runtime.damageRanking.Total == 81, "new damage accumulates on merged standing", 15);
            DamageCheck(runtime.damageRanking.Players.Length == 2 && runtime.damageRanking.Players.Single(p => p.Name == "pantheonguest").Damage == 50,
                "same-name reconnect preserves total and one standing");
            Mark("FINAL PASS damage ranking reconnect merged"); yield return Finish(); yield break;
        }
        var board = runtime.damageRanking.Board;
        var roomLimit = int.TryParse(Environment.GetEnvironmentVariable("HOLLOWCONNECT_DAMAGE_CHECK_ROOMS"), out var requestedRooms)
            ? Math.Max(2, Math.Min(sequence.Count, requestedRooms)) : sequence.Count;
        for (var index = 0; index < sequence.Count; index++) {
            var scene = sequence.GetSceneAt(index);
            if (!sequence.CanLoad(index)) continue;
            yield return Until(() => In(scene), "damage native run room " + index, 100); SafeHero();
            yield return Both("damage.room." + index);
            if (scene == "GG_Spa") {
                var exit = BossSceneController.Instance?.customExitPoint;
                DamageCheck(exit != null && exit.targetScene == sequence.GetSceneAt(index + 1) && !string.IsNullOrEmpty(exit.entryPoint),
                    "native configured rest room exit available");
                GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = exit.targetScene,
                    EntryGateName = exit.entryPoint, Visualization = GameManager.SceneLoadVisualizations.Default,
                    WaitForSceneTransitionCameraFade = true });
                continue;
            }
            DamageCheck(runtime.damageRanking.Board == board && runtime.damageRanking.Total >= 61 && !runtime.damageRanking.Finished, "same accumulated board before boss " + index);
            if (index + 1 == roomLimit && roomLimit < sequence.Count) {
                Mark("FINAL PASS native Pantheon damage attribution, visibility and cumulative room prefix (" + roomLimit + " rooms)");
                yield return Finish(); yield break;
            }
            if (manager.IsSceneHost) yield return Defeat(scene, index + 1 < sequence.Count ? sequence.GetSceneAt(index + 1) : "GG_End_Sequence");
            else yield return Until(() => !In(scene), "guest follows native defeat " + index, 100);
        }
        yield return Until(() => runtime.damageRanking.Finished, "native final boss settles whole Pantheon", 15);
        DamageCheck(runtime.damageRanking.Completed && runtime.damageRanking.Board == board && runtime.damageRanking.Total == 61,
            "whole Pantheon final result excludes scripted kill damage");
        Mark("FINAL PASS native Pantheon damage ranking and local visibility"); yield return Finish();
    }
    private IEnumerator DamageFinalOnly(BossSequence sequence) {
        // A focused saved-context entry, like the P5 scene probes: keep the
        // original full sequence and native final-room completion callbacks.
        typeof(BossSequenceController).GetField("bossIndex", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .SetValue(null, sequence.Count - 1);
        var scene = sequence.GetSceneAt(sequence.Count - 1);
        if (!host) yield return Until(() => Has("final.host.entered"), "final authority enters first");
        Enter(scene); yield return Until(() => In(scene), "native final arena"); SafeHero();
        if (host) { yield return Until(() => manager.IsSceneHostDetermined, "final authority ready"); Flag("final.host.entered"); }
        yield return Both("final.entered");
        yield return Until(() => engine.ClientManager.SceneSnapshotReady && runtime.damageRanking?.Players.Length == 2, "final board and native snapshot ready");
        yield return Both("final.ready");
        var board = runtime.damageRanking.Board;
        var deadline = Time.realtimeSinceStartup + 100;
        var sample = 0f;
        while (In(scene) && runtime.damageRanking?.Finished != true && Time.realtimeSinceStartup < deadline) {
            if (Time.realtimeSinceStartup >= sample) {
                sample = Time.realtimeSinceStartup + 2;
                foreach (var entity in Entities.Values.Where(e => BossDefeatSignals.IsBoss(e.Type) && e.Object.Host != null))
                    Mark("FINAL NATIVE " + entity.Type + " active=" + entity.Object.Host.activeInHierarchy +
                        " hp=" + entity.Object.Host.GetComponent<HealthManager>()?.hp + " dead=" + entity.Object.Host.GetComponent<HealthManager>()?.isDead +
                        " states=" + string.Join(",", entity.Object.Host.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + "/" + f.ActiveStateName)));
            }
            if (manager.IsSceneHost) foreach (var entity in Entities.Values.Where(e => BossDefeatSignals.IsBoss(e.Type)).ToArray()) {
                var obj = entity.Object.Host;
                if (obj == null) continue;
                var health = obj.GetComponent<HealthManager>();
                if (health == null || health.hp <= 0 || health.isDead || !health.gameObject.activeInHierarchy) continue;
                health.Hit(new HitInstance { Source = HeroController.instance.gameObject, AttackType = AttackTypes.Nail,
                    DamageDealt = 120, Multiplier = 1, IgnoreInvulnerable = false });
            }
            yield return new WaitForSecondsRealtime(.25f);
        }
        yield return Until(() => runtime.damageRanking?.Finished == true, "native final boss damage settlement", 3);
        DamageCheck(runtime.damageRanking.Completed && runtime.damageRanking.Board == board && runtime.damageRanking.Total > 0,
            "native full-sequence final callback settles accepted damage");
        Mark("FINAL PASS native Pantheon final damage settlement"); yield return Finish();
    }
}
