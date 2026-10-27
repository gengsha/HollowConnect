using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp;
using Hkmp.Animation.Effects;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using HollowConnect.Core;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private string LocalWorldScenario => Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_WORLD");
    private bool LocalWorldCase => !string.IsNullOrEmpty(LocalWorldScenario);
    private EntityManager LocalEntities => Field<EntityManager>(engine.ClientManager, "_entityManager");
    private Dictionary<ulong, Entity> LocalTable => Field<Dictionary<ulong, Entity>>(LocalEntities, "_entities");
    private void PrepareLocalWorld() {
        CoopMod.Statistics = new DeathStatistics();
        var pd = PlayerData.instance;
        pd.SetBool("hasDash", host); pd.SetBool("hasDoubleJump", false);
        pd.SetBool("gotCharm_20", false); pd.SetInt("charmsOwned", 0);
        pd.SetInt("charmSlots", host ? 3 : 4);
        pd.SetBool("defeatedDungDefender", LocalWorldScenario == "different-host" ? host : LocalWorldScenario == "different-guest" && !host);
        pd.SetBool("killedDungDefender", pd.GetBool("defeatedDungDefender"));
        CoopMod.Settings.Rescue = true;
    }
    private IEnumerator LocalWorldChecks() {
        var pd = PlayerData.instance;
        yield return Until(() => runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady, "local world peer ready");
        ResourceCheck(CoopContext.LocalWorldOnly && !runtime.Shared && !Field<bool>(engine.ClientManager, "_fullSynchronisation") &&
            !Field<bool>(engine.ClientManager, "_saveHooksRegistered"), "host mode wins over opposite guest preference and requested full save sync");
        ResourceCheck(pd.GetBool("hasDash") == host && pd.GetInt("charmSlots") == (host ? 3 : 4), "connection preserves personal ability and resource counts");
        ResourceCheck(CoopMod.Settings.Rescue, "local mode retains independent rescue setting");
        yield return ResourceBarrier("local-handshake");
        if (LocalWorldScenario.StartsWith("different-", StringComparison.Ordinal)) {
            yield return LocalDifferentProgress();
            yield break;
        }
        yield return LocalEnemyAndAttacks();
        yield return LocalNativePickup();
        yield return LocalNativeShard();
        yield return LocalReconnect();
    }
    private IEnumerator LocalEnemyAndAttacks() {
        yield return Room("Crossroads_01");
        ResourceCheck(LocalTable.Count == 0, "ordinary room registers no ordinary enemy or mechanism");
        var enemy = Object.FindObjectsOfType<HealthManager>().Where(h => h.gameObject.scene.name == "Crossroads_01" &&
            h.gameObject.activeInHierarchy && !h.isDead && h.hp > 0).OrderBy(h => EntityIdentity.Path(h.transform)).First();
        ResourceCheck(EntityReferences.Owner(enemy.gameObject) == null, "native ordinary enemy has no network owner");
        var remote = Field<PlayerManager>(engine.ClientManager, "_playerManager").GetPlayerObject(runtime.Peers.Single().Key);
        var effect = new Slash(); effect.SetServerSettings(new Hkmp.Game.Settings.ServerSettings()); effect.Play(remote, new bool[6]);
        var slash = remote.GetComponentsInChildren<Collider2D>(true).First(c => c.CompareTag("Nail Attack") && c.gameObject.LocateMyFSM("damages_enemy") != null);
        var before = enemy.hp;
        var hit = new HitInstance { Source = slash.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 1, Multiplier = 1, IgnoreInvulnerable = true };
        HitTaker.Hit(enemy.gameObject, hit, 3);
        typeof(HealthManager).GetMethod("TakeDamage", ResourcePrivate).Invoke(enemy, new object[] { hit });
        yield return new WaitForSecondsRealtime(.2f);
        ResourceCheck(enemy.hp == before && !enemy.isDead, "actual HKMP remote nail cannot hit or directly damage native ordinary enemy");
        // Exercise the native spell receiver with a real remote fireball clone.
        var spell = new VengefulSpirit(); spell.SetServerSettings(new Hkmp.Game.Settings.ServerSettings()); spell.Play(remote, new bool[6]);
        yield return null;
        var fireball = Object.FindObjectsOfType<Hkmp.Animation.Effects.Fireball>().Single(f => LocalWorldIsolation.IsRemote(f.gameObject));
        ResourceCheck(fireball != null, "real HKMP remote spell exists");
        var spellHit = hit; spellHit.Source = fireball.gameObject; spellHit.AttackType = AttackTypes.Spell;
        HitTaker.Hit(enemy.gameObject, spellHit, 3);
        ResourceCheck(enemy.hp == before && !enemy.isDead, "remote spell native damage receiver leaves private enemy unchanged");
        var localNail = HeroController.instance.gameObject.GetComponentsInChildren<Collider2D>(true).First(c => c.CompareTag("Nail Attack"));
        hit.Source = localNail.gameObject;
        enemy.Hit(hit);
        ResourceCheck(enemy.hp < before || enemy.isDead, "local real nail still damages native ordinary enemy");
        yield return ResourceBarrier("local-attacks");
        yield return Room("Waterways_05");
        var wall = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.name == "One Way Wall" && f.FsmName == "break_floor");
        ResourceCheck(EntityReferences.Owner(wall.gameObject) == null && !pdBool("dungDefenderWallBroken"), "permanent shortcut remains unbroken native local object");
        remote = Field<PlayerManager>(engine.ClientManager, "_playerManager").GetPlayerObject(runtime.Peers.Single().Key);
        effect.Play(remote, new bool[6]);
        slash = remote.GetComponentsInChildren<Collider2D>(true).First(c => c.CompareTag("Nail Attack") && c.gameObject.LocateMyFSM("damages_enemy") != null);
        var state = wall.ActiveStateName;
        var sourceBoss = LocalTable.Values.First(e => e.Type == EntityType.DungDefender);
        var sourceState = sourceBoss.GetClientFsms().First().FsmStates.First();
        var external = new SetFsmBool { gameObject = new HutongGames.PlayMaker.FsmOwnerDefault {
            OwnerOption = HutongGames.PlayMaker.OwnerDefaultOption.SpecifyGameObject, GameObject = wall.gameObject },
            fsmName = wall.FsmName, variableName = "Activated", setValue = true };
        external.Init(sourceState);
        Hkmp.Game.Client.Entity.Action.EntityFsmActions.ApplyNetworkDataFromAction(null, external);
        ResourceCheck(!wall.FsmVariables.GetFsmBool("Activated").Value, "network Boss action cannot write personal world mechanism variable");
        foreach (var f in wall.GetComponents<PlayMakerFSM>()) f.Fsm.OnTriggerEnter2D(slash);
        yield return new WaitForSecondsRealtime(.2f);
        ResourceCheck(wall.ActiveStateName == state && !pdBool("dungDefenderWallBroken"), "remote nail trigger cannot break private shortcut");
        ResourceCheck(wall.FsmStates.Single(s => s.Name == "Hit").Actions.OfType<BoolTest>().Any(), "native wall eligibility test survives local mode");
        if (host) {
            foreach (var offset in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down }) {
                if (pdBool("dungDefenderWallBroken")) break;
                HeroController.instance.transform.position = wall.transform.position + offset * 1.5f;
                HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
                yield return new WaitForSecondsRealtime(.3f);
                Mark("LOCAL WALL side=" + offset + " vars=" + string.Join(",", wall.FsmVariables.BoolVariables.Select(v => v.Name + "=" + v.Value)));
                for (var i = 0; i < 3 && !pdBool("dungDefenderWallBroken"); i++) {
                    if (offset.x > 0) HeroController.instance.FaceLeft(); else HeroController.instance.FaceRight();
                    HeroController.instance.Attack(GlobalEnums.AttackDirection.normal);
                    yield return new WaitForSecondsRealtime(.45f);
                    Mark("LOCAL WALL actual strike hits=" + wall.FsmVariables.GetFsmInt("Hits").Value + " state=" + wall.ActiveStateName);
                }
            }
            ResourceCheck(pdBool("dungDefenderWallBroken"), "local real nail trigger still breaks native shortcut");
            Flag("host.local.wall-broken");
        } else {
            yield return Until(() => Has("host.local.wall-broken"), "peer broke their local wall");
            ResourceCheck(!pdBool("dungDefenderWallBroken") && wall.FsmVariables.GetFsmInt("Hits").Value == 0,
                "peer wall break changes neither local flag nor local hit count");
        }
        yield return ResourceBarrier("local-wall");
    }
    private static bool pdBool(string key) => PlayerData.instance.GetBool(key);
    private IEnumerator LocalNativePickup() {
        yield return Room("Abyss_21");
        var pd = PlayerData.instance;
        var pickup = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == "Abyss_21" &&
            f.FsmStates.SelectMany(s => s.Actions).OfType<SetPlayerDataBool>().Any(a => a.boolName.Value == "hasDoubleJump"));
        ResourceCheck(!pd.GetBool("hasDoubleJump") && pickup.gameObject.activeInHierarchy, "uncollected wings visible in each personal world");
        foreach (var collector in new[] { "host", "guest" }) {
            if (role == collector) {
                HeroController.instance.AffectedByGravity(true);
                HeroController.instance.transform.position = pickup.transform.position;
                HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
                yield return Until(() => HeroController.instance.controlReqlinquished || !HeroController.instance.acceptingInput, "native wings cutscene takes local control");
                var end = Time.realtimeSinceStartup + 45;
                while (pickup != null && pickup.ActiveStateName != "Return Control" && Time.realtimeSinceStartup < end) {
                    if (pickup.ActiveStateName == "Msg") yield return ResourcePress(InputHandler.Instance.inputActions.jump);
                    yield return new WaitForSecondsRealtime(.2f);
                }
                yield return Until(() => pd.GetBool("hasDoubleJump") && HeroController.instance.acceptingInput &&
                    !HeroController.instance.controlReqlinquished && !pd.GetBool("disablePause"), "private wings granted and original cutscene restores control", 3);
                Freeze(); Flag(collector + ".local.wings");
            } else {
                yield return Until(() => Has(collector + ".local.wings"), "peer completed original wings pickup", 65);
                ResourceCheck(pd.GetBool("hasDoubleJump") == (collector == "guest") && (collector == "guest" || pickup.gameObject.activeInHierarchy),
                    "peer pickup neither grants ability nor removes own local pickup");
            }
            yield return ResourceBarrier("local-wings-" + collector);
        }
    }
    private IEnumerator LocalDifferentProgress() {
        var first = Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOCAL_FIRST") ?? "host";
        if (role != first) yield return Until(() => Has(first + ".local.different-entry"), "requested authority enters first");
        Enter("Waterways_05");
        yield return Until(() => AliveScene("Waterways_05") && engine.ClientManager.SceneSnapshotReady && LocalEntities.IsSceneHostDetermined,
            "different-progress native room ready"); Freeze(); Flag(role + ".local.different-entry");
        yield return ResourceBarrier("local-different-entry");
        yield return new WaitForSecondsRealtime(3);
        var complete = LocalWorldScenario == "different-host" ? host : !host;
        ResourceCheck(pdBool("defeatedDungDefender") == complete, "authority layout cannot overwrite personal boss defeat flag");
        var live = Object.FindObjectsOfType<HealthManager>(true).Where(h => h.gameObject.scene.name.StartsWith("Waterways_05") && h.name == "Dung Defender").ToArray();
        if (complete) ResourceCheck(live.Length == 0, "completed personal world stays completed");
        else {
            ResourceCheck(live.Length == 1, "unfinished local world retains its original dormant boss before local trigger");
            ResourceCheck(LocalEntities.IsSceneHost || EntityReferences.Owner(live[0].gameObject) == null,
                "unmatched private boss restored from replica control");
            var battle = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "Battle Scene" && f.FsmName == "Battle Scene");
            HeroController.instance.transform.position = new Vector3(56, 12.5f, HeroController.instance.transform.position.z);
            HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(.3f);
            HeroController.instance.transform.position = battle.GetComponent<Collider2D>().bounds.center; Physics2D.SyncTransforms();
            yield return Until(() => battle.ActiveStateName == "Dung Start", "private native battle trigger reaches Dung Start", 12);
            yield return Until(() => live[0].gameObject.activeInHierarchy &&
                live[0].gameObject.LocateMyFSM("Dung Defender").ActiveStateName != "Sleep",
                "private native boss activates and starts fighting", 12);
            var boss = Object.FindObjectsOfType<HealthManager>().Single(h => h.name == "Dung Defender");
            boss.ApplyExtraDamage(boss.hp + 1);
            yield return Until(() => pdBool("defeatedDungDefender") && HeroController.instance.acceptingInput,
                "private unmatched boss native death and control complete", 25);
        }
        yield return ResourceBarrier("local-different-complete", 50);
    }
    private IEnumerator LocalNativeShard() {
        yield return Room("Crossroads_13");
        var pickup = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == "Crossroads_13" &&
            f.name == "Heart Piece" && f.FsmName == "Heart Container Control");
        ResourceCheck(pickup.gameObject.activeInHierarchy && PlayerData.instance.GetInt("heartPieces") == 0, "personal mask shard starts present and unclaimed");
        foreach (var collector in new[] { "host", "guest" }) {
            if (role == collector) {
                HeroController.instance.transform.position = pickup.transform.position;
                HeroController.instance.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
                yield return Until(() => PlayerData.instance.GetInt("heartPieces") == 1, "native personal shard collision grants exactly one fragment");
                yield return ResourceIdle("local mask shard", 25);
                Freeze(); Flag(collector + ".local.shard");
            } else {
                yield return Until(() => Has(collector + ".local.shard"), "peer completed personal shard pickup");
                ResourceCheck(PlayerData.instance.GetInt("heartPieces") == (collector == "guest" ? 1 : 0) &&
                    (collector == "guest" || pickup.gameObject.activeInHierarchy), "peer fragment never removes or awards personal shard");
            }
            yield return ResourceBarrier("local-shard-" + collector);
        }
    }
    private IEnumerator LocalReconnect() {
        yield return ResourceBarrier("local-before-disconnect");
        engine.ClientManager.Disconnect();
        yield return Until(() => !runtime.Connected, "actual local-world disconnect");
        ResourceCheck(!CoopContext.LocalWorldOnly, "disconnect clears local-world mode after hook cleanup");
        yield return ResourceBarrier("local-offline");
        typeof(ClientManager).GetField("_autoConnect", ResourcePrivate).SetValue(engine.ClientManager, true);
        typeof(ClientManager).GetMethod("Connect", ResourcePrivate).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "local" + role });
        yield return Until(() => runtime.Connected && runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady,
            "actual local-world reconnect");
        ResourceCheck(CoopContext.LocalWorldOnly && !runtime.Shared && pdBool("hasDoubleJump") && pdBool("hasDash") == host,
            "reconnect keeps personal collected progress and host local mode");
        yield return ResourceBarrier("local-rejoined");
        engine.ClientManager.Disconnect();
        yield return Until(() => !runtime.Connected, "disconnect before changing server mode");
        yield return ResourceBarrier("local-switch-offline");
        CoopMod.Settings.ShareProgress = host;
        if (host) {
            yield return Until(() => engine.ServerManager.Players.Count == 0, "server accepts both disconnects before mode change");
            engine.ServerManager.Stop(); engine.ServerManager.Start(29650, false); Flag("local.shared-server");
        } else yield return Until(() => Has("local.shared-server"), "host reopened shared world");
        typeof(ClientManager).GetField("_autoConnect", ResourcePrivate).SetValue(engine.ClientManager, true);
        typeof(ClientManager).GetMethod("Connect", ResourcePrivate).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "shared" + role });
        yield return Until(() => runtime.Connected && runtime.Shared && runtime.Peers.Count == 1 && engine.ClientManager.SceneSnapshotReady,
            "shared-world handshake replaces previous local-world session");
        ResourceCheck(!CoopContext.LocalWorldOnly, "new host shared mode wins over opposite guest preference");
        yield return Room("Crossroads_01");
        ResourceCheck(LocalTable.Values.Any(e => e.Type == EntityType.Crawlid), "switching back restores ordinary network enemy registration");
        yield return ResourceBarrier("local-switch-complete");
    }
}
