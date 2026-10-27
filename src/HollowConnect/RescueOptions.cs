using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp;
using Hkmp.Game.Client.Entity;
using HollowConnect.Core;
using UnityEngine;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    internal bool Spectating { get; private set; }
    internal bool SpectateAfterExhaustion;
    internal int RescueLimit, RescuesUsed;
    private uint restSerial, rescueStatusSerial, rescueStatusRest;
    private uint localDownLifeVersion;
    private bool rescueStatusInitialized;
    private double rescueCountUntil;
    private bool benchOccupied;
    private bool encounterEnded;
    private readonly PlayerVisualHide spectatorRenderers = new();
    private readonly Dictionary<ushort, PlayerVisualHide> spectatorPeers = new();
    internal bool RescueAvailable => RescueEnabled && (RescueLimit <= 0 || RescuesUsed < RescueLimit);

    internal bool InBossEncounter() {
        var scene = CoopContext.LocalScene;
        var controller = BossSceneController.Instance;
        if (controller != null && (controller.gameObject.scene.name == scene || controller.gameObject.scene.name.StartsWith(scene + "_boss", StringComparison.Ordinal))) return true;
        // Failed Champion's story body currently lacks an entity registration.
        // Its native dream arena still participates in death/spectator rules.
        if (scene == "Dream_01_False_Knight") return true;
        return EntityReferences.Entities != null && EntityReferences.Entities.Values.Any(e =>
            // Story bosses such as Traitor Lord belong to a native additive
            // <room>_boss scene. They use the same rescue room as its parent.
            (e.SourceScene == scene || e.SourceScene.StartsWith(scene + "_boss", StringComparison.Ordinal)) &&
            BossDefeatSignals.IsBoss(e.Type) && e.Object.Host != null &&
            e.Object.Host.GetComponent<HealthManager>() is { isDead: false, hp: > 0 });
    }
    private void ResetRescueOptions() {
        StopSpectating(); RescuesUsed = 0;
        encounterEnded = false;
        RestoreSpectatorPeers();
        rescueStatusSerial = rescueStatusRest = restSerial = 0;
        localDownLifeVersion = 0; rescueStatusInitialized = false; rescueCountUntil = 0;
        benchOccupied = PlayerData.instance != null && PlayerData.instance.GetBool("atBench");
    }
    private void ReceiveRescueStatus(Message message) {
        if (message.Id != LocalId || message.Serial < rescueStatusSerial || message.Aux < rescueStatusRest) return;
        if (rescueStatusInitialized && message.Aux == rescueStatusRest && message.Value > RescuesUsed)
            rescueCountUntil = Now + 4.5;
        RescuesUsed = message.Aux > rescueStatusRest ? Math.Max(0, message.Value) : Math.Max(RescuesUsed, message.Value);
        rescueStatusInitialized = true;
        rescueStatusSerial = message.Serial; rescueStatusRest = message.Aux;
    }
    private void TickRescueOptions() {
        var occupied = PlayerData.instance != null && PlayerData.instance.GetBool("atBench");
        // Observe this edge even during native respawn: returning to a bench
        // alone must not refill the rescue budget.
        if (occupied && !benchOccupied && Playable && !Down && !Returning && !Resetting && !changingScene && personalDeath == null)
            Send(new Message { Kind = Kind.BenchRest, Serial = ++restSerial, Scene = CoopContext.LocalScene });
        benchOccupied = occupied;
        if (Spectating && Down) HideSpectator();
        foreach (var id in spectatorPeers.Keys.ToArray())
            if (!Peers.TryGetValue(id, out var peer) || peer.Life != Life.Spectating || peer.State?.Scene != CoopContext.LocalScene)
                RestoreSpectatorPeer(id);
        foreach (var pair in Peers) {
            if (pair.Value.Life != Life.Spectating || pair.Value.State?.Scene != CoopContext.LocalScene) continue;
            // Username is a sibling of PlayerObject under PlayerContainer.
            var avatar = api.ClientManager.GetPlayer(pair.Key)?.PlayerContainer;
            if (avatar == null) {
                if (spectatorPeers.ContainsKey(pair.Key)) RestoreSpectatorPeer(pair.Key);
                continue;
            }
            if (!spectatorPeers.TryGetValue(pair.Key, out var hidden)) spectatorPeers[pair.Key] = hidden = new PlayerVisualHide();
            hidden.Hide(avatar);
        }
    }
    private void RestoreSpectatorPeer(ushort id) {
        spectatorPeers[id].Restore();
        spectatorPeers.Remove(id);
    }
    private void RestoreSpectatorPeers() {
        foreach (var hidden in spectatorPeers.Values) hidden.Restore();
        spectatorPeers.Clear();
    }
    private void HideSpectator() {
        spectatorRenderers.Hide(HeroController.instance == null ? null : HeroController.instance.gameObject);
    }
    private void StopSpectating() {
        Spectating = false;
        spectatorRenderers.Restore();
    }
    private Vector3 SpectatorPosition() {
        var target = Peers.OrderBy(p => p.Key == Selected ? 0 : 1).ThenBy(p => p.Key).FirstOrDefault(p =>
            p.Value.State != null && p.Value.State.Scene == deathScene &&
            (p.Value.Life == Life.Alive || p.Value.Life == Life.Loading) && Now >= p.Value.Seen && Now - p.Value.Seen < 2 &&
            PlayerObject(p.Key) is { activeInHierarchy: true });
        if (target.Value == null) return DeathPosition;
        var avatar = PlayerObject(target.Key);
        return avatar != null ? avatar.transform.position : new Vector3(target.Value.State.X, target.Value.State.Y, DeathPosition.z);
    }
    private void RequestGiveUp() {
        // Automatic unsafe-anchor returns still call RequestPersonalReturn.
        // Only the player's voluntary action can choose spectating here.
        if (!SpectateAfterExhaustion || Spectating) { RequestPersonalReturn(); return; }
        giveUpRequested = true;
        Send(new Message { Kind = Kind.SpectateRequest, Serial = downSerial, Scene = deathScene });
        Notice("正在放弃救援；无同场队友时返回自己的复活点…");
    }
    private void ReceiveSpectating(Message message) {
        if (Resetting || message.Serial == 0) return;
        if (message.Id == LocalId) {
            if (!Down || Returning || message.Serial != downSerial || message.Scene != deathScene || message.Aux <= localDownLifeVersion) return;
            localDownLifeVersion = message.Aux; giveUpRequested = false;
            Spectating = true; shades.Remove(LocalId); HideSpectator();
            Notice("正在观战 · Boss击败后在存活队友身边复活");
        } else if (Peers.TryGetValue(message.Id, out var peer) && message.Serial == peer.DownSerial && message.Aux > peer.LifeVersion) {
            peer.Life = Life.Spectating; peer.LifeVersion = message.Aux; shades.Remove(message.Id);
            if (peer.State != null) peer.State.Value = (int)Life.Spectating;
        }
    }
    private string RescueRemainingText => !RescueEnabled ? "暗影救援已关闭" : RescueLimit <= 0 ? "救援次数不限" :
        "剩余可被救起 " + Math.Max(0, RescueLimit - RescuesUsed) + "/" + RescueLimit + " 次 · 坐椅子恢复";
}
