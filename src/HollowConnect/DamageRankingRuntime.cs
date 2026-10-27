using System;
using System.Linq;
using Hkmp;
using Hkmp.Animation;
using Hkmp.Game.Client.Entity;
using HollowConnect.Core;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    internal bool damageRankingEnabled;
    internal DamageRankingSnapshot damageRanking;
    internal bool damageRankingHidden;
    private uint damageReportSerial, damageAttempt, damageBeginVisit;
    private string damageSequence = "", damageBeginScene = "";
    private bool damageBeginSent, damageEndSent;
    private bool damageBeginConfirmed;
    private float nextDamageBegin;
    private DamageRankingView damageView;
    private void RegisterDamageRanking() => On.BossSequenceController.SetupNewSequence += StartDamageSequence;
    private void UnregisterDamageRanking() {
        On.BossSequenceController.SetupNewSequence -= StartDamageSequence;
        damageView?.Dispose(); damageView = null;
    }
    private void StartDamageSequence(On.BossSequenceController.orig_SetupNewSequence orig, BossSequence sequence,
        BossSequenceController.ChallengeBindings bindings, string playerData) {
        orig(sequence, bindings, playerData);
        damageAttempt++; damageBeginSent = false; damageSequence = sequence.name;
    }
    private void ResetDamageRanking() {
        damageRankingEnabled = false; damageRanking = null; damageRankingHidden = false;
        damageReportSerial = damageAttempt = damageBeginVisit = 0;
        damageSequence = damageBeginScene = ""; damageBeginSent = damageEndSent = damageBeginConfirmed = false;
        nextDamageBegin = 0;
        damageView?.Dispose(); damageView = null;
    }
    private string DamageSequence() => BossSequenceController.IsInSequence ? BossTravel.CurrentSequenceName : "";
    private Message DamageMessage(Kind kind) => new() {
        Kind = kind, Scene = CoopContext.LocalScene, Aux = CoopContext.LocalVisit,
        BossSequence = DamageSequence(), BossIndex = BossSequenceController.IsInSequence ? BossSequenceController.BossIndex : 0,
        Key = damageAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
    private void BeginDamageEncounter() {
        if (!Connected || !damageRankingEnabled || Resetting || Returning || CoopContext.Resetting || changingScene) return;
        var sequence = DamageSequence();
        if (sequence != damageSequence) { damageSequence = sequence; damageAttempt++; damageBeginSent = false; }
        if (damageBeginSent && damageBeginVisit == CoopContext.LocalVisit && damageBeginScene == CoopContext.LocalScene) {
            if (!damageBeginConfirmed && Time.unscaledTime >= nextDamageBegin) {
                nextDamageBegin = Time.unscaledTime + .5f; Send(DamageMessage(Kind.DamageBegin));
            }
            return;
        }
        damageBeginVisit = CoopContext.LocalVisit; damageBeginScene = CoopContext.LocalScene;
        damageBeginSent = true; damageEndSent = false;
        damageBeginConfirmed = false; nextDamageBegin = Time.unscaledTime + .5f;
        Send(DamageMessage(Kind.DamageBegin));
    }
    private int DamageSourcePlayer(GameObject source) {
        var player = AttackSourcePlayer(source);
        if (player >= 0 || source == null) return player;
        // Uumuu's retaliating core retains the player who actually struck Ooma.
        var entity = EntityReferences.Owner(source);
        if (entity?.Type is EntityType.Ooma or EntityType.OomaCorpse or EntityType.OomaCore)
            return BossTargetCommitment.Decode(entity.BossTargetReceipt?.Value, Session, CoopContext.Epoch).Player;
        // Native detached player attacks use the hero-attack layer. Remote
        // clones are tagged at their creation site, before their first hit.
        if (source.GetComponentInParent<RemoteVisualOpacity>() != null || entity != null) return -1;
        if (source.layer == 17 || source.GetComponent<DamageEnemies>() != null || source.GetComponent<DamageEffectTicker>() != null)
            return LocalId;
        return -1;
    }
    private void ReportBossDamage(int damage, GameObject source) {
        if (!damageRankingEnabled || damage <= 0) return;
        BeginDamageEncounter();
        if (!Connected || !damageBeginSent || damageEndSent || Resetting || Returning || changingScene) return;
        var player = DamageSourcePlayer(source);
        if (player < 0 || player > ushort.MaxValue) return;
        var message = DamageMessage(Kind.DamageHit);
        message.Serial = ++damageReportSerial; message.Target = (ushort)player; message.Value = damage;
        Send(message);
    }
    private void EndDamageEncounter(ulong source) {
        if (!Connected || !damageRankingEnabled || !damageBeginSent || damageEndSent || Resetting || Returning ||
            damageBeginVisit != CoopContext.LocalVisit || damageBeginScene != CoopContext.LocalScene) return;
        // Godhome has a native whole-arena completion. Individual health
        // deaths (duos, phases, final cinematics) cannot settle that arena.
        var controller = BossSceneController.Instance;
        if (source != 0 && controller != null && (controller.gameObject.scene.name == CoopContext.LocalScene ||
            controller.gameObject.scene.name.StartsWith(CoopContext.LocalScene + "_boss", StringComparison.Ordinal))) return;
        if (source != 0 && EntityReferences.Entities?.Values.Any(e => e.Id != source && BossDefeatSignals.IsBoss(e.Type) &&
            (e.SourceScene == CoopContext.LocalScene || e.SourceScene.StartsWith(CoopContext.LocalScene + "_boss", StringComparison.Ordinal)) &&
            e.Object.Host != null && e.Object.Host.GetComponent<HealthManager>() is { hp: > 0, isDead: false }) == true) return;
        damageEndSent = true;
        var message = DamageMessage(Kind.DamageEnd);
        message.IsReady = BossSequenceController.IsInSequence && BossSequenceController.IsLastBossScene;
        Send(message);
    }
    private void ReceiveDamageSnapshot(Message message) {
        if (damageRankingEnabled && message.Round == CoopContext.Epoch && message.Scene == CoopContext.LocalScene &&
            damageRanking != null && message.Serial == damageRanking.Board && message.Aux >= damageRanking.Revision &&
            damageRanking.Sequence == DamageSequence()) damageBeginConfirmed = true;
        if (!damageRankingEnabled || message.Round != CoopContext.Epoch || message.BossPlan.Length == 0 ||
            damageRanking != null && (message.Serial < damageRanking.Board || message.Serial == damageRanking.Board && message.Aux <= damageRanking.Revision)) return;
        try {
            var value = JsonConvert.DeserializeObject<DamageRankingSnapshot>(message.BossPlan);
            if (value == null || value.Board != message.Serial || value.Revision != message.Aux || value.Players == null ||
                value.Players.Length > 256 || value.Players.Any(p => p == null || p.Damage < 0 || p.Name == null || p.Name.Length > 128)) return;
            damageRanking = value;
            if (message.Scene == CoopContext.LocalScene && value.Sequence == DamageSequence() && value.Players.Any(p => p.Id == LocalId)) damageBeginConfirmed = true;
        } catch (JsonException) { }
    }
    internal void ToggleDamageRanking() => damageRankingHidden = !damageRankingHidden;
    private void TickDamageRanking() {
        if (!damageRankingEnabled) return;
        if (KeyPressed(KeyCode.F1) &&
            !GameManager.instance.isPaused) ToggleDamageRanking();
        // A scripted arena can have no HealthManager (or only a deferred
        // spawn). Its controller still starts a zero-damage board.
        var controller = BossSceneController.Instance;
        if (controller != null && controller.HasTransitionedIn && controller.gameObject.scene.name == CoopContext.LocalScene &&
            EntityReferences.Entities?.Values.Any(e => e.IsNativeAuthority) == true) BeginDamageEncounter();
        var sequence = DamageSequence();
        if (sequence.Length == 0 && damageSequence.Length > 0) { damageSequence = ""; damageBeginSent = false; }
        var visible = Connected && Playable && !damageRankingHidden && damageRanking != null &&
            !GameManager.instance.isPaused && !changingScene;
        if (visible) {
            damageView ??= new DamageRankingView(transform, font);
            damageView.Show(damageRanking, LocalId);
        } else damageView?.Hide();
    }
}
