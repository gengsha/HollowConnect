using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// The seven story warriors share this native NPC template. Synchronize battle
// participation and defeat, while leaving the 1 -> 2 essence claim private.
internal sealed class DreamWarriorSynchronizer {
    internal static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal) {
        ["RestingGrounds_02"] = "xeroDefeated", ["Cliffs_02"] = "aladarSlugDefeated",
        ["Fungus2_32"] = "elderHuDefeated", ["Fungus3_40"] = "mumCaterpillarDefeated",
        ["Fungus1_35"] = "noEyesDefeated", ["Deepnest_40"] = "galienDefeated",
        ["Deepnest_East_10"] = "markothDefeated"
    };
    private readonly NetClient client;
    private readonly EntityManager entities;
    private PlayMakerFSM conversation;
    private GameObject collectedNpc;
    private byte desired, applied, offered;
    private bool applying;
    private float next;
    internal DreamWarriorSynchronizer(NetClient client, EntityManager entities) { this.client = client; this.entities = entities; }
    internal void Register() => On.HutongGames.PlayMaker.Fsm.EnterState += Enter;
    internal void Destroy() => On.HutongGames.PlayMaker.Fsm.EnterState -= Enter;
    internal void Clear() { conversation = null; collectedNpc = null; desired = applied = offered = 0; applying = false; next = 0; }
    private static bool Owns(string scene) => scene == CoopContext.LocalScene || scene == CoopContext.LocalScene + "_boss" || scene == CoopContext.LocalScene + "_boss_defeated";
    private void Enter(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, HutongGames.PlayMaker.Fsm fsm, FsmState state) {
        var npc = client.IsConnected && !CoopContext.Resetting &&
            fsm.GameObject.name == "Ghost Warrior NPC" && Owns(fsm.GameObject.scene.name) && Keys.ContainsKey(CoopContext.LocalScene);
        var warrior = npc && fsm.Name == "Conversation Control";
        // A previously collected NPC exits Init before constructing its text
        // keys and object references. It must stay inert even when the shared
        // boss broadcasts GHOST DEFEAT. Do not interrupt a new native claim's
        // vanish/reward animation when its own PlayerData changes to 2.
        if (warrior && (state.Name == "Init" || state.Name == "Inert") &&
            PlayerData.instance.GetInt(Keys[CoopContext.LocalScene]) >= 2) collectedNpc = fsm.GameObject;
        if (npc && collectedNpc == fsm.GameObject && !entities.IsPrivateBossScene(fsm.GameObject.scene.name) &&
            (!CoopContext.LocalWorldOnly || entities.HasSharedBoss)) {
            // SwitchState assigned ActiveState before calling this hook.
            // Switch again through PlayMaker instead of just replacing the
            // OnEnter argument, which leaves the old state's transitions live.
            if (warrior && state.Name != "Init" && state.Name != "Pause" && state.Name != "Inert") {
                fsm.SetState("Inert"); return;
            }
            if (fsm.Name == "Appear" && state.Name == "Reappear") {
                fsm.SetState("Fighting"); return;
            }
        }
        // The participant runs native presentation, but only the scene
        // authority may activate the original warrior and its AI.
        var activation = warrior && !entities.IsPrivateBossScene(fsm.GameObject.scene.name) && state.Name == "Start Fight" && entities.IsSceneHostDetermined && !entities.IsSceneHost
            ? state.Actions.OfType<ActivateAllChildren>().FirstOrDefault() : null;
        var enabled = activation?.Enabled ?? false;
        if (activation != null) activation.Enabled = false;
        try { orig(fsm, state); }
        finally { if (activation != null) activation.Enabled = enabled; }
        if (applying || !warrior || state.Name != "Ready") return;
        if (CoopContext.LocalWorldOnly && !entities.HasSharedBoss) return;
        desired = System.Math.Max(desired, (byte)1);
        applied = System.Math.Max(applied, (byte)1);
        Send(1);
    }
    private void Send(byte stage) {
        if (stage <= offered || !entities.IsSceneHostDetermined || !Keys.TryGetValue(CoopContext.LocalScene, out var key)) return;
        if (stage == 2 && offered == 1 && entities.IsSceneHost) CoopContext.BossDefeated?.Invoke(0);
        var data = new EntityNetworkData { Type = EntityComponentType.DreamWarrior };
        data.Packet.Write(key); data.Packet.Write(stage);
        client.UpdateManager.AddEntityData(0UL, data);
        Hkmp.Logging.Logger.Info($"Dream warrior sent: {CoopContext.LocalScene}, stage={stage}");
        offered = stage;
    }
    internal void Receive(EntityNetworkData data) {
        var key = data.Packet.ReadString(); var stage = data.Packet.ReadByte();
        if (!Keys.TryGetValue(CoopContext.LocalScene, out var expected) || key != expected || stage < 1 || stage > 2) return;
        // A peer's old completed save is not a completion of this encounter.
        if (CoopContext.LocalWorldOnly && (!entities.HasSharedBoss || stage == 2 && applied < 1 && desired < 1 && offered < 1)) return;
        desired = System.Math.Max(desired, stage);
        Hkmp.Logging.Logger.Info($"Dream warrior received: {CoopContext.LocalScene}, stage={stage}");
    }
    internal void Tick() {
        if (Time.unscaledTime < next || !Keys.TryGetValue(CoopContext.LocalScene, out var key)) return;
        next = Time.unscaledTime + .25f;
        if (conversation == null) {
            conversation = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>(true).FirstOrDefault(f =>
                Owns(f.gameObject.scene.name) && f.name == "Ghost Warrior NPC" && f.FsmName == "Conversation Control" &&
                f.FsmVariables.GetFsmString("PD Int Name")?.Value == key);
            if (conversation != null && conversation.ActiveStateName == "Inert" && PlayerData.instance.GetInt(key) >= 2)
                collectedNpc = conversation.gameObject;
        }
        if (conversation == null) return;
        if (CoopContext.LocalWorldOnly && !entities.HasSharedBoss) return;
        var appearance = conversation.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Appear");
        if (entities.IsSceneHost && (!CoopContext.LocalWorldOnly || applied >= 1) && PlayerData.instance.GetInt(key) >= 1 &&
            (desired == 0 || desired == 2 || conversation.ActiveStateName == "Idle" && appearance?.ActiveStateName == "Reappear")) {
            desired = 2; Send(2);
        } else if (desired == 1) Send(1);
        if (desired <= applied || !(CoopContext.BossTransitionReady?.Invoke() ?? true)) return;
        if (desired == 2) {
            // Never copy the summoner's "collected" value or essence balance.
            if (PlayerData.instance.GetInt(key) < 1) PlayerData.instance.SetInt(key, 1);
            if (PlayerData.instance.GetInt(key) == 1) {
                var appear = conversation.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Appear");
                if (appear != null && appear.ActiveStateName != "Reappear") appear.SendEvent("GHOST DEFEAT");
                if (conversation.ActiveStateName == "Start Fight" || conversation.ActiveStateName == "Ready" || conversation.ActiveStateName == "Music")
                    conversation.SendEvent("GHOST DEFEAT");
            }
            applied = 2;
        } else {
            // Entity snapshots still supply the live shared warrior. A player
            // who already claimed it needs no NPC challenge/reward playback.
            if (collectedNpc == conversation.gameObject) { applied = 1; return; }
            var hero = HeroController.instance;
            if (hero == null || !hero.acceptingInput || hero.controlReqlinquished || global::GameManager.instance.isPaused) return;
            applying = true;
            try { conversation.SetState("Ready"); applied = 1; }
            finally { applying = false; }
        }
    }
}
