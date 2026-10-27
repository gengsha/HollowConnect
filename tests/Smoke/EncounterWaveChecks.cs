using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private static string EncounterPath(GameObject go) => go == null ? "null" : EntityReferences.Capture(go).Replace('\n', '|');
    private void EncounterSchema(PlayMakerFSM fsm) {
        Log("ENCOUNTER FSM " + EncounterPath(fsm.gameObject) + "/" + fsm.FsmName + "=" + fsm.ActiveStateName + " active=" + fsm.gameObject.activeInHierarchy + " pos=" + fsm.transform.position);
        Log("ENCOUNTER INTS " + string.Join(",", fsm.FsmVariables.IntVariables.Select(v => v.Name + "=" + v.Value)));
        foreach (var state in fsm.FsmStates) {
            Log("ENCOUNTER STATE " + state.Name + " -> " + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
            foreach (var action in state.Actions) {
                var fields = action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(field => {
                    var value = field.GetValue(action);
                    return field.Name + "=" + (value is FsmGameObject obj ? EncounterPath(obj.Value) : value is FsmOwnerDefault owner ? EncounterPath(fsm.Fsm.GetOwnerDefaultTarget(owner)) : value is FsmEventTarget target ? target.target + ":" + EncounterPath(fsm.Fsm.GetOwnerDefaultTarget(target.gameObject)) + ":children=" + target.sendToChildren + ":fsm=" + target.fsmName : value?.ToString());
                });
                Log("ENCOUNTER ACTION " + state.Name + "/" + action.GetType().Name + " " + string.Join(";", fields));
            }
        }
    }
    private IEnumerator EncounterWaveChecks() {
        var scene = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENCOUNTER_SCENE") ?? "Crossroads_08";
        var entry = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENCOUNTER_ENTRY") ?? (scene == "Crossroads_22" ? "bot1" : "left1");
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENCOUNTER_CASE");
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENCOUNTER_AUTHORITY") ?? "host");
        var other = host ? "guest" : "host";
        var hero = HeroController.instance;
        using var contactGuard = IgnoreExternalCombatDamage();
        foreach (var index in new[] { 44, 59, 309, 315 })
            Log("ENCOUNTER ASSET level" + index + "=" + UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(index));
        var late = mode == "late" || mode == "late-end";
        if (!authority) yield return Until(() => Has(late ? mode == "late-end" ? "encounter.finished" : "encounter.first-killed" : "encounter.first"), "first player owns encounter scene or completed first wave");
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = entry,
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene).isLoaded, "native encounter scene loaded");
        var entries = Object.FindObjectsOfType<TransitionPoint>(true).Where(p => p.gameObject.scene.name == scene).ToArray();
        Log("ENCOUNTER ENTRIES requested=" + entry + " actual=" + string.Join(",", entries.Select(p => p.name + "@" + p.transform.position)));
        Check(entries.Any(p => p.name == entry), "requested native encounter entry exists");
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        yield return Until(() => runtime.Playable && hero.acceptingInput && GameManager.instance.sceneName == scene && !GameManager.instance.IsInSceneTransition &&
            engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined && manager.IsSceneHost == authority, "encounter scene snapshot and authority ready");
        hero.takeNoDamage = true;
        var fsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene).ToArray();
        if (mode == "schema" || mode == "inventory") foreach (var fsm in fsms.Where(f => f.FsmName.Contains("Battle") || f.FsmName == "BG Control")) EncounterSchema(fsm);
        foreach (var point in Object.FindObjectsOfType<TransitionPoint>(true).Where(p => p.gameObject.scene.name == scene))
            Log("ENCOUNTER ENTRY " + point.name + "@" + point.transform.position);
        foreach (var health in Object.FindObjectsOfType<HealthManager>(true).Where(h => h.gameObject.scene.name == scene))
            Log("ENCOUNTER ENEMY " + EncounterPath(health.gameObject) + " name=" + health.name + " pos=" + health.transform.position + " active=" + health.gameObject.activeInHierarchy + " hp=" + health.hp + " battle=" + EncounterPath(typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(health) as GameObject));
        if (mode == "inventory") {
            var helpers = entities.Values.Where(e => e.Type == EntityType.EncounterSummoner).ToArray();
            Check(helpers.Length > 0, "native summon family actually loaded in " + scene);
            foreach (var helper in helpers) {
                // Proximity-triggered helpers may already have detached their
                // native child during entry. The original FSM retains that
                // reference after its native SetParent action.
                var helperFsm = helper.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "summon");
                EncounterSchema(helperFsm);
                var children = helper.Object.Host.GetComponentsInChildren<HealthManager>(true).Concat(
                    helperFsm.FsmVariables.GameObjectVariables.Select(v => v.Value == null ? null : v.Value.GetComponent<HealthManager>())
                        .Where(h => h != null)).Distinct().ToArray();
                Check(children.Length == 1, "one native enemy retained by " + helper.Object.Host.name + "; found=" + string.Join(",", children.Select(h => h.name)));
                var child = children[0]; var expected = child.name.StartsWith("Super Spitter") ? EntityType.PrimalAspid : EntityType.AspidHunter;
                var owner = EntityReferences.Owner(child.gameObject);
                Check(owner != null && owner.Type == expected && owner.Object.Host == child.gameObject, "native summon child retains its enemy family " + expected);
                Log("ENCOUNTER INVENTORY helper=" + EncounterPath(helper.Object.Host) + " child=" + owner.Id + ":" + owner.Type +
                    " native=" + child.name + " helper=" + helper.Id);
            }
            if (authority) Flag("encounter.first");
            Flag(role + ".encounter.checked"); yield return Until(() => Has(other + ".encounter.checked"), "both native inventories checked");
            yield break;
        }
        var battles = fsms.Where(f => f.FsmName == "Battle Control").ToArray();
        Check(battles.Length == 1, "exactly one native Battle Control; found " + string.Join(",", battles.Select(f => f.name)));
        var battle = battles[0];
        if (mode == "schema" || Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENCOUNTER_SCHEMA") == "1")
            foreach (var fsm in battle.GetComponentsInChildren<PlayMakerFSM>(true)) EncounterSchema(fsm);
        var trigger = battle.GetComponent<Collider2D>();
        Check(trigger != null && trigger.isTrigger, "native encounter collider exists at " + battle.transform.position);
        Log("ENCOUNTER TRIGGER " + trigger.bounds);
        if (authority) Flag("encounter.first");
        if (mode == "schema") {
            hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.transform.position = trigger.bounds.center; Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(2);
            Log("ENCOUNTER AFTER ENTRY " + battle.ActiveStateName);
            var camera = GameCameras.instance.cameraController.cam;
            var priorTarget = camera.targetTexture; var priorActive = RenderTexture.active;
            var target = new RenderTexture(800, 600, 24);
            var pixels = new Texture2D(800, 600, TextureFormat.RGB24, false);
            try {
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 800, 600), 0, 0); pixels.Apply();
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(root, role + ".room.png"), pixels.EncodeToPNG());
            } finally {
                camera.targetTexture = priorTarget; RenderTexture.active = priorActive;
                Object.Destroy(pixels); Object.Destroy(target);
            }
            Flag(role + ".encounter.checked");
            yield return Until(() => Has(other + ".encounter.checked"), "both encounter schemas captured");
            yield break;
        }
        var gates = fsms.Where(f => f.FsmName == "BG Control").ToArray();
        var counter = battle.FsmVariables.FindFsmInt("Battle Enemies");
        Check(LocalBattleCompletion.IsLocal(battle.gameObject) && gates.Length == 4 && counter != null, "native local four-gate Aspid encounter contract");
        var battleField = typeof(HealthManager).GetField("battleScene", BindingFlags.Instance | BindingFlags.NonPublic);
        var insects = entities.Values.Where(e => e.Type == EntityType.AspidHunter && e.Object.Host != null &&
            battleField.GetValue(e.Object.Host.GetComponent<HealthManager>()) as GameObject == battle.gameObject).ToArray();
        Check(insects.Length == 3, "three native bound Aspid Hunters");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_ENCOUNTER_FIXED") == "1")
            Check(entities.Values.Count(e => e.Type == EntityType.EncounterSummoner) == 2, "two native summon helpers share scene authority");
        var first = insects.Single(e => e.Object.Host.transform.position.x < 50);
        foreach (var insect in insects) {
            Log("ENCOUNTER ASPID id=" + insect.Id + " host=" + insect.Object.Host.transform.position + " active=" + insect.Object.Host.activeInHierarchy);
        }
        hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        var outside = mode == "outside" && !authority || (mode.StartsWith("remote") || mode == "handoff" || mode == "disconnect") && authority;
        hero.transform.position = outside ? new Vector3(12, 12, 0) : trigger.bounds.center;
        Physics2D.SyncTransforms();
        if (!outside) yield return Until(() => battle.ActiveStateName != "Detect", "local hero collision starts native battle");
        else Check(battle.ActiveStateName == "Detect", "scene authority or delayed player remains outside native trigger");
        Flag(role + ".encounter.entered");
        if (authority) {
            if (!late) yield return Until(() => Has(other + ".encounter.entered"), "both configured entry conditions ready");
            var health = first.Object.Host.GetComponent<HealthManager>();
            Check(health.hp > 0 && !health.isDead, "first native Aspid alive before native damage");
            if (mode == "remote-nail") yield return Until(() => Has("encounter.nail-killed") && (first.Object.Host == null || health.isDead), "teammate's actual replicated nail collision kills first Aspid", 15);
            else health.ApplyExtraDamage(health.hp);
            Log("ENCOUNTER FIRST KILLED battle=" + battle.ActiveStateName + " counter=" + counter.Value);
            Flag("encounter.first-killed");
        } else {
            if (mode == "remote-nail") {
                var health = first.Object.Client.GetComponent<HealthManager>();
                var deadline = Time.realtimeSinceStartup + 12;
                hero.RegainControl(); hero.StartAnimationControl();
                while (first.Object.Client != null && !health.isDead && Time.realtimeSinceStartup < deadline) {
                    hero.transform.position = health.GetComponent<Collider2D>().bounds.center + new Vector3(-3, -.5f, 0);
                    hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.FaceRight(); Physics2D.SyncTransforms();
                    hero.Attack(GlobalEnums.AttackDirection.normal);
                    yield return new WaitForSecondsRealtime(.55f);
                }
                Check(first.Object.Client == null || health.isDead, "real local nail and replicated attack kill first native Aspid");
                Flag("encounter.nail-killed");
                hero.transform.position = trigger.bounds.center; Physics2D.SyncTransforms();
            }
            yield return Until(() => Has("encounter.first-killed") && first.Object.Host.GetComponent<HealthManager>().isDead,
                "terminal first Aspid snapshot applied");
        }
        if (outside && !authority) {
            hero.transform.position = trigger.bounds.center; Physics2D.SyncTransforms();
            yield return Until(() => battle.ActiveStateName != "Detect", "delayed same-room hero triggers native battle");
        }
        if (mode == "remote-enter" && authority) {
            yield return new WaitForSecondsRealtime(2);
            hero.transform.position = trigger.bounds.center; Physics2D.SyncTransforms();
            yield return Until(() => battle.ActiveStateName != "Detect", "authority later enters native trigger after teammate's first kill");
            Log("ENCOUNTER AUTHORITY LATE START state=" + battle.ActiveStateName + " counter=" + counter.Value);
        }
        if (mode == "handoff" || mode == "disconnect") {
            if (!authority) {
                yield return Until(() => entities.Values.Where(e => e.Type == EntityType.EncounterSummoner).All(e =>
                    Field<bool>(Field<Dictionary<Hkmp.Game.Client.Entity.Component.EntityComponentType, Hkmp.Game.Client.Entity.Component.EntityComponent>>(e, "_components")
                        [Hkmp.Game.Client.Entity.Component.EntityComponentType.EncounterSummon], "accepted")), "native wave requests accepted before authority departure");
                Flag("encounter.request-accepted");
                yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost, "native summon authority transfers to remaining participant");
                Log("ENCOUNTER TAKEOVER helpers=" + string.Join(",", entities.Values.Where(e => e.Type == EntityType.EncounterSummoner).Select(e =>
                    e.Id + ":" + string.Join("/", e.Object.Host.GetComponents<PlayMakerFSM>().Select(f => f.ActiveStateName)))));
                Flag("encounter.takeover-ready");
                yield return Until(() => Has("encounter.returned"), "former authority reenters before second wave is killed");
            } else {
                yield return Until(() => Has("encounter.request-accepted"), "peer confirms accepted native summon request");
                Log("ENCOUNTER DEPARTURE helpers=" + string.Join(",", entities.Values.Where(e => e.Type == EntityType.EncounterSummoner).Select(e =>
                    e.Id + ":" + string.Join("/", e.Object.Host.GetComponents<PlayMakerFSM>().Select(f => f.ActiveStateName)))));
                if (mode == "disconnect") { engine.ClientManager.Disconnect(); Flag("encounter.disconnected"); }
                GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = "Town", EntryGateName = "left1", Visualization = GameManager.SceneLoadVisualizations.Default });
                yield return Until(() => GameManager.instance.sceneName == "Town" && !GameManager.instance.IsInSceneTransition && hero.acceptingInput, "former authority leaves isolated encounter natively");
                yield return Until(() => Has("encounter.takeover-ready"), "remaining player owns summons");
                if (mode == "disconnect") {
                    typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
                    typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "rescue" + role });
                    yield return Until(() => runtime.Connected && engine.ClientManager.SceneSnapshotReady, "former authority reconnects over real UDP");
                }
                GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = scene, EntryGateName = entry, Visualization = GameManager.SceneLoadVisualizations.Default });
                yield return Until(() => runtime.Playable && GameManager.instance.sceneName == scene && !GameManager.instance.IsInSceneTransition &&
                    hero.acceptingInput && engine.ClientManager.SceneSnapshotReady && manager.IsSceneHostDetermined && !manager.IsSceneHost, "former authority reenters as replica with retained native summon snapshot");
                fsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == scene).ToArray();
                battle = fsms.Single(f => f.FsmName == "Battle Control"); gates = fsms.Where(f => f.FsmName == "BG Control").ToArray();
                counter = battle.FsmVariables.FindFsmInt("Battle Enemies"); trigger = battle.GetComponent<Collider2D>();
                insects = entities.Values.Where(e => e.Type == EntityType.AspidHunter && e.Object.Host != null &&
                    battleField.GetValue(e.Object.Host.GetComponent<HealthManager>()) as GameObject == battle.gameObject).ToArray();
                Check(insects.Length == 3 && insects.Count(e => e.Object.Host.GetComponent<HealthManager>().isDead) == 1, "reentry snapshot retains exactly one terminal Aspid and two living ones");
                first = insects.Single(e => e.Object.Host.GetComponent<HealthManager>().isDead);
                hero.AffectedByGravity(false); hero.transform.position = trigger.bounds.center;
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms(); outside = false;
                yield return Until(() => battle.ActiveStateName != "Detect", "reentered hero uses original encounter collider");
                Flag("encounter.returned");
            }
        }
        if (authority && mode != "late-end") yield return Until(() => Has(other + ".encounter.entered"), "teammate arrives after first kill when configured");
        var summoned = false; var previous = "";
        var end = Time.realtimeSinceStartup + 14;
        while (Time.realtimeSinceStartup < end && battle.ActiveStateName != "End") {
            var live = insects.Where(e => e.Id != first.Id).Select(e => manager.IsSceneHost ? e.Object.Host : e.Object.Client)
                .Where(go => go != null && go.activeInHierarchy && go.transform.position.x >= 19 && go.transform.position.x <= 44)
                .Select(go => go.GetComponent<HealthManager>()).Where(h => h != null && !h.isDead && h.hp > 0).ToArray();
            var observation = battle.ActiveStateName + "/" + counter.Value + "/" + live.Length;
            if (previous != observation) { previous = observation; Log("ENCOUNTER PROGRESS state/counter/live-wave2=" + observation); }
            if (live.Length == 2 && !summoned) {
                summoned = true;
                Check(battle.ActiveStateName != "End" && gates.All(g => g.GetComponent<Collider2D>().enabled == !(authority && outside && mode != "remote-enter")),
                    "both second-wave enemies in arena; participant gates closed, outside authority gates open");
                Flag(role + ".encounter.summoned");
            }
            if (manager.IsSceneHost && summoned && (mode == "late-end" || Has(other + ".encounter.summoned")))
                foreach (var h in live) h.ApplyExtraDamage(h.hp);
            if (battle.ActiveStateName == "End") Flag(role + ".encounter.local-complete");
            if (authority && outside && mode != "remote-enter" && Has(other + ".encounter.local-complete")) break;
            yield return new WaitForSecondsRealtime(.1f);
        }
        if (battle.ActiveStateName == "End") Flag(role + ".encounter.local-complete");
        if (authority && outside && mode != "remote-enter" && Has(other + ".encounter.local-complete")) {
            Check(battle.ActiveStateName == "Detect" && gates.All(g => !g.GetComponent<Collider2D>().enabled), "completed teammate encounter never closes gates on outside authority");
            hero.transform.position = trigger.bounds.center; Physics2D.SyncTransforms();
            yield return Until(() => battle.ActiveStateName == "End", "authority later crosses native trigger and consumes all early native deaths", 8);
        }
        foreach (var helper in entities.Values.Where(e => e.Type == EntityType.EncounterSummoner)) {
            var components = Field<Dictionary<Hkmp.Game.Client.Entity.Component.EntityComponentType, Hkmp.Game.Client.Entity.Component.EntityComponent>>(helper, "_components");
            var component = components[Hkmp.Game.Client.Entity.Component.EntityComponentType.EncounterSummon];
            Log("ENCOUNTER HELPER id=" + helper.Id + " requested=" + Field<bool>(component, "requested") + " accepted=" + Field<bool>(component, "accepted") +
                " state=" + string.Join(",", (manager.IsSceneHost ? helper.Object.Host : helper.Object.Client).GetComponents<PlayMakerFSM>().Select(f => f.ActiveStateName)));
        }
        foreach (var insect in insects) {
            var go = manager.IsSceneHost ? insect.Object.Host : insect.Object.Client;
            if (go == null) { Log("ENCOUNTER FINAL ASPID id=" + insect.Id + " destroyed by native death"); continue; }
            Log("ENCOUNTER FINAL ASPID id=" + insect.Id + " pos=" + go.transform.position + " active=" + go.activeInHierarchy +
                " hp=" + go.GetComponent<HealthManager>().hp + " dead=" + go.GetComponent<HealthManager>().isDead +
                " fsms=" + string.Join(",", go.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.ActiveStateName)));
        }
        if (mode == "late-end" && !authority) Check(insects.All(e => e.Object.Host.GetComponent<HealthManager>().isDead), "late entrant receives all three native deaths without spawning another wave");
        else Check(summoned, "native SUMMON actually produces both second-wave Aspids");
        Check(battle.ActiveStateName == "End" && counter.Value == 0 && gates.All(g => !g.GetComponent<Collider2D>().enabled),
            "native complete encounter reaches End/count zero/four open gate colliders; actual=" + battle.ActiveStateName + "/" + counter.Value);
        if (authority) Flag("encounter.finished");
        foreach (var gate in gates) {
            var direction = gate.transform.position.x < trigger.bounds.center.x ? -1f : 1f;
            var box = gate.GetComponent<BoxCollider2D>();
            var center = gate.transform.TransformPoint(box.offset);
            hero.transform.position = center - Vector3.right * (2.5f * direction);
            hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
            using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = direction; })))
                yield return Until(() => direction * (hero.transform.position.x - center.x) > 1.5f, "native movement crosses open " + gate.name, 5);
        }
        Flag(role + ".encounter.checked");
        yield return Until(() => Has(other + ".encounter.checked"), "both native encounter completions agree");
    }
}
