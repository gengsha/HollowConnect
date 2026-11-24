using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using Modding;
using UnityEngine;
using Scenes = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator PackCompatibilityChecks() {
        foreach (var name in new[] { "Benchwarp", "EnemyHPBar", "MapChangerMod", "VanillaMapMod", "Additional Maps" })
            ResourceCheck(ModHooks.GetMod(name, onlyEnabled: true) != null, name + " enabled");
        ResourceCheck(Field<MonoMod.RuntimeDetour.Hook>(runtime, "benchwarpRespawnHook") != null, "Benchwarp adapter installed");
        var service = Field<MapService>(runtime, "map");
        ResourceCheck(Field<Delegate>(service, "mapChangerZone") != null, "MapChanger adapter resolved");
        var benchwarp = ModHooks.GetMod("Benchwarp").GetType().Assembly.GetType("Benchwarp.ChangeScene", true);
        var door = benchwarp.GetMethod("ChangeToScene");
        var bench = benchwarp.GetMethod("WarpToRespawn");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_PACK_LINKED") == "1") {
            // Use the same real gameplay entry as the normal pack case. Fresh
            // Town respawn and the title fade can still own the transition state.
            yield return Until(() => runtime.LinkedHealthRuleEnabled, "room negotiated shared health", 10);
            if (Has(role + ".failed")) {
                Mark($"LINKED PACK settings={CoopMod.Settings.LinkedHealth}/{CoopMod.Settings.ShareCharms} runtime={runtime.Connected}/{runtime.Shared}/{runtime.ShareCharms}");
                yield break;
            }
            door.Invoke(null, new object[] { "Crossroads_01", "top1", 0f });
            yield return Until(() => AliveScene("Crossroads_01"), "real linked-health room ready");
            if (Has(role + ".failed")) yield break;
            Freeze();
            var origin = Scenes.GetActiveScene().handle;
            var health = PlayerData.instance.GetInt("health");
            bench.Invoke(null, null);
            ResourceCheck(Field<string>(runtime, "warpNotice").Contains("共享血量"), "linked-health restriction explains the blocked bench warp");
            yield return new WaitForSecondsRealtime(1);
            ResourceCheck(Scenes.GetActiveScene().handle == origin && AliveScene("Crossroads_01") && runtime.Connected &&
                PlayerData.instance.GetInt("health") == health, "blocked bench warp leaves scene, health and session intact");
            yield return ResourceBarrier("pack-linked", 20);
            yield break;
        }
        var enemyBars = ModHooks.GetMod("EnemyHPBar").GetType().Assembly;
        var barType = enemyBars.GetType("EnemyHPBar.HPBar", true);
        var maxField = barType.GetField("maxHP");
        var fillField = barType.GetField("health_bar");
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        var entities = Field<Dictionary<ulong, Entity>>(manager, "_entities");
        const string crossroads = "Crossroads_01";
        var pd = PlayerData.instance;
        pd.SetBool("hasMap", true); pd.SetBool("mapCrossroads", true); pd.SetBool("equippedCharm_2", true);
        GameManager.instance.gameMap.GetComponent<GameMap>().SetupMap();
        if (host) {
            door.Invoke(null, new object[] { crossroads, "top1", 0f });
            yield return Until(() => AliveScene(crossroads) && manager.IsSceneHost, "Benchwarp door and authoritative enemy room");
            if (Has(role + ".failed")) yield break;
            Freeze();
            Entity enemy = null;
            yield return Until(() => (enemy = entities.Values.FirstOrDefault(e => e.Object.Host != null &&
                e.Object.Host.activeInHierarchy && e.Object.Host.GetComponent<HealthManager>() is { hp: > 4 } &&
                e.Object.Host.GetComponent(barType) != null)) != null, "enemy with native HP bar");
            if (Has(role + ".failed")) yield break;
            var hm = enemy.Object.Host.GetComponent<HealthManager>();
            var initial = hm.hp;
            hm.ApplyExtraDamage(3);
            ResourceCheck(hm.hp > 0 && hm.hp < initial, "authority nonlethal damage");
            File.WriteAllText(Path.Combine(root, "pack.enemy"), $"{enemy.Id},{initial},{hm.hp}");
        } else {
            yield return Until(() => Has("pack.enemy"), "damage precedes guest room entry");
            if (Has(role + ".failed")) yield break;
            var parts = File.ReadAllText(Path.Combine(root, "pack.enemy")).Split(',');
            var id = ulong.Parse(parts[0]); var initial = int.Parse(parts[1]); var hp = int.Parse(parts[2]);
            door.Invoke(null, new object[] { crossroads, "top1", 0f });
            yield return Until(() => AliveScene(crossroads) && manager != null && engine.ClientManager.SceneSnapshotReady,
                "guest Benchwarp door and room snapshot");
            if (Has(role + ".failed")) yield break;
            Freeze();
            bool Filled() {
                if (!entities.TryGetValue(id, out var enemy) || enemy.Object.Client == null || !enemy.Object.Client.activeInHierarchy ||
                    enemy.Object.Host.activeInHierarchy || enemy.Object.Client.GetComponent<HealthManager>().hp != hp) return false;
                var bar = enemy.Object.Client.GetComponent(barType);
                return bar != null && (float)maxField.GetValue(bar) >= initial &&
                    fillField.GetValue(bar) is UnityEngine.UI.Image image && Mathf.Abs(image.fillAmount - hp / (float)maxField.GetValue(bar)) < .01f;
            }
            yield return Until(Filled, "late guest HP bar uses replicated health and correct denominator");
        }
        if (Has(role + ".failed")) yield break;
        yield return ResourceBarrier("pack-combat", 55);
        var initialScene = Scenes.GetActiveScene().handle;
        pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        if (!host) {
            bench.Invoke(null, null);
            // This second call arrives during Benchwarp's pre-load fade.
            bench.Invoke(null, null);
            ResourceCheck(Field<string>(runtime, "warpNotice").Contains("Benchwarp"), "second warp rejected in the fade window");
            yield return Until(() => Scenes.GetActiveScene().name == "Town" && Scenes.GetActiveScene().handle != initialScene &&
                runtime.Playable && pd.GetBool("atBench"), "real Benchwarp respawn at Dirtmouth bench");
            if (Has(role + ".failed")) yield break;
            ResourceCheck(runtime.Connected && Hkmp.CoopContext.Epoch == 0, "local bench warp preserves room session");
            var rest = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => rest.ActiveStateName == "Resting", "native Benchwarp bench resting");
            if (Has(role + ".failed")) yield break;
            rest.Fsm.Event(rest.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => AliveScene("Town") && !pd.GetBool("atBench"), "native bench get-up");
            if (Has(role + ".failed")) yield break;
        }
        yield return ResourceBarrier("pack-bench", 55);
        if (host) ResourceCheck(AliveScene(crossroads) && manager.IsSceneHost && runtime.Connected && Hkmp.CoopContext.Epoch == 0,
            "survivor room unchanged after guest bench warp");
        if (!host) yield return new WaitForSecondsRealtime(2);
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
            SceneName = "GG_Atrium", EntryGateName = "door_dreamEnter", WaitForSceneTransitionCameraFade = true
        });
        yield return Until(() => AliveScene("GG_Atrium"), "native Godhome atrium entrance");
        if (Has(role + ".failed")) yield break;
        Freeze();
        var map = GameManager.instance.gameMap.GetComponent<GameMap>();
        var area = map.transform.Find("GODS_GLORY");
        ResourceCheck(area != null && area.Find("GG_Atrium") != null, "AdditionalMaps Godhome layer built");
        yield return Until(() => service.TryLocation(out _), "Godhome position is available with map closed");
        if (Has(role + ".failed")) yield break;
        yield return ResourceBarrier("pack-map", 55);
        var mapChanger = ModHooks.GetMod("MapChangerMod").GetType().Assembly;
        var modeManager = mapChanger.GetType("MapChanger.ModeManager", true);
        if (!Field<Func<bool>>(service, "mapChangerEnabled")()) modeManager.GetMethod("ToggleModEnabled").Invoke(null, null);
        var currentMode = modeManager.GetMethod("CurrentMode");
        for (var attempt = 0; attempt < 4 && currentMode.Invoke(null, null).GetType().Name != "FullMapMode"; attempt++)
            modeManager.GetMethod("ToggleMode").Invoke(null, null);
        ResourceCheck(currentMode.Invoke(null, null).GetType().FullName == "VanillaMapMod.FullMapMode", "Vanilla Map Mod full-map mode selected");
        map.WorldMap();
        var pinType = ModHooks.GetMod("VanillaMapMod").GetType().Assembly.GetType("VanillaMapMod.Pins.VmmPin", true);
        yield return Until(() => map.GetComponentsInChildren(pinType, false).Length > 0, "Vanilla Map Mod item pins visible");
        if (Has(role + ".failed")) yield break;
        var quickMap = ModHooks.GetMod("Additional Maps").GetType().Assembly
            .GetType("AdditionalMaps.MonoBehaviours.GameMapHooks", true)
            .GetMethod("QuickMapNew", new[] { typeof(GameMap), typeof(string) });
        quickMap.Invoke(null, new object[] { map, "GODS_GLORY" });
        yield return Until(() => runtime.Peers.Keys.Any(id => map.transform.Find("HollowConnect teammate " + id) is Transform icon &&
            icon.gameObject.activeInHierarchy), "team icon visible on AdditionalMaps layer");
        if (Has(role + ".failed")) yield break;
        area.gameObject.SetActive(false);
        yield return null;
        ResourceCheck(!runtime.Peers.Keys.Any(id => map.transform.Find("HollowConnect teammate " + id) is Transform icon &&
            icon.gameObject.activeInHierarchy), "closed extra map layer has no misplaced teammate icons");
        map.CloseQuickMap();
        yield return null;
        ResourceCheck(!runtime.Peers.Keys.Any(id => map.transform.Find("HollowConnect teammate " + id) is Transform icon &&
            icon.gameObject.activeInHierarchy), "closing map hides team icons");
        yield return ResourceBarrier("pack-done", 20);
    }
}
