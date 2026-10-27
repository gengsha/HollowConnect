using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Modding;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Targeting = Hkmp.Game.Client.Entity.BossTargeting;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private bool FlexibleTargetCheck => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_FLEX_TARGET_AUTHORITY"));
    private MenuScreen flexibleMenu;
    private List<IMenuMod.MenuEntry> originalRoomEntries;
    private void ConfigureFlexibleTargetChecks() {
        CoopMod.Settings.BossTargetingMode = host ? 1 : 0;
        originalRoomEntries = CoopMod.Instance.GetMenuData(null).Where(e => CoopMod.IsRoomSetting(e.Name)).ToList();
        Check(!CoopMod.RoomSettingsLocked, "room configuration is editable before hosting or connecting");
        flexibleMenu = CoopMod.Instance.GetMenuScreen(null, null);
        var rows = flexibleMenu.GetComponentsInChildren<CoopMenuLockRow>(true);
        Check(rows.Length == originalRoomEntries.Count && rows.Length >= 9, "every captured room rule has a lockable native menu row");
        flexibleMenu.gameObject.SetActive(false);
    }

    private IEnumerator FlexibleMenuLockChecks() {
        Check(CoopMod.RoomSettingsLocked, "actual connection locks room rules on both host and guest");
        flexibleMenu.gameObject.SetActive(true);
        foreach (var row in flexibleMenu.GetComponentsInChildren<CoopMenuLockRow>(true)) row.Refresh();
        Check(flexibleMenu.GetComponentsInChildren<MenuOptionHorizontal>(true).Single(o => o.name == "Boss 索敌模式（房主）").optionList[0].Contains("灵活") &&
            CoopMod.Settings.BossTargetingMode == (host ? 1 : 0), "locked menu shows effective host rule without overwriting guest preset");
        var before = JsonConvert.SerializeObject(CoopMod.Settings);
        foreach (var entry in originalRoomEntries) entry.Saver((entry.Loader() + 1) % entry.Values.Length);
        foreach (var row in flexibleMenu.GetComponentsInChildren<CoopMenuLockRow>(true)) {
            var option = row.GetComponent<MenuOptionHorizontal>();
            Check(option.optionList.Length == 1 && option.optionList[0].Contains("已锁定") && option.interactable,
                "locked row remains focusable with explicit fixed value: " + option.name);
            var description = row.transform.Find("Description")?.GetComponent<Text>();
            Check(description != null && description.text.Contains("断开联机"), "locked row explains how to unlock: " + option.name);
            option.OnMove(new AxisEventData(EventSystem.current) { moveDir = MoveDirection.Left });
            option.OnMove(new AxisEventData(EventSystem.current) { moveDir = MoveDirection.Right });
            option.OnPointerClick(new PointerEventData(EventSystem.current));
            Check(option.selectedOptionIndex == 0, "mouse and keyboard cannot cycle a locked setting: " + option.name);
        }
        Check(JsonConvert.SerializeObject(CoopMod.Settings) == before, "locked interactions and stale pre-connect savers do not mutate configuration");
        var opacity = CoopMod.Instance.GetMenuData(null).Single(e => e.Name == "队友透明度（本机）");
        opacity.Saver(2); Check(CoopMod.Settings.OtherPlayerOpacity == .5f, "local display setting remains adjustable while connected"); opacity.Saver(0);
        flexibleMenu.gameObject.SetActive(false);
        yield return null;
    }

    private IEnumerator FlexibleMenuUnlockChecks() {
        Check(!CoopMod.RoomSettingsLocked && !runtime.flexibleBossTargeting, "disconnect and stopping host clear room rule and unlock configuration");
        flexibleMenu.gameObject.SetActive(true);
        foreach (var row in flexibleMenu.GetComponentsInChildren<CoopMenuLockRow>(true)) {
            row.Refresh(); var option = row.GetComponent<MenuOptionHorizontal>();
            Check(option.optionList.Length >= 2 && !option.optionText.text.Contains("已锁定"), "existing menu row unlocks without recreating menu: " + option.name);
        }
        var mode = originalRoomEntries.Single(e => e.Name == "Boss 索敌模式（房主）");
        var old = mode.Loader(); mode.Saver(1 - old); Check(mode.Loader() == 1 - old, "pre-connect menu saver works again after leaving multiplayer"); mode.Saver(old);
        if (MenuPointerCheck) {
            ShowPointerMenu(flexibleMenu);
            var option = flexibleMenu.GetComponentsInChildren<MenuOptionHorizontal>(true).Single(o => o.name == mode.Name);
            CheckPointerRow(option, mode.Loader, "unlocked room setting");
        }
        UnityEngine.Object.Destroy(flexibleMenu.gameObject);
        yield return null;
    }

    private IEnumerator FlexibleTargetChecks() {
        yield return FlexibleMenuLockChecks();
        Check(runtime.flexibleBossTargeting && CoopContext.FlexibleBossTargetingOn(), "host flexible rule overrides guest native preference");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_FLEX_TARGET_SCENE") == "GG_Grimm_Nightmare") {
            yield return NightmarePillarChecks(); yield break;
        }
        const string scene = "GG_Hornet_1";
        var authority = role == Environment.GetEnvironmentVariable("HOLLOWCONNECT_FLEX_TARGET_AUTHORITY");
        var other = host ? "guest" : "host";
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        if (!authority) yield return Until(() => Has("flex.first"), "flex first entrant");
        BossSequenceController.Reset(); PlayerData.instance.SetInt("bossStatueTargetLevel", 0);
        HeroController.instance.takeNoDamage = true;
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = "door_dreamEnter",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => runtime.Playable && CoopContext.LocalScene == scene && engine.ClientManager.SceneSnapshotReady &&
            manager.IsSceneHostDetermined && manager.IsSceneHost == authority && entities.Values.Any(e => e.Type == EntityType.Hornet), "flex native Hornet authority");
        var hero = HeroController.instance;
        yield return Until(() => hero.acceptingInput && hero.cState.onGround && GameManager.instance.HasFinishedEnteringScene, "flex native Hornet entry");
        using var noContact = IgnoreExternalCombatDamage();
        hero.AffectedByGravity(false); var body = hero.GetComponent<Rigidbody2D>(); body.velocity = Vector2.zero;
        var floor = hero.transform.position.y;
        hero.transform.position = new Vector3(authority ? 9 : 30, floor, 0);
        if (authority) Flag("flex.first");
        Flag(role + ".flex-ready");
        yield return Until(() => Has(other + ".flex-ready") && runtime.Peers.Values.Single().Life == Life.Alive, "flex both players alive");
        var boss = entities.Values.Single(e => e.Type == EntityType.Hornet);
        Check(boss.BossTargetReceipt != null && boss.BossTargetControl != null, "boss target snapshot prepared before native variable capture");
        Log("FLEX SCHEMA " + boss.BossTargetControl.Name + " states=" + string.Join(",", boss.BossTargetControl.States.Select(s => s.Name)));
        var nativeReads = 0; var remoteReads = 0; var localReads = 0; var inherited = 0; var badReads = 0;
        using var reads = new Hook(typeof(Targeting).GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Static),
            new Func<Func<GameObject, FsmStateAction, GameObject>, GameObject, FsmStateAction, GameObject>((orig, original, action) => {
                var target = orig(original, action);
                if (!authority || original != hero.gameObject || EntityReferences.Owner(action?.Fsm?.GameObject) != boss) return target;
                var choice = BossTargetCommitment.Decode(boss.BossTargetReceipt.Value, runtime.Session, CoopContext.Epoch);
                var expected = CoopContext.BossTargetObject(choice.Player);
                if (expected != null && target != expected) badReads++;
                if (expected != null) { nativeReads++; if (target == hero.gameObject) localReads++; else remoteReads++; }
                return target;
            }));
        using var spawn = new Hook(typeof(Targeting).GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Static),
            new Action<Action<Entity, PlayMakerFSM[], PlayMakerFSM[], Entity>, Entity, PlayMakerFSM[], PlayMakerFSM[], Entity>((orig, e, h, c, source) => {
                orig(e, h, c, source);
                if (!authority || source != boss || !e.InheritedBossTarget || e.BossTargetReceipt == null) return;
                if (e.BossTargetReceipt.Value != source.BossTargetReceipt.Value) throw new Exception("attack entity lost source target");
                inherited++; Log("FLEX INHERITED " + e.Type + " " + e.Object.Host.name + " receipt=" + e.BossTargetReceipt.Value);
            }));
        if (authority) {
            yield return Until(() => remoteReads >= 3, "natural Hornet reads target living teammate while local hero is alive", 90);
            var choice = BossTargetCommitment.Decode(boss.BossTargetReceipt.Value, runtime.Session, CoopContext.Epoch);
            Check(choice.Player == runtime.Peers.Single().Key, "nearest living teammate can attract boss independently of room authority");
            FlexibleTargetBoundaryChecks(boss, hero);
            Flag("flex.remote-observed");
        } else yield return Until(() => Has("flex.remote-observed"), "authority observes native remote targeting", 120);
        hero.transform.position = new Vector3(authority ? 30 : 9, floor, 0); body.velocity = Vector2.zero;
        Flag(role + ".flex-swapped"); yield return Until(() => Has(other + ".flex-swapped"), "players exchange distance advantage");
        if (authority) {
            yield return Until(() => localReads >= 3, "native decision boundary eventually switches back to closer authority", 90);
            Log("FLEX dynamic attack inheritance samples=" + inherited + "; required natural generation is covered separately by NKG pillars");
            Check(nativeReads >= 6 && badReads == 0, "native position direction and aim reads agree with committed target");
            var avatar = runtime.PlayerObject(runtime.Peers.Single().Key);
            Check(!boss.Object.Host.GetComponentsInChildren<PlayMakerFSM>(true).SelectMany(f => f.FsmVariables.GameObjectVariables).Any(v => v.Value == avatar),
                "flexible targeting never persists remote avatars into native FSM variables");
            Flag("flex.local-observed");
        } else yield return Until(() => Has("flex.local-observed"), "authority verifies native switch and generated attacks", 210);
        // Return the target to the survivor before transfer, so retaining it
        // exercises snapshot continuity rather than invalid-target recovery.
        hero.transform.position = new Vector3(authority ? 9 : 30, floor, 0); body.velocity = Vector2.zero;
        if (authority) {
            yield return Until(() => BossTargetCommitment.Decode(boss.BossTargetReceipt.Value, runtime.Session, CoopContext.Epoch).Player == runtime.Peers.Single().Key,
                "survivor committed before authority departure", 90);
            Flag("flex.survivor-target");
            yield return Until(() => Has("flex.snapshot-received"), "survivor receives committed target snapshot");
            engine.ClientManager.Disconnect(); Flag("flex.authority-left");
            yield return Until(() => Has("flex.takeover-passed"), "survivor checks native target takeover", 90);
        } else {
            yield return Until(() => Has("flex.survivor-target") && BossTargetCommitment.Decode(boss.BossTargetReceipt.Value, runtime.Session, CoopContext.Epoch).Player == runtime.LocalId,
                "replica receives committed player ID in ordinary FSM snapshot", 120);
            Flag("flex.snapshot-received");
            yield return Until(() => Has("flex.authority-left") && manager.IsSceneHost && boss.IsNativeAuthority, "survivor becomes native authority");
            Check(BossTargetCommitment.Decode(boss.BossTargetReceipt.Value, runtime.Session, CoopContext.Epoch).Player == runtime.LocalId,
                "takeover retains eligible committed player");
            Flag("flex.takeover-passed");
        }
        if (host) Check(CoopMod.RoomSettingsLocked, "hosting keeps rules locked even after host client disconnects");
    }

    private void FlexibleTargetBoundaryChecks(Entity boss, HeroController hero) {
        var peer = runtime.Peers.Single(); var avatar = runtime.PlayerObject(peer.Key);
        var oldHero = hero.transform.position; var oldPeer = avatar.transform.position; var oldBoss = boss.Object.Host.transform.position;
        var life = peer.Value.Life; var seen = peer.Value.Seen; var scene = peer.Value.State.Scene;
        try {
            boss.Object.Host.transform.position = Vector3.zero; hero.transform.position = new Vector3(10, 0); avatar.transform.position = new Vector3(8, 0);
            Check(CoopContext.ChooseBossTarget(boss.Object.Host, runtime.LocalId, true) == runtime.LocalId, "20 percent closer challenger does not break hysteresis");
            avatar.transform.position = new Vector3(7, 0);
            Check(CoopContext.ChooseBossTarget(boss.Object.Host, runtime.LocalId, true) == peer.Key, "30 percent closer challenger can win at decision boundary");
            Check(CoopContext.ChooseBossTarget(boss.Object.Host, runtime.LocalId, false) == runtime.LocalId, "aim and facing reads alone never replace an eligible committed player");
            foreach (var invalid in new[] { Life.Down, Life.Spectating, Life.Returning, Life.Resetting }) {
                peer.Value.Life = invalid;
                Check(CoopContext.ChooseBossTarget(boss.Object.Host, peer.Key, false) == runtime.LocalId, "invalid committed target immediately replaced: " + invalid);
            }
            peer.Value.Life = Life.Loading;
            Check(CoopContext.BossTargetObject(peer.Key) == avatar, "temporary input lock remains a living target");
            peer.Value.Seen = CoopRuntime.Now - 3; Check(CoopContext.BossTargetObject(peer.Key) == null, "stale remote target excluded"); peer.Value.Seen = seen;
            peer.Value.State.Scene = "Town"; Check(CoopContext.BossTargetObject(peer.Key) == null, "other room target excluded");
        } finally {
            hero.transform.position = oldHero; avatar.transform.position = oldPeer; boss.Object.Host.transform.position = oldBoss;
            peer.Value.Life = life; peer.Value.Seen = seen; peer.Value.State.Scene = scene;
        }
    }
}
