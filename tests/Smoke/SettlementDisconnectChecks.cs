using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Api.Server;
using Hkmp.Api.Server.Networking;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Server;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using HollowConnect;
using HollowConnect.Core;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    // Interposes only the enqueue boundary. Every accepted send still goes
    // through the real sender, per-client queue and DTLS transport.
    private sealed class DisconnectBoundarySender : IServerAddonNetworkSender<Channel> {
        internal IServerAddonNetworkSender<Channel> Inner;
        internal Action<ushort, Message> Before;
        public void SendCollectionData<T>(Channel c, T data, ushort id) where T : IPacketData, new() {
            Before?.Invoke(id, (Message)(object)data); Inner.SendCollectionData(c, data, id);
        }
        public void SendCollectionData<T>(Channel c, T data, params ushort[] ids) where T : IPacketData, new() {
            foreach (var id in ids) SendCollectionData(c, data, id);
        }
        public void BroadcastCollectionData<T>(Channel c, T data) where T : IPacketData, new() => Inner.BroadcastCollectionData(c, data);
        public void SendSingleData(Channel c, IPacketData data, ushort id) => Inner.SendSingleData(c, data, id);
        public void SendSingleData(Channel c, IPacketData data, params ushort[] ids) => Inner.SendSingleData(c, data, ids);
        public void BroadcastSingleData(Channel c, IPacketData data) => Inner.BroadcastSingleData(c, data);
    }

    private void DisconnectClientSend(Message message) => typeof(CoopRuntime).GetMethod("Send", BindingFlags.Instance | BindingFlags.NonPublic)
        .Invoke(runtime, new object[] { message });

    private IEnumerator SettlementDisconnectChecks() {
        using var noContact = IgnoreExternalCombatDamage();
        var roles = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLES").Split(',');
        var survivors = roles.Where(r => r != "guest").ToArray();
        var damage = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SETTLEMENT_DISCONNECT") == "damage";
        bool All(string suffix) => roles.All(r => Has(r + suffix));
        bool Survivors(string suffix) => survivors.All(r => Has(r + suffix));
        yield return Until(() => All(".connected") && runtime.Peers.Count == roles.Length - 1, "full targeted real multiplayer roster");
        File.WriteAllText(Path.Combine(root, role + ".sd.id"), runtime.LocalId.ToString());
        Flag(role + ".sd.identity"); yield return Until(() => All(".sd.identity"), "all role identities recorded");
        var guestId = ushort.Parse(File.ReadAllText(Path.Combine(root, "guest.sd.id")));
        var hostId = ushort.Parse(File.ReadAllText(Path.Combine(root, "host.sd.id")));
        var rosterReceived = 0; var readyReceived = false; var wipeReceived = false; var releaseReceived = false;
        using var receiveTrace = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, owner, message) => {
                orig(owner, message);
                if (message.Kind == Kind.Roster) rosterReceived++;
                if (message.Kind == Kind.BossSettlementReady && message.Target == owner.LocalId &&
                    message.Serial == Field<uint>(owner, "settlementToken") && message.Aux == Field<uint>(owner, "settlementVisit")) {
                    readyReceived = true; Log("RECEIPT actual Ready token=" + message.Serial + " visit=" + message.Aux); Flag(role + ".sd.ready");
                }
                if (message.Kind == Kind.Wipe) wipeReceived = true;
                if (message.Kind == Kind.Release) releaseReceived = true;
            }));
        CoopServerAddon addon = null; DisconnectBoundarySender proxy = null;
        uint finalBoard = 0, finalRevision = 0;
        if (host) {
            var addonManager = typeof(ServerManager).GetField("AddonManager", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine.ServerManager);
            addon = Field<Dictionary<(string, string), ServerAddon>>(addonManager, "_networkedAddons").Values.OfType<CoopServerAddon>().Single();
            proxy = new DisconnectBoundarySender { Inner = Field<IServerAddonNetworkSender<Channel>>(addon, "sender") };
            typeof(CoopServerAddon).GetField("sender", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(addon, proxy);
            proxy.Before = (id, message) => {
                if (Has("sd.retired") || id != guestId) return;
                var final = damage && message.Kind == Kind.DamageSnapshot && JsonConvert.DeserializeObject<DamageRankingSnapshot>(message.BossPlan).Finished;
                if (!(damage ? final : message.Kind == Kind.BossSettlementReady)) return;
                if (final) { finalBoard = message.Serial; finalRevision = message.Aux; }
                var party = Field<Party>(addon, "party");
                Check(party.Members.ContainsKey(id) && engine.ServerManager.IsPlayerConnected(id), "precondition: real connected recipient and roster before enqueue");
                engine.ServerManager.DisconnectPlayer(id, DisconnectReason.Kicked);
                Check(!engine.ServerManager.IsPlayerConnected(id) && party.Members.ContainsKey(id), "race: transport retired while addon Leave remains pending");
                Log("INJECTION after Connected before real sender kind=" + message.Kind + " id=" + id + " token=" + message.Serial + " revision=" + message.Aux);
                Flag("sd.retired");
                // The real sender now performs its own lookup and throws the
                // genuine disconnected-recipient error into production Send.
            };
        }
        try {
            BossSequenceController.Reset(); PlayerData.instance.SetInt("bossStatueTargetLevel", 0);
            PlayerData.instance.SetString("dreamReturnScene", "Town"); PlayerData.instance.SetString("dreamReturnGate", "left1");
            if (!host) yield return Until(() => Has("sd.first"), "authority enters native Godhome arena first");
            yield return RuleEnter("GG_Gruz_Mother", "door_dreamEnter");
            var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
            yield return Until(() => manager.IsSceneHostDetermined && manager.IsSceneHost == host &&
                EntityReferences.Entities.Values.Any(e => e.Type == EntityType.GruzMother && e.Object.Host != null), "native Gruz entity and authority preflight");
            var boss = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.GruzMother);
            var health = boss.Object.Host.GetComponent<HealthManager>();
            var hero = HeroController.instance; hero.takeNoDamage = true; hero.AffectedByGravity(true);
            yield return Until(() => hero.cState.onGround && hero.acceptingInput && !hero.cState.transitioning, "native Godhome entrance and supported hero");
            Log("PREFLIGHT boss=" + health.name + " hp=" + health.hp + " invincible=" + health.IsInvincible +
                " fsms=" + string.Join(",", health.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.ActiveStateName)));
            if (host) Flag("sd.first");
            Flag(role + ".sd.arena"); yield return Until(() => All(".sd.arena"), "all clients inside native arena");
            Check(runtime.Shared == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_SETTLEMENT_SHARED") == "1"), "shared-progress setting negotiated");
            if (damage) yield return Until(() => runtime.damageRanking?.Players.Length == roles.Length, "real damage board includes all clients");
            yield return new WaitForSecondsRealtime(2);
            if (host) {
                yield return Until(() => !health.IsInvincible && health.hp > 0, "native boss accepts health damage without bypassing intro");
                health.Hit(new HitInstance { Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = 9, Multiplier = 1, IgnoreInvulnerable = false });
                if (damage) yield return Until(() => runtime.damageRanking?.Total > 0, "accepted native hit reaches real damage board");
                health.ApplyExtraDamage(Math.Max(1, health.hp));
                Log("NATIVE terminal health damage applied"); Flag("sd.killed");
            }
            yield return Until(() => Has("sd.retired"), "native terminal reports reach selected real send boundary", 35);
            if (role == "guest") {
                engine.ClientManager.Disconnect();
                yield return Until(() => !runtime.Connected, "retired client observes local disconnect");
                Flag(role + ".sd.offline");
                yield return Until(() => Survivors(".sd.complete"), "surviving clients finish after deliberate disconnect", 160);
                yield break;
            }
            yield return Until(() => readyReceived && Has("guest.sd.offline"), "survivor receives actual Ready instead of timeout fallback", 12);
            if (damage) {
                yield return Until(() => runtime.damageRanking?.Finished == true && runtime.damageRanking.Completed && runtime.damageRanking.Total > 0,
                    "survivor receives actual completed damage snapshot", 8);
                Log("DAMAGE final board=" + runtime.damageRanking.Board + " revision=" + runtime.damageRanking.Revision + " total=" + runtime.damageRanking.Total);
                if (host) {
                    var revisions = Field<Dictionary<uint, uint>>(addon, "sentDamageRevisions");
                    yield return Until(() => revisions.TryGetValue(finalBoard, out var revision) && revision >= finalRevision,
                        "failed final damage enqueue retries and records revision after Leave");
                }
            }
            var beforeRoster = rosterReceived;
            yield return Until(() => rosterReceived >= beforeRoster + 3 && !runtime.Peers.ContainsKey(guestId), "fresh roster continues and departed peer is removed");
            if (host) {
                Check(Field<Dictionary<uint, (string, Dictionary<ushort, uint>, double)>>(addon, "bossSettlements").Count == 0, "settlement queue has no persistent poison item");
                Check(!Field<Party>(addon, "party").Members.ContainsKey(guestId), "pending addon Leave processed");
            }
            yield return Until(() => runtime.Playable && !GameManager.instance.IsInSceneTransition, "native victory transition settles");
            if (GameManager.instance.sceneName != "Town") yield return RuleEnter("Town");
            yield return RuleBenchUp();
            hero = HeroController.instance;
            hero.transform.position = StoryFloor(new Vector3(20 + Array.IndexOf(survivors, role), 12, 0), "Town");
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return Until(() => hero.cState.onGround && hero.acceptingInput, "supported personal checkpoint scene");
            Flag(role + ".sd.town"); yield return Until(() => Survivors(".sd.town"), "all survivors regroup before rescue and wipe");
            if (survivors.Length == 2) {
                if (host) { Die(); Flag("sd.down.requested"); }
                else {
                    yield return Until(() => runtime.Peers.TryGetValue(hostId, out var peer) && peer.Life == Life.Down, "surviving peer receives real Down after retirement");
                    Check(runtime.RequestShadeHit(hostId, runtime.Peers[hostId].DownSerial), "survivor submits real rescue request over transport");
                }
                yield return Until(() => !runtime.Down && (host ? Has("sd.down.requested") : runtime.Peers[hostId].Life == Life.Alive), "real Down and rescue messages still complete");
                if (host) Check(runtime.RescuesUsed == 1, "rescue completed rather than merely observing initial Alive");
                Flag(role + ".sd.rescued"); yield return Until(() => Survivors(".sd.rescued"), "both survivors observe accepted rescue");
            }
            var round = CoopContext.Epoch;
            Die();
            yield return Until(() => wipeReceived && releaseReceived && runtime.ReleasedRound > round && !runtime.Down && !runtime.Resetting && runtime.Playable,
                "actual survivor deaths drive native wipe recovery and Release", 65);
            Log("RETURN checkpoint scene=" + GameManager.instance.sceneName + " atBench=" + PlayerData.instance.GetBool("atBench") +
                " input=" + HeroController.instance.acceptingInput + " nativeLock=" + HeroController.instance.controlReqlinquished);
            // Release only acknowledges all scene arrivals. The native bench
            // FSM may still own control until its normal get-up interaction.
            yield return RuleBenchUp();
            yield return Until(() => HeroController.instance.acceptingInput && !HeroController.instance.controlReqlinquished &&
                !GameManager.instance.IsInSceneTransition, "native control restored after wipe and bench get-up", 15);
            Log("POSTCHECK roster=" + rosterReceived + " releaseRound=" + runtime.ReleasedRound);
            Flag(role + ".sd.complete"); yield return Until(() => Survivors(".sd.complete"), "all surviving endpoints complete");
        } finally {
            if (host && proxy != null) typeof(CoopServerAddon).GetField("sender", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(addon, proxy.Inner);
        }
    }
}
