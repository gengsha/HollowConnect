using System.Collections.Generic;
using System.Linq;
using Hkmp;
using HollowConnect.Core;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private bool bossVictoryRescue;
    private uint bossReportSerial, lastBossVictory, bossReportVisit, bossReportEpoch;
    private uint settlementToken, settlementVisit;
    private string settlementScene;
    private double settlementUntil;
    private bool BossSettlementPending => Now < settlementUntil && settlementVisit == CoopContext.LocalVisit && settlementScene == CoopContext.LocalScene;
    private readonly HashSet<ulong> reportedBossDefeats = new HashSet<ulong>();
    private void AwaitBossSettlement() {
        if (!Connected || !(RescueEnabled && bossVictoryRescue || SpectateAfterExhaustion) || BossSettlementPending) return;
        var sameVisit = settlementVisit == CoopContext.LocalVisit && settlementScene == CoopContext.LocalScene;
        if (sameVisit && settlementToken != 0 && !Down && !Peers.Values.Any(p => p.State?.Scene == CoopContext.LocalScene &&
            (p.Life == Life.Down || p.Life == Life.Spectating))) return;
        settlementScene = CoopContext.LocalScene; settlementVisit = CoopContext.LocalVisit; settlementToken = 0; settlementUntil = Now + 17;
    }
    private void ResetBossVictoryRescue() {
        bossReportSerial = lastBossVictory = 0;
        settlementUntil = 0; settlementToken = 0; settlementScene = null;
        reportedBossDefeats.Clear();
    }
    private void ReportBossDefeated(ulong source) {
        EndDamageEncounter(source);
        if (!Connected || !(RescueEnabled && bossVictoryRescue || SpectateAfterExhaustion) || Resetting || Returning || CoopContext.Resetting) return;
        if (bossReportVisit != CoopContext.LocalVisit || bossReportEpoch != CoopContext.Epoch) {
            reportedBossDefeats.Clear(); bossReportVisit = CoopContext.LocalVisit; bossReportEpoch = CoopContext.Epoch;
        }
        if (!reportedBossDefeats.Add(source)) return;
        settlementScene = CoopContext.LocalScene; settlementVisit = CoopContext.LocalVisit; settlementToken = 0; settlementUntil = Now + 17;
        CoopMod.Instance?.Log("Boss victory reported scene=" + CoopContext.LocalScene + " visit=" + CoopContext.LocalVisit + " source=" + source);
        Send(new Message { Kind = Kind.BossDefeated, Serial = ++bossReportSerial, Aux = CoopContext.LocalVisit, Scene = CoopContext.LocalScene });
    }
    private void ReceiveBossVictory(Message message) {
        if (message.Serial == 0 || message.Serial <= lastBossVictory || message.Target != LocalId ||
            message.Aux != CoopContext.LocalVisit || message.Scene != CoopContext.LocalScene) return;
        // Consume even while alive. A replay must not revive a later death.
        lastBossVictory = message.Serial;
        settlementScene = message.Scene; settlementVisit = message.Aux; settlementToken = message.Serial; settlementUntil = Now + 17;
        var recover = (bossVictoryRescue && RescueEnabled || Spectating) && Down && !Resetting && !Returning && !changingScene && deathScene == message.Scene;
        Send(new Message { Kind = Kind.BossRecoveryRequest, Serial = downSerial, Aux = message.Serial,
            Value = unchecked((int)CoopContext.LocalVisit), Scene = message.Scene, IsReady = !recover });
    }
    private void ReceiveBossSettlementReady(Message message) {
        if (message.Target == LocalId && message.Serial == settlementToken && message.Aux == settlementVisit &&
            message.Scene == settlementScene && message.Aux == CoopContext.LocalVisit && message.Scene == CoopContext.LocalScene)
            settlementUntil = 0;
    }
}
