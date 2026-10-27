using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator BossVictoryChecks() {
        var settlementSeen = false;
        var victoryDelayed = false;
        using var fixtureDamage = IgnoreExternalCombatDamage();
        On.GameManager.BeginSceneTransition += (orig, gm, info) => {
            Log("VICTORY NATIVE TRANSITION " + gm.sceneName + " -> " + info.SceneName + "/" + info.EntryGateName);
            orig(gm, info);
        };
        using var lifeTrace = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, owner, message) => {
                if (message.Kind == Kind.Down || message.Kind == Kind.Returning || message.Kind == Kind.BossVictory || message.Kind == Kind.BossRevived)
                    Log("TRACE victory life " + message.Kind + " id=" + message.Id + " reason=" + message.Name + " scene=" + message.Scene + " serial=" + message.Serial);
                if (!victoryDelayed && message.Kind == Kind.BossVictory &&
                    (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_VICTORY_CASE") ?? "").StartsWith("p5-", StringComparison.Ordinal)) {
                    victoryDelayed = true;
                    StartCoroutine(DelayedVictory());
                    return;
                    IEnumerator DelayedVictory() {
                        Log("FIXTURE hold actual reliable victory notice for 5 seconds on both participants");
                        yield return new WaitForSecondsRealtime(5);
                        Check(Hkmp.CoopContext.LocalScene == message.Scene && !owner.Returning &&
                            owner.Peers.Values.Single().State.Scene == message.Scene, "living teammate waits in original P5 arena during delayed recovery");
                        orig(owner, message);
                    }
                }
                orig(owner, message);
                if (message.Kind == Kind.BossRevived && message.Id == owner.LocalId && !owner.Down) {
                    settlementSeen = true;
                    Check(!owner.Spectating && !owner.Returning && Hkmp.CoopContext.LocalScene == message.Scene &&
                        Vector2.Distance(HeroController.instance.transform.position, new Vector2(message.X, message.Y)) < .1f && owner.RescuesUsed == 0,
                        "reliable settlement acceptance revives at survivor before local scene progression");
                    Check(!HeroController.instance.GetComponent<Renderer>().forceRenderingOff, "settlement restores local body rendering");
                    Flag("boss-victory.settlement-local");
                }
            }));
        var other = host ? "guest" : "host";
        var authorityRole = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_VICTORY_AUTHORITY") ?? "host";
        var authority = role == authorityRole;
        var selectedCase = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_VICTORY_CASE") ?? "armour";
        var p5 = selectedCase.StartsWith("p5-", StringComparison.Ordinal);
        var dream = selectedCase == "dream-champion";
        var healthCase = selectedCase != "armour" && selectedCase != "p5-failed" && !dream;
        var godhome = selectedCase == "godhome" || p5;
        var disabled = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_VICTORY_DISABLED") == "1";
        var spectator = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SETTLEMENT_SPECTATOR") == "1";
        var scene = selectedCase == "p5-failed" ? "GG_Failed_Champion" : selectedCase == "p5-pure" ? "GG_Hollow_Knight" :
            dream ? "Dream_01_False_Knight" : godhome ? "GG_Gruz_Mother" : healthCase ? "Crossroads_04" : "Crossroads_10";
        var type = selectedCase == "p5-pure" ? EntityType.PureVessel : healthCase ? EntityType.GruzMother : EntityType.FalseKnight;
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        if (!authority) yield return Until(() => Has("boss-victory.first"), "first entrant in boss room");
        var previousVisit = Hkmp.CoopContext.LocalVisit;
        BossSequenceController.Reset(); PlayerData.instance.SetInt("bossStatueTargetLevel", 0);
        if (dream) {
            PlayerData.instance.SetBool("killedFalseKnight", true);
            PlayerData.instance.SetBool("falseKnightDefeated", true);
            PlayerData.instance.SetString("dreamReturnScene", "Crossroads_10");
            PlayerData.instance.SetString("dreamReturnGate", "door_dreamReturn");
        }
        var sequenceIndex = -1;
        string nextScene = null;
        if (p5) {
            Log("FIXTURE initial sequence return route scene=" + PlayerData.instance.GetString("dreamReturnScene") +
                " gate=" + PlayerData.instance.GetString("bossReturnEntryGate"));
            // The native Pantheon door supplies both fields before starting a
            // sequence. This fixture starts at a selected native sequence room.
            PlayerData.instance.SetString("dreamReturnScene", "Town");
            PlayerData.instance.SetString("bossReturnEntryGate", "left1");
            var sequence = Resources.Load<BossSequence>("GG/Boss Sequence Tier 5");
            sequenceIndex = Enumerable.Range(0, sequence.Count).First(i => sequence.GetSceneAt(i) == scene);
            nextScene = sequence.GetSceneAt(sequenceIndex + 1);
            BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.None, "bossDoorStateTier5");
            typeof(BossSequenceController).GetField("bossIndex", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, sequenceIndex);
            Log("FIXTURE native fifth sequence configured at index " + sequenceIndex + " next=" + nextScene);
        }
        HeroController.instance.takeNoDamage = true;
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = godhome ? "door_dreamEnter" : dream ? "door1" : "left1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => runtime.Playable && GameManager.instance.sceneName == scene && !GameManager.instance.IsInSceneTransition &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == scene && Hkmp.CoopContext.LocalScene == scene && Hkmp.CoopContext.LocalVisit > previousVisit &&
            HeroController.instance.acceptingInput && !HeroController.instance.cState.transitioning &&
            engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined && entities.Values.Any(e => e.Type == type), "native boss room ready");
        Log("SCOPE native=" + scene + " visit=" + Hkmp.CoopContext.LocalVisit + " authority=" + manager.IsSceneHost + " expected=" + authority);
        if (p5) {
            var initial = BossTravel.CaptureContext();
            Check(initial != null, "native sequence fixture captures a valid room context");
            var context = Newtonsoft.Json.JsonConvert.DeserializeObject<Message>(initial);
            Log("PREFLIGHT CONTEXT " + initial);
            Check(context.Scene == scene && context.BossReturnScene == "Town" && context.BossReturnGate == "left1" &&
                context.BossSequence.Length != 0, "native sequence capture contains the complete fixture return route");
        }
        yield return Until(() => manager.IsSceneHost == authority, "room authority follows first entrant " + authorityRole, 10);
        var hero = HeroController.instance; var body = hero.GetComponent<Rigidbody2D>(); var pd = PlayerData.instance;
        hero.takeNoDamage = true; hero.RegainControl(); hero.AffectedByGravity(true);
        yield return Until(() => hero.cState.onGround && hero.acceptingInput && !hero.cState.transitioning &&
            hero.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION && GameManager.instance.HasFinishedEnteringScene,
            "native supported hero and completed entrance before down");
        hero.SetHazardRespawn(hero.transform.position, true);
        // Keep the surviving teammate input-ready so presence reports Alive;
        // an artificial control lock correctly reports Loading instead.
        hero.AffectedByGravity(true); body.velocity = Vector2.zero;
        if (authority) Flag("boss-victory.first");
        Flag(role + ".boss-ready"); yield return Until(() => Has(other + ".boss-ready"), "both native boss entries ready");
        var bossCandidates = entities.Values.Where(e => e.Type == type).ToArray();
        Log("BOSS CANDIDATES type=" + type + " count=" + bossCandidates.Length + " all=" + string.Join(",", entities.Values.Select(e => e.Type + "/" + e.Id + "/" + (e.Object.Host != null))));
        var boss = bossCandidates.FirstOrDefault();
        Check(boss != null && boss.Object.Host != null, "boss entity has an authority object");
        var hp = boss.Object.Host.GetComponent<HealthManager>();
        if (authority && !healthCase && !godhome) {
            // Story False Knight loads its native battle in an additive
            // Crossroads_10_boss scene; the dream arena stays in its main scene.
            var trigger = Object.FindObjectsOfType<Collider2D>().Single(c => c.isTrigger && c.gameObject.name == "Battle Scene" &&
                (c.gameObject.scene.name == scene || c.gameObject.scene.name.StartsWith(scene + "_boss", StringComparison.Ordinal)));
            hero.transform.position = trigger.bounds.center; body.velocity = Vector2.zero; hero.AffectedByGravity(false);
            yield return new WaitForSecondsRealtime(1);
            foreach (var enemy in entities.Values.Where(e => !BossDefeatSignals.IsBoss(e.Type) && e.Type != EntityType.FalseKnightHead &&
                e.Object.Host != null && e.Object.Host.activeInHierarchy && e.Object.Host.GetComponent<HealthManager>() != null).ToArray())
                enemy.Object.Host.GetComponent<HealthManager>().ApplyExtraDamage(1000);
            var control = boss.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "FalseyControl");
            yield return Until(() => control.ActiveStateName == "Idle", "native battle trigger starts False Knight");
            hero.transform.position = dream ? StoryFloor(boss.Object.Host.transform.position, scene) : new Vector3(25, 33, 0);
            body.velocity = Vector2.zero; hero.AffectedByGravity(true);
            yield return Until(() => hero.cState.onGround, "authority supported after native battle intro");
            hero.SetHazardRespawn(hero.transform.position, true);
        }
        Flag(role + ".boss-started"); yield return Until(() => Has(other + ".boss-started"), "both ready for actual hero death");
        if (!godhome) {
            // Both combatants must actually be inside the closed arena. The
            // former entrance-side victim is correctly isolated by story gates.
            var floor = StoryFloor(dream || healthCase ? boss.Object.Host.transform.position : new Vector3(28.5f, 30, 0), scene);
            hero.transform.position = floor + Vector3.right * (host ? -3 : 3); body.velocity = Vector2.zero;
            hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return new WaitForFixedUpdate();
            yield return Until(() => hero.cState.onGround && hero.acceptingInput, "both victory actors on native arena floor");
            hero.SetHazardRespawn(hero.transform.position, true);
            Flag(role + ".victory-arena"); yield return Until(() => Has(other + ".victory-arena"), "both victory actors inside closed arena");
        }
        pd.SetInt("health", 5); pd.SetInt("healthBlue", 2); pd.SetInt("MPCharge", 0); pd.SetInt("MPReserve", 0);
        pd.SetInt("geo", 123); pd.SetInt("geoPool", 45); pd.SetString("shadeScene", "Town");
        Modding.ModHooks.SoulGainHook += _ => 0;
        if (!host) {
            var cameraBefore = GameCameras.instance.cameraController.transform.position;
            Die(); yield return Until(() => runtime.Down, "guest becomes a real cooperative shadow");
            if (p5) {
                var camera = GameCameras.instance.cameraController;
                var view = camera.cam.WorldToViewportPoint(runtime.DeathPosition);
                Check(view.z > 0 && view.x > 0 && view.x < 1 && view.y > 0 && view.y < 1 &&
                    Vector3.Distance(camera.transform.position, cameraBefore) < .01f, "P5 visible shade preserves original native battle camera " + scene);
            }
            Check(!body.simulated && !hero.acceptingInput, "shadow owns native control lock");
            if (spectator) {
                typeof(CoopRuntime).GetMethod("RequestGiveUp", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, null);
                yield return Until(() => runtime.Spectating && runtime.Down, "voluntary spectator before boss death");
            }
            Flag("boss-victory.down");
        }
        yield return Until(() => Has("boss-victory.down") && (host ? runtime.Peers.Values.Single().Life == (spectator ? Life.Spectating : Life.Down) : runtime.Down), "both observe defeated participant before boss death");
        if (spectator && host) yield return Until(() => runtime.PlayerObject(runtime.Peers.Single().Key).transform.parent.GetComponentsInChildren<Renderer>(true)
            .Where(r => r.name == "Username").All(r => r.forceRenderingOff), "native boss spectator name is hidden");
        if (authority) {
            if (!healthCase) {
                var control = boss.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "FalseyControl");
                yield return Until(() => control.ActiveStateName == "Idle", "native armour boss ready for first break");
                hp.ApplyExtraDamage(hp.hp);
                yield return Until(() => control.ActiveStateName == "Opened", "native armour break is not final defeat");
                Flag("boss-victory.armour");
            } else Flag("boss-victory.armour");
        }
        yield return Until(() => Has("boss-victory.armour"), "phase comparison boundary");
        yield return new WaitForSecondsRealtime(1);
        Log("TRACE unfinished boss localDown=" + runtime.Down + " returning=" + runtime.Returning + " spectating=" + runtime.Spectating +
            " peer=" + runtime.Peers.Values.Single().Life + "/" + runtime.Peers.Values.Single().State?.Scene + " scene=" + Hkmp.CoopContext.LocalScene +
            " local=" + HeroController.instance.transform.position + " anchor=" + runtime.DeathPosition);
        Check(!host ? runtime.Down : runtime.Peers.Values.Single().Life == (spectator ? Life.Spectating : Life.Down), "armour break or unfinished boss never revives participant");
        Flag(role + ".phase-checked"); yield return Until(() => Has(other + ".phase-checked"), "both reject premature revival");
        if (authority) {
            if (healthCase) {
                if (hp != null && hp.gameObject != null && hp.gameObject.activeInHierarchy) hp.ApplyExtraDamage(Math.Max(1, hp.hp));
                yield return new WaitForSecondsRealtime(.5f);
                Check(hp == null || hp.gameObject == null || hp.isDead || !hp.gameObject.activeInHierarchy, "ordinary boss has actual terminal HealthManager death");
            } else {
                var headEntity = entities.Values.FirstOrDefault(e => e.Type == EntityType.FalseKnightHead && e.Object.Host != null);
                Check(headEntity != null, "False Knight head entity remains registered");
                var head = headEntity.Object.Host.GetComponent<HealthManager>();
                var control = boss.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "FalseyControl");
                var deadline = Time.realtimeSinceStartup + 95;
                var nextTrace = 0f;
                while (Hkmp.CoopContext.LocalScene == scene && boss.Object.Host != null && boss.Object.Host.activeInHierarchy &&
                    !new[] { "Set PlayerData", "Death Anim Start", "Steam", "Ready", "Dream Return", "Blow" }.Contains(control.ActiveStateName) &&
                    Time.realtimeSinceStartup < deadline) {
                    if (Time.realtimeSinceStartup >= nextTrace) {
                        nextTrace = Time.realtimeSinceStartup + 1;
                        Log("VICTORY PHASE " + control.ActiveStateName + " armor=" + hp.hp + "/" + hp.isDead +
                            " head=" + head.hp + "/" + head.isDead + "/" + head.gameObject.activeInHierarchy +
                            " rages=" + control.FsmVariables.GetFsmInt("Rages").Value + " boss=" + boss.Object.Host.transform.position);
                    }
                    // The head object stays active between exposures. Damaging
                    // it while closed keeps choosing that object forever and
                    // never breaks the next armour phase.
                    if (control.ActiveStateName.StartsWith("Opened", StringComparison.Ordinal) && head != null && head.gameObject.activeInHierarchy && head.hp > 0)
                        head.ApplyExtraDamage(head.hp);
                    else if (control.ActiveStateName == "Idle" && hp != null && hp.gameObject.activeInHierarchy && hp.hp > 0)
                        hp.ApplyExtraDamage(hp.hp);
                    yield return new WaitForSecondsRealtime(.4f);
                }
                Check(Time.realtimeSinceStartup < deadline, "native final armour stage reached terminal death");
            }
            Flag("boss-victory.defeated");
        }
        yield return Until(() => Has("boss-victory.defeated"), "actual boss defeat completed", 80);
        if (p5 || dream) {
            yield return Until(() => Has("boss-victory.settlement-local"), "downed participant revived before native next scene", 30);
            if (!host) Check(settlementSeen, "victory acceptance observed locally");
            if (p5) {
                yield return Until(() => Hkmp.CoopContext.LocalScene == nextScene && runtime.Playable && !GameManager.instance.IsInSceneTransition &&
                    HeroController.instance.acceptingInput && engine.ClientManager.SceneSnapshotReady, "native fifth next-room control restored " + nextScene, 50);
                Check(BossSequenceController.BossIndex == sequenceIndex + 1 && !runtime.Down && !runtime.Spectating,
                    "P5 settlement keeps native sequence index and living participation");
                Check(!hero.GetComponent<Renderer>().forceRenderingOff, "native next room restores local body visibility");
                yield return RuleRemoteVisibility("native next room after Pure Vessel");
            } else {
                yield return Until(() => Hkmp.CoopContext.LocalScene == "Crossroads_10" && !GameManager.instance.IsInSceneTransition,
                    "native dream victory returns living participants to body room", 75);
                var wake = hero.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Return");
                yield return Until(() => wake.ActiveStateName == "Ready" || hero.acceptingInput, "native dream wake ready for personal input");
                if (!hero.acceptingInput) yield return RulePress(InputHandler.Instance.inputActions.jump);
                yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished, "native dream wake restores settlement participant control");
                Check(Object.FindObjectsOfType<PlayMakerFSM>(true).Any(f => f.name == "Ghost False Knight NPC" && f.gameObject.activeInHierarchy),
                    "revived dream participant has personal reward spirit available");
                var reward = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.name == "Ghost False Knight NPC" && f.FsmName == "Conversation Control");
                var npc = reward.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "npc_control");
                var beforeOrbs = pd.GetInt("dreamOrbs");
                hero.transform.position = reward.transform.position + Vector3.right;
                body.velocity = Vector2.zero; Physics2D.SyncTransforms();
                yield return Until(() => npc.ActiveStateName == "In Range", "personal native reward interaction range");
                yield return RulePress(InputHandler.Instance.inputActions.up);
                yield return Until(() => reward.ActiveStateName == "Defeated", "native spirit conversation after real interact input");
                reward.SendEvent("CONVO_FINISH"); // Dialogue-close boundary; native actions grant the personal reward.
                yield return Until(() => pd.GetBool("falseKnightOrbsCollected") && pd.GetInt("dreamOrbs") == beforeOrbs + 300,
                    "each revived dream participant claims own 300 essence");
                yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished && !pd.GetBool("disablePause"),
                    "native reward conversation restores control");
            }
            Flag(role + ".boss-checked"); yield return Until(() => Has(other + ".boss-checked"), "both special settlement checks complete");
            yield break;
        }
        if (disabled) {
            yield return new WaitForSecondsRealtime(2);
            Check(!host ? runtime.Down && !body.simulated : runtime.Peers.Values.Single().Life == Life.Down, "host disabled option retains normal shadow rescue");
        } else {
            yield return Until(() => !runtime.Down && runtime.Peers.Values.Single().Life == Life.Alive, "server and both clients revive current death episode");
            if (!host) {
                Check(pd.health == 1 && pd.healthBlue == 0 && !runtime.Returning && !runtime.Resetting, "recovered hero has exactly one red mask and no blue masks");
                yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished && body.simulated && !HeroBox.inactive &&
                    GameCameras.instance.cameraController.mode != CameraController.CameraMode.FROZEN, "native input physics hitbox and camera restored");
                Check(pd.geo == 123 && pd.geoPool == 45 && pd.shadeScene == "Town", "personal geo and formal shade metadata preserved");
                var survivor = runtime.PlayerObject(runtime.Peers.Single().Key);
                Check(survivor != null && Vector2.Distance(hero.transform.position, survivor.transform.position) < 2,
                    "settlement recovery uses surviving teammate rather than death anchor");
                Check(runtime.RescuesUsed == 0, "boss settlement does not spend nail rescue quota");
                Check(body.gravityScale > 0, "recovery restores native gravity without test repair");
                yield return Until(() => hero.cState.onGround && Math.Abs(body.velocity.y) < .5, "recovered hero has real ground support");
            } else Check(pd.health == 5 && pd.healthBlue == 2, "living teammate retains original health");
            Check(pd.MPCharge == 0, "automatic recovery gives no rescuer soul reward");
            yield return new WaitForSecondsRealtime(1);
            Check(Object.FindObjectsOfType<ShadeView>().Length == 0, "both clients retire shadow visuals");
        }
        Flag(role + ".boss-checked"); yield return Until(() => Has(other + ".boss-checked"), "both complete boss recovery checks");
    }
}
