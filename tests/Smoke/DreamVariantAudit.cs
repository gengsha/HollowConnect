using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Scenes = UnityEngine.SceneManagement.SceneManager;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Observation only. Direct scene loads do not certify native dream-nail entry,
// all boss phases, victory returns or personal reward collection.
public sealed partial class ProgressProbe {
    private void TraceDreamLoad(string phase) {
        var hero = HeroController.instance;
        var scenes = Enumerable.Range(0, Scenes.sceneCount).Select(i => Scenes.GetSceneAt(i)).ToArray();
        Mark("DREAM LOAD " + phase + " gm=" + GameManager.instance?.sceneName + " active=" + Scenes.GetActiveScene().name +
            " network=" + Hkmp.CoopContext.LocalScene + " visit=" + Hkmp.CoopContext.LocalVisit +
            " transition=" + GameManager.instance?.IsInSceneTransition + " positioned=" + hero?.isHeroInPosition +
            " input=" + hero?.acceptingInput + " relinquished=" + hero?.controlReqlinquished +
            " heroTransition=" + hero?.transitionState + " changing=" + hero?.cState.transitioning +
            " scenes=" + string.Join(",", scenes.Select(s => s.name + "/loaded=" + s.isLoaded)));
    }

    private void ObserveDreamLoads() {
        Scenes.sceneLoaded += (scene, mode) => TraceDreamLoad("loaded " + scene.name + " mode=" + mode);
        Scenes.sceneUnloaded += scene => TraceDreamLoad("unloaded " + scene.name);
        Scenes.activeSceneChanged += (before, after) => TraceDreamLoad("active " + before.name + " -> " + after.name);
        On.GameManager.BeginSceneTransition += (orig, gm, info) => {
            TraceDreamLoad("request " + info.SceneName + "/" + info.EntryGateName);
            orig(gm, info);
        };
    }

    private IEnumerator DreamVariantSchema() {
        var scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_VARIANT_SCENE");
        var variants = new[] { "Dream_01_False_Knight", "Dream_02_Mage_Lord", "Dream_03_Infected_Knight", "Dream_04_White_Defender", "Dream_Mighty_Zote" };
        if (!variants.Contains(scene)) throw new Exception("Unknown story dream variant scene");
        var pd = PlayerData.instance;
        var defeated = new[] { "falseKnightDreamDefeated", "mageLordDreamDefeated", "infectedKnightDreamDefeated", "whiteDefenderDefeated", "greyPrinceDefeated" };
        var collected = new[] { "falseKnightOrbsCollected", "mageLordOrbsCollected", "infectedKnightOrbsCollected", "whiteDefenderOrbsCollected", "greyPrinceOrbsCollected" };
        foreach (var key in defeated.Concat(collected)) pd.SetBool(key, !host);
        pd.SetInt("whiteDefenderDefeats", host ? 0 : 2); pd.SetInt("greyPrinceDefeats", host ? 0 : 2);
        pd.SetInt("dreamOrbs", host ? 0 : 230);
        pd.SetString("dreamReturnScene", "Town");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SETTLEMENT") != null)
            PrepareDreamSettlement(scene);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_ESSENCE_SETTLEMENT") == "1") {
            foreach (var key in collected) pd.SetBool(key, false);
            // Authoritative scene load is a fixture; actual battle, lethal damage,
            // corpse outro, dream return and essence use their native flow.
            HollowConnect.CoopMod.Statistics.Resources.Claimed.Remove("essence:" + DreamCollectedKey(scene));
            if (!host) {
                // A real remote observer stays in Town while the host runs
                // native combat/outro/return, proving cross-room source grants.
                var before = pd.GetInt("dreamOrbs");
                On.HutongGames.PlayMaker.Fsm.EnterState += SourceNotice;
                try {
                    Flag("guest.variant-observed"); Flag("guest.settlement-battle");
                    yield return Until(() => LocalResourceClaims.Contains("essence:" + DreamCollectedKey(scene)) && pd.GetInt("dreamOrbs") == before + 300,
                        "remote native dream corpse grants exactly one essence source", 180);
                    yield return Until(() => sourceNotices.Contains("共享梦境精华 ×300"), "remote dream corpse lower-left essence notice", 12);
                    ResourceCheck(!pd.GetBool(DreamCollectedKey(scene)) && GameManager.instance.sceneName == "Town",
                        "shared dream corpse essence preserves observer native claim and room");
                    Flag("guest.settlement-claimed");
                    yield return Until(() => Has("host.settlement-claimed"), "host native dream settlement complete", 45);
                    yield return new WaitForSecondsRealtime(3);
                    ResourceCheck(pd.GetInt("dreamOrbs") == before + 300 && sourceNotices.Count == 1,
                        "duplicate dream corpse source cannot replay essence or notice");
                } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= SourceNotice; }
                yield break;
            }
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOST_KIN_LIFECYCLE") != null) {
            var claimedKin = !host && Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOST_KIN_LIFECYCLE") == "claimed";
            pd.SetBool("infectedKnightDreamDefeated", claimedKin);
            pd.SetBool("infectedKnightOrbsCollected", claimedKin);
            pd.SetBool("killedInfectedKnight", true);
            pd.SetBool("infectedKnightDefeated", true);
            pd.SetString("dreamReturnScene", "Abyss_19");
            pd.SetString("dreamReturnGate", "door_dreamReturn");
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_CHAMPION_LIFECYCLE") == "1") {
            // A legitimate story rematch requires the original False Knight
            // to be defeated; otherwise the return room loads the live arena.
            pd.SetBool("killedFalseKnight", true);
            pd.SetBool("falseKnightDefeated", true);
            pd.SetString("dreamReturnScene", "Crossroads_10");
            pd.SetString("dreamReturnGate", "door_dreamReturn");
        }
        BossSequenceController.Reset(); pd.SetInt("bossStatueTargetLevel", 0);
        if (!host) yield return Until(() => Has("host.variant-entered"), "host enters schema scene first");
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
            SceneName = scene, EntryGateName = scene == "Dream_04_White_Defender" ? "dreamGate" : "door1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
        });
        yield return Until(() => Scenes.GetSceneByName(scene).isLoaded && Hkmp.CoopContext.LocalScene == scene && engine.ClientManager.SceneSnapshotReady,
            "dream schema asset and room snapshot committed", 35);
        Flag(role + ".variant-entered");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_ESSENCE_SETTLEMENT") != "1")
            yield return Until(() => Has((host ? "guest" : "host") + ".variant-entered"), "both loaded dream schema scene");
        yield return new WaitForSecondsRealtime(4);
        TraceDreamLoad("schema observation");
        Mark("DREAM VARIANT " + scene + " PD=" + string.Join(",", defeated.Concat(collected).Select(k => k + "=" + pd.GetBool(k))) +
            " whiteCount=" + pd.GetInt("whiteDefenderDefeats") + " greyCount=" + pd.GetInt("greyPrinceDefeats") + " orbs=" + pd.GetInt("dreamOrbs"));
        var local = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene ||
            f.gameObject.scene.name.StartsWith(scene + "_", StringComparison.Ordinal)).ToArray();
        Mark("DREAM VARIANT ghost-npc-count=" + local.Count(f => f.name == "Ghost Warrior NPC" && f.FsmName == "Conversation Control"));
        foreach (var f in local.Where(f => new[] { "Boss", "Mage", "False", "Knight", "Grey", "White", "Defender", "Dream", "Ghost", "Essence", "Return" }
            .Any(word => f.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) ||
            f.FsmStates.SelectMany(s => s.Actions).Any(a => a is SetPlayerDataBool b && defeated.Concat(collected).Contains(b.boolName.Value)))) {
            Mark("DREAM VARIANT FSM " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " enabled=" + f.enabled + " state=" + f.ActiveStateName);
            Dump(f);
        }
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        foreach (var e in Field<System.Collections.Generic.Dictionary<ulong, Entity>>(manager, "_entities").Values.Where(e => BossDefeatSignals.IsBoss(e.Type))) {
            var go = manager.IsSceneHost ? e.Object.Host : e.Object.Client;
            Mark("DREAM VARIANT ENTITY " + e.Type + " authority=" + manager.IsSceneHost + " active=" + (go != null && go.activeInHierarchy) +
                " hp=" + go?.GetComponent<HealthManager>()?.hp);
        }
        Flag(role + ".variant-observed");
        yield return Until(() => Has((host ? "guest" : "host") + ".variant-observed"), "both observed dream schema only");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_VARIANT_DAMAGE") == "1")
            yield return DreamVariantDamage(scene);
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_CHAMPION_LIFECYCLE") == "1")
            yield return ChampionLifecycle();
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOST_KIN_LIFECYCLE") != null)
            yield return LostKinLifecycle();
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SETTLEMENT") != null)
            yield return DreamSettlement(scene);
    }

    // A small, non-lethal native Hit falsifies registration/health sharing.
    // This does not certify a native entry, armor phases, death or return.
    private IEnumerator DreamVariantDamage(string scene) {
        if (scene != "Dream_01_False_Knight") throw new Exception("Damage audit is scoped to the Failed Champion registration finding");
        var native = Object.FindObjectsOfType<HealthManager>(true).Single(h => h.gameObject.scene.name == scene && h.name == "False Knight Dream");
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<System.Collections.Generic.Dictionary<ulong, Entity>>(manager, "_entities");
        var matched = EntityRegistry.TryGetEntry(native.gameObject, out var entry);
        var registered = entities.Values.Where(e => e.Object.Host == native.gameObject || e.Object.Client == native.gameObject).ToArray();
        // A registered replica has a separate visible object; the dormant
        // native source is intentionally hidden and does not take updates.
        var shown = registered.Length == 0 ? native.gameObject : manager.IsSceneHost ? registered[0].Object.Host : registered[0].Object.Client;
        var boss = shown.GetComponent<HealthManager>();
        var control = boss.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "FalseyControl");
        // Dormant native bosses are invincible. Enter the real Battle Scene
        // trigger before using damage as evidence; never overwrite HP or FSM.
        var trigger = Object.FindObjectsOfType<Collider2D>(true).First(c => c.gameObject.scene.name == scene &&
            c.name == "Battle Scene" && c.isTrigger && c.enabled && c.gameObject.activeInHierarchy);
        Mark("DREAM DAMAGE native trigger bounds=" + trigger.bounds + " bossPosition=" + boss.transform.position);
        HeroController.instance.transform.position = trigger.bounds.center;
        HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        if (manager.IsSceneHost) yield return Until(() => control.ActiveStateName != "Dormant" && control.ActiveStateName != "Wait for Hero" &&
            !boss.IsInvincible, "native Failed Champion battle trigger releases invincibility", 20);
        else yield return Until(() => boss.gameObject.activeInHierarchy && !boss.IsInvincible,
            "Failed Champion replica follows native battle activation", 20);
        Mark("DREAM DAMAGE before name=" + boss.name + " hp=" + boss.hp + " registry=" + matched + " type=" + entry?.Type +
            " registered=" + string.Join(",", registered.Select(e => e.Type.ToString())) + " enabled=" + control.enabled + " state=" + control.ActiveStateName);
        var before = boss.hp;
        if (before <= 17 || !boss.gameObject.activeInHierarchy) throw new Exception("Damage audit requires a live native boss");
        Flag(role + ".variant-health-ready");
        yield return Until(() => Has((host ? "guest" : "host") + ".variant-health-ready"), "both sampled native Failed Champion before damage");
        if (host) {
            boss.Hit(new HitInstance { Source = HeroController.instance.gameObject, AttackType = AttackTypes.Nail,
                DamageDealt = 17, Multiplier = 1, IgnoreInvulnerable = true, Direction = 0, MagnitudeMultiplier = 1 });
            if (boss.hp >= before) throw new Exception("Invalid damage probe: native Hit did not decrease authority HP");
            Flag("host.variant-hit");
        } else yield return Until(() => Has("host.variant-hit"), "authority applied a non-lethal native Hit");
        yield return new WaitForSecondsRealtime(3);
        Mark("DREAM DAMAGE after hp=" + boss.hp + " enabled=" + control.enabled + " state=" + control.ActiveStateName);
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, role + ".variant-hp"), boss.hp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Flag(role + ".variant-health-sampled");
        yield return Until(() => Has((host ? "guest" : "host") + ".variant-health-sampled"), "both sampled post-hit native HP");
        var authorityHp = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "host.variant-hp"));
        var participantHp = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "guest.variant-hp"));
        if (!matched || registered.Length == 0 || authorityHp != participantHp)
            throw new Exception("Failed Champion registration/health divergence: registry=" + matched + ", registered=" + registered.Length +
                ", hostHP=" + authorityHp + ", guestHP=" + participantHp);
        Mark("PASS Failed Champion registered with converged native HP after an authority Hit");
    }
}
