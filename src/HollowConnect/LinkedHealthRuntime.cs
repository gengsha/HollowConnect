using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp;
using HutongGames.PlayMaker;
using HollowConnect.Core;
using Modding;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    internal static CoopRuntime Current { get; private set; }
    private bool linkedHealthMode, linkedHealthInitSent, linkedHealthStateReceived, linkedHealthHooks;
    private bool linkedHealthApplying, linkedDeathRequested;
    private int linkedHealthCurrent, linkedHealthMax, linkedHealthBonusMasks, linkedNativeScope;
    private uint linkedHealthRevision, linkedHealthDeltaSerial;
    private int linkedSaveMode, linkedPersonalHealth;
    private bool linkedPersonalCaptured;
    private bool linkedSteelEnded;
    private PlayerData linkedHealthPlayerData;
    private Hook linkedCurrentMaxHook;
    private Hook linkedMaskUiUpdateHook;
    private readonly Dictionary<Fsm, int> linkedMaskUiHealth = new Dictionary<Fsm, int>();
    private readonly SortedDictionary<uint, int> linkedPending = new SortedDictionary<uint, int>();
    private static readonly MethodInfo ClonePlayerData = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
    internal bool LinkedHealthRuleEnabled => Connected && linkedHealthMode && Shared && ShareCharms;
    internal bool LinkedHostReady => resourceImported && !Resetting && !Down;
    internal int LinkedHostHealth => linkedPersonalCaptured ? linkedPersonalHealth : PlayerData.instance.GetIntInternal("health");
    private bool LinkedHealthActive => LinkedHealthRuleEnabled && linkedHealthStateReceived &&
        linkedNativeScope == 0 && linkedHealthPlayerData == PlayerData.instance;

    private void ResetLinkedHealth(bool keepRule = false) {
        if (linkedHealthHooks) {
            ModHooks.GetPlayerIntHook -= ReadLinkedHealth;
            ModHooks.BeforeSavegameSaveHook -= SaveLinkedHealth;
            On.PlayerData.AddToMaxHealth -= LinkedMaskUpgrade;
            linkedCurrentMaxHook?.Dispose(); linkedCurrentMaxHook = null;
            linkedMaskUiUpdateHook?.Dispose(); linkedMaskUiUpdateHook = null;
            On.HutongGames.PlayMaker.Fsm.EnterState -= LinkedMaskUiEnter;
            ModHooks.GetPlayerBoolHook -= ReadLinkedCharm;
            On.PlayerData.EquipCharm -= LinkedEquipCharm;
            On.PlayerData.UnequipCharm -= LinkedUnequipCharm;
            On.HeroController.CharmUpdate -= LinkedCharmUpdate;
            On.BossSequenceController.SetupNewSequence -= LinkedSequence;
            linkedHealthHooks = false;
        }
        var restEpoch = linkedRestEpoch;
        ResetLinkedRest(); RestoreLinkedCharms();
        if (keepRule) linkedRestEpoch = restEpoch + 1;
        if (linkedHealthPlayerData != null && linkedHealthPlayerData == PlayerData.instance) {
            var pd = linkedHealthPlayerData;
            if (linkedPersonalCaptured)
                pd.SetIntInternal("health", Math.Min(linkedPersonalHealth, Math.Max(1, pd.GetIntInternal("maxHealth"))));
            pd.SetIntInternal("prevHealth", pd.GetIntInternal("health"));
        }
        if (!keepRule) { linkedHealthMode = false; linkedHealthDeltaSerial = 0; }
        linkedHealthInitSent = linkedHealthStateReceived = linkedDeathRequested = linkedHealthApplying = false;
        linkedHealthCurrent = linkedHealthMax = 0; linkedHealthRevision = 0;
        linkedHealthPlayerData = null; linkedPending.Clear();
        linkedPersonalCaptured = false;
        linkedSteelEnded = false;
        linkedMaskUiHealth.Clear();
        if (keepRule && linkedHealthMode) RegisterLinkedHealth();
    }
    private void RegisterLinkedHealth() {
        if (linkedHealthHooks) return;
        ModHooks.GetPlayerIntHook += ReadLinkedHealth;
        ModHooks.GetPlayerBoolHook += ReadLinkedCharm;
        On.PlayerData.EquipCharm += LinkedEquipCharm;
        On.PlayerData.UnequipCharm += LinkedUnequipCharm;
        On.HeroController.CharmUpdate += LinkedCharmUpdate;
        On.BossSequenceController.SetupNewSequence += LinkedSequence;
        ModHooks.BeforeSavegameSaveHook += SaveLinkedHealth;
        On.PlayerData.AddToMaxHealth += LinkedMaskUpgrade;
        linkedCurrentMaxHook = new Hook(typeof(PlayerData).GetProperty("CurrentMaxHealth").GetGetMethod(),
            new Func<Func<PlayerData, int>, PlayerData, int>((orig, pd) =>
                LinkedHealthActive && pd == PlayerData.instance ? linkedHealthMax : orig(pd)));
        On.HutongGames.PlayMaker.Fsm.EnterState += LinkedMaskUiEnter;
        linkedMaskUiUpdateHook = new Hook(typeof(Fsm).GetMethod("Update"),
            new Action<Action<Fsm>, Fsm>(LinkedMaskUiUpdate));
        linkedHealthHooks = true;
    }
    private int ReadLinkedHealth(string key, int value) => LinkedHealthActive && key == "maxHealth" ? linkedHealthMax : value;

    private void CaptureLinkedPersonal(PlayerData pd) {
        if (linkedPersonalCaptured && linkedHealthPlayerData == pd) return;
        linkedHealthPlayerData = pd; linkedPersonalCaptured = true;
        linkedPersonalHealth = pd.GetIntInternal("health");
    }
    private bool ValidateLinkedSlot() {
        if (!linkedHealthStateReceived || !Playable || Down || Resetting || Returning) return true;
        var pd = PlayerData.instance;
        var reason = pd.GetIntInternal("permadeathMode") != linkedSaveMode ?
            (linkedSaveMode == 1 ? "链在一起：钢魂房间需要使用钢魂存档" : "链在一起：普通房间需要使用普通存档") :
            BossSequenceController.BoundShell ? "链在一起：请先结束带生命绑定的挑战再加入" : null;
        if (reason == null) return true;
        api.ClientManager.Disconnect(); Notice(reason); return false;
    }
    private void LinkedSequence(On.BossSequenceController.orig_SetupNewSequence orig, BossSequence sequence,
        BossSequenceController.ChallengeBindings bindings, string playerData) {
        if ((bindings & BossSequenceController.ChallengeBindings.Shell) != 0) {
            Notice("链在一起：不能开启生命绑定"); return;
        }
        orig(sequence, bindings, playerData);
    }

    private void LinkedMaskUiEnter(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, Fsm fsm, FsmState state) {
        var restScope = BeginLinkedRestFsm(fsm);
        try {
        if (!BeginNativeMaskUi(fsm, out var pd, out var health, out var previous)) { orig(fsm, state); return; }
        try { orig(fsm, state); } finally { EndNativeMaskUi(fsm, pd, health, previous); }
        } finally { if (restScope) linkedHealBlockScope--; }
    }
    private void LinkedMaskUiUpdate(Action<Fsm> orig, Fsm fsm) {
        var restScope = BeginLinkedRestFsm(fsm);
        try {
        if (!BeginNativeMaskUi(fsm, out var pd, out var health, out var previous)) { orig(fsm); return; }
        try { orig(fsm); } finally { EndNativeMaskUi(fsm, pd, health, previous); }
        } finally { if (restScope) linkedHealBlockScope--; }
    }

    // The original full-mask UI animates a refill before it upgrades the
    // permanent mask. Let that UI finish against its own vanilla health;
    // its animation must not refill the room or include temporary masks in
    // the permanent upgrade. Both fields are restored before other gameplay.
    private bool BeginNativeMaskUi(Fsm fsm, out PlayerData pd, out int health, out int previous) {
        pd = null; health = previous = 0;
        if (fsm.Name != "Heart Container UI" || !LinkedHealthActive) return false;
        pd = PlayerData.instance; health = pd.health; previous = pd.prevHealth;
        if (!linkedMaskUiHealth.TryGetValue(fsm, out var nativeHealth)) nativeHealth = Math.Min(health, pd.maxHealth);
        pd.health = nativeHealth; pd.prevHealth = nativeHealth; linkedNativeScope++;
        return true;
    }
    private void EndNativeMaskUi(Fsm fsm, PlayerData pd, int health, int previous) {
        linkedMaskUiHealth[fsm] = pd.health;
        linkedNativeScope--; pd.health = health; pd.prevHealth = previous;
    }

    // Capacity and mask receipts always stay in vanilla PlayerData. This
    // narrowly scoped bypass lets the original upgrade run its own counters,
    // while only the server's unique-source union grows the room bar.
    private void LinkedMaskUpgrade(On.PlayerData.orig_AddToMaxHealth orig, PlayerData self, int amount) {
        if (!LinkedHealthRuleEnabled || !linkedHealthStateReceived || self != linkedHealthPlayerData) { orig(self, amount); return; }
        var nativeUi = linkedNativeScope > 0;
        var permanent = self.GetIntInternal("maxHealthBase");
        var health = self.GetIntInternal("health"); var previous = self.GetIntInternal("prevHealth");
        linkedNativeScope++;
        try { orig(self, amount); }
        finally {
            linkedNativeScope--;
            linkedPersonalHealth = Math.Min(LinkedPersonalCapacity(self), linkedPersonalHealth + Math.Max(0, self.GetIntInternal("maxHealthBase") - permanent));
            if (!nativeUi) { self.SetIntInternal("health", health); self.SetIntInternal("prevHealth", previous); }
        }
    }
    private void SaveLinkedHealth(SaveGameData data) {
        if (!LinkedHealthActive || data?.playerData == null) return;
        // SaveGameData normally aliases the live PlayerData. Clone before
        // changing its scalar fields so serialization cannot mutate the room.
        data.playerData = (PlayerData)ClonePlayerData.Invoke(data.playerData, null);
        RestoreLinkedCharmSnapshot(data.playerData);
        data.playerData.health = Math.Min(linkedPersonalHealth, Math.Max(1, data.playerData.maxHealth));
        data.playerData.prevHealth = data.playerData.health;
    }
    internal void ApplyLinkedWelcome(Message message) {
        if ((message.Value & 2048) != 0) { api.ClientManager.Disconnect(); Notice("链在一起：钢魂挑战已结束，请重新建房"); return; }
        linkedHealthBonusMasks = LinkedHealthRules.NormalizeBonus(message.LinkedBonusMasks);
        if ((message.Value & 1024) == 0 || !Shared || !ShareCharms) { ResetLinkedHealth(); return; }
        if (linkedHealthMode) return; // duplicate Hello must not restart report identities
        linkedHealthMode = true;
        RescueEnabled = SpectateAfterExhaustion = false;
        RegisterLinkedHealth();
    }
    private void TickLinkedHealth() {
        if (!LinkedHealthRuleEnabled) return;
        var pd = PlayerData.instance;
        if (pd == null) return;
        if (!ValidateLinkedSlot()) return;
        if (Playable && !linkedPersonalCaptured) CaptureLinkedPersonal(pd);
        if (Playable) SanitizeLinkedCharms(pd);
        TickLinkedRest();
        if (!linkedHealthInitSent && Playable && resourceImported) {
            CaptureLinkedPersonal(pd); linkedHealthInitSent = true;
            Send(new Message { Kind = Kind.LinkedHealthInit, LinkedHealth = pd.GetIntInternal("health"),
                LinkedMaxHealth = pd.GetIntInternal("maxHealthBase") });
            if (linkedHealthStateReceived) ApplyLinkedHealthState();
        }
        if (!LinkedHealthActive || Resetting || Returning || changingScene || Down) return;
        if (linkedHealthCurrent == 0 && !linkedDeathRequested && Playable) {
            linkedDeathRequested = true; StartCoroutine(DieForLinkedHealth());
        }
    }
    private void ReceiveLinkedHealthState(Message message) {
        if (!LinkedHealthRuleEnabled || message.Round != CoopContext.Epoch || message.LinkedMaxHealth < 5) return;
        // State may arrive before the local slot is ready. Buffer without
        // touching any PlayerData from a title/profile screen.
        if (message.Id == LocalId) foreach (var serial in linkedPending.Keys.Where(s => s <= message.Serial).ToArray()) linkedPending.Remove(serial);
        if (linkedHealthStateReceived && message.LinkedRevision < linkedHealthRevision) return;
        linkedHealthStateReceived = true; linkedHealthRevision = message.LinkedRevision;
        linkedHealthCurrent = LinkedHealthRules.ClampHealth(message.LinkedHealth, message.LinkedMaxHealth);
        if (linkedHealthCurrent > 0) linkedDeathRequested = false;
        linkedHealthMax = message.LinkedMaxHealth;
        linkedSaveMode = message.LinkedSaveMode;
        linkedRestCount = new LinkedRestCount(Math.Max(0, message.Value), Math.Max(0, (int)message.X), Math.Max(0, (int)message.Y));
        linkedRestStatus = message.LinkedRestStatus;
        if (!ValidateLinkedSlot()) return;
        if (!linkedPersonalCaptured && Playable) CaptureLinkedPersonal(PlayerData.instance);
        ApplyLinkedHealthState();
    }
    private int PredictedLinkedHealth() {
        var health = linkedHealthCurrent;
        if (health > 0) foreach (var delta in linkedPending.Values) health = LinkedHealthRules.ApplyDelta(health, delta, linkedHealthMax);
        return health;
    }
    private void ApplyLinkedHealthState() {
        if (!LinkedHealthActive || Resetting || Down || Returning) return;
        var pd = PlayerData.instance;
        var previous = pd.GetIntInternal("health");
        // Only an authoritative zero starts death. Keep native TakeDamage
        // from committing a predicted lethal hit before concurrent heals land.
        var health = linkedHealthCurrent == 0 ? 0 : Math.Max(1, PredictedLinkedHealth());
        pd.SetIntInternal("prevHealth", previous); pd.SetIntInternal("health", health);
        if (previous != health) {
            linkedHealthApplying = true;
            try { RefreshLinkedHealthHud(health > previous); }
            finally { linkedHealthApplying = false; }
        }
    }
    private static void RefreshLinkedHealthHud(bool healed) {
        // Native HERO DAMAGED has gameplay consumers on the Knight and world.
        // Only HUD descendants receive the visual event for a shared update.
        var canvas = GameCameras.instance?.hudCanvas;
        if (canvas != null) foreach (var fsm in canvas.GetComponentsInChildren<PlayMakerFSM>(true))
            fsm.SendEvent(healed ? "HERO HEALED" : "HERO DAMAGED");
        PlayMakerFSM.BroadcastEvent("FURY REFRESH");
    }
    private IEnumerator DieForLinkedHealth() {
        if (!LinkedHealthActive || linkedHealthCurrent != 0 || Down || Resetting || !Playable) { linkedDeathRequested = false; yield break; }
        var hero = HeroController.instance;
        var method = typeof(HeroController).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic);
        hero.StartCoroutine((IEnumerator)method.Invoke(hero, null));
    }
    private int? LinkedHealthWrite(string key, int value) {
        if (!LinkedHealthActive || linkedHealthApplying || key != "health") return null;
        if (Resetting || Down || Returning || linkedNativeScope > 0) return null;
        if (linkedHealthCurrent == 0) return 0;
        var pd = PlayerData.instance;
        if (value > pd.GetIntInternal("health") && BlockLinkedRestHeal) return pd.GetIntInternal("health");
        var delta = LinkedHealthRules.ClampHealth(value, linkedHealthMax) - pd.GetIntInternal("health");
        if (delta != 0) {
            var serial = ++linkedHealthDeltaSerial; linkedPending[serial] = delta;
            if (delta < 0) linkedRestEpoch++;
            Send(new Message { Kind = Kind.LinkedHealthDelta, Serial = serial, Value = delta, LinkedRestEpoch = linkedRestEpoch });
        }
        return Math.Max(1, LinkedHealthRules.ClampHealth(value, linkedHealthMax));
    }
    private void BeginLinkedDeath(On.HeroController.orig_Die orig, HeroController hero) {
        if (Down || Resetting || Returning) return;
        if (linkedHealthCurrent != 0) {
            LinkedHealthWrite("health", 0); return;
        }
        personalDeath = new PersonalDeath(hero, () => orig(hero));
        var zone = GameManager.instance.GetCurrentMapZone();
        if (linkedSteelEnded || linkedSaveMode == 1 && zone != "DREAM_WORLD" && zone != "GODS_GLORY") {
            Send(new Message { Kind = Kind.LinkedHealthEnd });
            EndLinkedSteel(); return;
        }
        deathScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        DeathPosition = hero.transform.position; Down = true; downSerial++; localDownLifeVersion = 0;
        Lock(); SendDown();
    }
    private PersonalDeath CaptureLinkedDisconnectDeath() {
        if (!linkedHealthStateReceived || linkedHealthCurrent != 0 || linkedHealthPlayerData != PlayerData.instance || HeroController.instance == null) return null;
        var hero = HeroController.instance;
        var method = typeof(HeroController).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic);
        return new PersonalDeath(hero, () => (IEnumerator)method.Invoke(hero, null));
    }
    private void EndLinkedSteel() {
        if (!LinkedHealthRuleEnabled || linkedSaveMode != 1) return;
        linkedSteelEnded = true;
        if (personalDeath == null || personalDeath.Started) return;
        Down = true; Unlock(); personalDeath.Start();
        StartCoroutine(LeaveLinkedSteel(personalDeath.OriginHandle, Session));
    }
    private IEnumerator LeaveLinkedSteel(int origin, string session) {
        var until = Time.realtimeSinceStartup + 40;
        while (Connected && Session == session && UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == origin && Time.realtimeSinceStartup < until)
            yield return null;
        if (Connected && Session == session) { api.ClientManager.Disconnect(); Notice("链在一起：钢魂挑战结束"); }
    }
}
