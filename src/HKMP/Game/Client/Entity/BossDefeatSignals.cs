using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

// Native Godhome completion covers scripted/empty boss arrays as well as
// ordinary deaths. Story encounters also have health and defeat-record paths.
internal static class BossDefeatSignals {
    internal static bool IsAbsoluteEndingFlag(EntityType type, string scene, string fsm, string state,
        string variable, bool value) => type == EntityType.AbsoluteRadiance &&
        scene == "GG_Radiance" && fsm == "Control" && state == "Ending Scene" &&
        variable == "ggCinematicEnding" && value;
    internal static bool IsAbsoluteEndingFlag(EntityType type, string scene, SetStaticVariable action) {
        if (type != EntityType.AbsoluteRadiance || scene != "GG_Radiance" || !action.Enabled ||
            action.setValue.IsNone || action.setValue.Type != VariableType.Bool) return false;
        action.setValue.UpdateValue();
        return IsAbsoluteEndingFlag(type, scene, action.Fsm.Name, action.State.Name, action.variableName.Value,
            action.setValue.GetValue() is true);
    }
    internal static void ApplyFinalCinematicRecord(EntityType type, string scene, FsmState state) {
        if (type != EntityType.AbsoluteRadiance || scene != "GG_Radiance" || CoopContext.Resetting || scene != CoopContext.LocalScene) return;
        if (state.Actions.OfType<SetStaticVariable>().Any(a => IsAbsoluteEndingFlag(type, scene, a)))
            StaticVariableList.SetValue("ggCinematicEnding", true);
    }
    internal static bool IsBoss(EntityType type) => type is
        EntityType.FalseKnight or EntityType.GruzMother or EntityType.BroodingMawlek or
        EntityType.MassiveMossCharger or EntityType.VengeflyKing or EntityType.Hornet or EntityType.Uumuu or
        EntityType.MantisLordBattle or EntityType.MantisLord or EntityType.SoulWarrior or
        EntityType.SoulMaster or EntityType.SoulMasterPhase2 or EntityType.SoulTyrant or EntityType.SoulTyrantPhase2 or
        EntityType.WatcherKnight or EntityType.TheCollector or EntityType.DungDefender or EntityType.WhiteDefender or
        EntityType.Flukemarm or EntityType.CrystalGuardian or EntityType.EnragedGuardian or EntityType.Nosk or
        EntityType.BrokenVessel or EntityType.LostKin or EntityType.TraitorLord or EntityType.Oblobble or
        EntityType.Zote or EntityType.Tamer or EntityType.Beast or EntityType.Xero or EntityType.Gorb or
        EntityType.ElderHu or EntityType.Marmu or EntityType.NoEyes or EntityType.Galien or EntityType.Markoth or
        EntityType.HollowKnight or EntityType.Radiance or EntityType.GreyPrinceZote or EntityType.Grimm or
        EntityType.NightmareKingGrimm or EntityType.HiveKnight or EntityType.OroMato or EntityType.Oro or
        EntityType.Mato or EntityType.Sheo or EntityType.Sly or EntityType.PureVessel or
        EntityType.WingedNosk or EntityType.AbsoluteRadiance or EntityType.PaleLurker or EntityType.Revek;

    private static bool HasRoomController() {
        var controller = BossSceneController.Instance;
        if (controller == null) return false;
        var scene = controller.gameObject.scene.name;
        return scene == CoopContext.LocalScene || scene == CoopContext.LocalScene + "_boss" || scene == CoopContext.LocalScene + "_boss_defeated";
    }
    internal static bool IsFinalHealthDeath(EntityType type) => IsBoss(type) &&
        type is not (EntityType.SoulMaster or EntityType.SoulTyrant or EntityType.MantisLord);

    internal static void ApplyLateDeathRecord(EntityType type) {
        // The story second corpse writes this in its native Init state. A
        // completed snapshot can have removed that corpse before Die replays,
        // so its one-shot flag never reaches the local reward controller.
        // Do not treat phase-one death, dream/Godhome or arbitrary boss flags
        // as this story completion, and never grant the spell here.
        var pd = PlayerData.instance;
        if (type == EntityType.SoulMasterPhase2 && CoopContext.LocalScene == "Ruins1_24" &&
            pd != null && !pd.GetBool("mageLordDefeated")) pd.SetBool("mageLordDefeated", true);
    }

    internal static bool HealthDeath(EntityType type, ulong id) {
        if (!IsFinalHealthDeath(type)) return false;
        // The story Watcher encounter includes dormant knights. Defeating one
        // knight must not finish the entire group or revive between knights.
        if (type == EntityType.WatcherKnight && EntityReferences.Entities != null && EntityReferences.Entities.Values.Any(e =>
            e.Id != id && e.Type == type && (e.SourceScene == CoopContext.LocalScene ||
                e.SourceScene.StartsWith(CoopContext.LocalScene + "_boss", System.StringComparison.Ordinal)) &&
            e.Object.Host != null && e.Object.Host.GetComponent<HealthManager>() is { isDead: false, hp: > 0 })) return false;
        CoopContext.BossDefeated?.Invoke(id);
        return true;
    }
    internal static bool IsDefeatRecord(EntityType type, string fsm, string state, FsmStateAction[] actions) {
        // Lost Kin writes its story completion on the registered corpse, after
        // the body has died. Keep this metadata path separate from boss health
        // deaths so a corpse never becomes another combat/victory entity.
        if (type == EntityType.LostKinCorpse) return fsm == "corpse" && state == "Set Defeated" &&
            actions.OfType<SetPlayerDataBool>().Any(a => a.Enabled && a.value.Value && a.boolName.Value == "infectedKnightDreamDefeated");
        if (!IsBoss(type) || type is EntityType.SoulMaster or EntityType.SoulTyrant) return false;
        if (type == EntityType.HollowKnight && fsm == "Phase Control" && state == "Die") return true;
        foreach (var action in actions) {
            if (!action.Enabled) continue;
            if (action is SendMessage message && message.functionCall?.FunctionName == "StoryRecord_defeated") return true;
            if (action is SetPlayerDataBool flag && flag.value.Value && DefeatKey(flag.boolName.Value)) return true;
            if (action is SetPlayerDataInt count && count.value.Value > 0 && DefeatKey(count.intName.Value)) return true;
        }
        return false;
    }
    private static bool DefeatKey(string key) => key != null &&
        (key.StartsWith("killed", System.StringComparison.Ordinal) || key.StartsWith("defeated", System.StringComparison.OrdinalIgnoreCase) || key.EndsWith("Defeated", System.StringComparison.Ordinal));
    internal static IEnumerable<string> LocalDefeatFlags(EntityType type, string fsm, string state, FsmStateAction[] actions) =>
        IsDefeatRecord(type, fsm, state, actions) ? actions.OfType<SetPlayerDataBool>()
            .Where(a => a.Enabled && a.value.Value && DefeatKey(a.boolName.Value) &&
                (type != EntityType.LostKinCorpse || a.boolName.Value == "infectedKnightDreamDefeated"))
            .Select(a => a.boolName.Value).Distinct() : Enumerable.Empty<string>();
    internal static void ApplyLocalDefeatRecord(EntityType type, string scene, FsmState state) {
        // Active story participants need the boss's own defeat metadata even
        // with permanent progress sharing disabled. Collected rewards, essence,
        // counters, arbitrary story flags and Godhome records stay personal.
        // The final Hollow Knight corpse has its separate native continuation.
        if (type == EntityType.HollowKnight || PlayerData.instance == null || CoopContext.Resetting ||
            type == EntityType.LostKinCorpse && (scene != "Dream_03_Infected_Knight" || scene != CoopContext.LocalScene) ||
            CoopContext.LocalScene.StartsWith("GG_", System.StringComparison.Ordinal) ||
            scene != CoopContext.LocalScene && !scene.StartsWith(CoopContext.LocalScene + "_boss", System.StringComparison.Ordinal)) return;
        foreach (var key in LocalDefeatFlags(type, state.Fsm.Name, state.Name, state.Actions))
            if (!PlayerData.instance.GetBool(key)) PlayerData.instance.SetBool(key, true);
    }
    internal static void State(EntityType type, ulong id, FsmState state) {
        // Absolute Radiance has a final-hit cinematic before EndGGBossScene.
        // Settle defeated participants at that verified final hit so they can
        // join the presentation before the native room completion runs.
        if (!IsBoss(type) || HasRoomController() && type != EntityType.AbsoluteRadiance) return;
        if (IsDefeatRecord(type, state.Fsm.Name, state.Name, state.Actions)) CoopContext.BossDefeated?.Invoke(id);
    }
}
