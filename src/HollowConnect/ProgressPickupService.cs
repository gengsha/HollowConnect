using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Modding;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Scenes = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect;

// Keep pickup checks local while actual abilities remain the party union.
// Only the native action's read/write is adapted; every animation, event and
// control-taking/releasing action still executes normally.
internal sealed class ProgressPickupService : IDisposable {
    private readonly Func<bool> playable;
    private readonly List<Hook> hooks = new List<Hook>();
    private readonly Dictionary<Fsm, HashSet<string>> pickupFsms = new Dictionary<Fsm, HashSet<string>>();
    private readonly HashSet<PlayerDataBoolTest> waitingPickups = new HashSet<PlayerDataBoolTest>();
    private int sceneRevision;
    private bool disposed;
    private string readKey, writeKey;
    private int writeValue;
    private bool charmCountWrite;
    internal ProgressPickupService(Func<bool> playable) { this.playable = playable; }
    internal void Register() {
        HookRead<GetPlayerDataBool>(a => a.boolName.Value);
        hooks.Add(new Hook(typeof(PlayerDataBoolTest).GetMethod("OnEnter"),
            (Action<Action<PlayerDataBoolTest>, PlayerDataBoolTest>)ReadPickupCondition));
        HookRead<GetPlayerDataInt>(a => a.intName.Value);
        HookWrite<SetPlayerDataBool>(); HookWrite<SetPlayerDataInt>();
        HookWrite<IncrementPlayerDataInt>(); HookWrite<PlayerDataIntAdd>();
        HookVisibility<DeactivateIfPlayerdataTrue>("Start", c => c.boolName);
        HookVisibility<DeactivateIfPlayerdataTrue>("OnEnable", c => c.boolName);
        HookVisibility<DeactivateIfPlayerdataFalse>("Start", c => c.boolName);
        HookVisibility<DeactivateIfPlayerdataFalse>("OnEnable", c => c.boolName);
        HookVisibility<ActivateIfPlayerdataTrue>("Start", c => c.boolName);
        ModHooks.GetPlayerBoolHook += ReadBool;
        ModHooks.GetPlayerIntHook += ReadInt;
        ModHooks.SetPlayerIntHook += WriteInt;
        Scenes.activeSceneChanged += SceneChanged;
    }
    private void HookRead<T>(Func<T, string> key) where T : FsmStateAction {
        hooks.Add(new Hook(typeof(T).GetMethod("OnEnter"), (Action<Action<T>, T>)((orig, action) => {
            var field = key(action);
            if (!CanAdapt(action.Fsm, field)) { orig(action); return; }
            var before = readKey; readKey = field;
            try { orig(action); } finally { readKey = before; }
        })));
    }
    private void HookWrite<T>() where T : FsmStateAction {
        hooks.Add(new Hook(typeof(T).GetMethod("OnEnter"), (Action<Action<T>, T>)((orig, action) => Write(a => orig((T)a), action))));
    }
    private void ReadPickupCondition(Action<PlayerDataBoolTest> orig, PlayerDataBoolTest action) {
        var key = action.boolName.Value;
        var before = readKey;
        if (CanAdapt(action.Fsm, key)) readKey = key;
        try { orig(action); } finally { readKey = before; }
        // A late room snapshot can set a defeat flag after this one-shot check,
        // without replaying the corpse's earlier broadcast. Only a native
        // pickup waiting in the same state may recheck its original condition.
        var fsm = action.Fsm;
        if (disposed || PlayerData.instance == null || CoopMod.Statistics.NativePickups == null ||
            fsm?.Name != "Pickup" || fsm.GameObject == null || !fsm.GameObject.scene.IsValid() ||
            !ProgressCatalog.IsBoolean(key) || ProgressCatalog.IsPickup(key) ||
            !(key.StartsWith("killed", StringComparison.Ordinal) || key.EndsWith("Defeated", StringComparison.OrdinalIgnoreCase)) ||
            action.isTrue == null || string.IsNullOrEmpty(action.isTrue.Name) ||
            action.isFalse != null && !string.IsNullOrEmpty(action.isFalse.Name) ||
            fsm.ActiveState != action.State || Keys(fsm).Count == 0 || PlayerData.instance.GetBool(key) ||
            !waitingPickups.Add(action)) return;
        GameManager.instance.StartCoroutine(WaitForDefeat());
        IEnumerator WaitForDefeat() {
            var revision = sceneRevision;
            var owner = fsm.GameObject;
            var scene = owner.scene;
            var pd = PlayerData.instance;
            var component = owner.GetComponents<PlayMakerFSM>().First(f => f.Fsm == fsm);
            float? defeatAt = null;
            try {
                while (!disposed && revision == sceneRevision && pd == PlayerData.instance && owner != null &&
                    scene.isLoaded && component != null && component.enabled && owner.activeInHierarchy && fsm.ActiveState == action.State) {
                    if (pd.GetBool(key)) {
                        if (!defeatAt.HasValue) defeatAt = Time.time;
                        // Give the native corpse/cascade its original event
                        // first. Only recover an unchanged, stranded waiter.
                        if (Time.time - defeatAt.Value >= 12f) { orig(action); yield break; }
                    } else defeatAt = null;
                    yield return null;
                }
            } finally { waitingPickups.Remove(action); }
        }
    }
    private void HookVisibility<T>(string method, Func<T, string> key) where T : Component {
        hooks.Add(new Hook(typeof(T).GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            (Action<Action<T>, T>)((orig, component) => {
                var field = key(component);
                if (!ProgressCatalog.IsPickup(field) || CoopMod.Statistics.NativePickups == null ||
                    HeroController.instance != null && component.transform.IsChildOf(HeroController.instance.transform) ||
                    !component.GetComponents<PlayMakerFSM>().Any(f => Keys(f.Fsm).Contains(field)) && !PickupScenery(component.gameObject, field)) { orig(component); return; }
                var before = readKey; readKey = field;
                try { orig(component); } finally { readKey = before; }
            })));
    }
    internal void Prepare() {
        if (!playable() || PlayerData.instance == null || CoopMod.Statistics.NativePickups != null) return;
        var native = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in ProgressCatalog.Limits.Keys.Where(ProgressCatalog.IsPickup)) {
            var value = Raw(key);
            if (value > 0 && value <= ProgressCatalog.Limits[key]) native[key] = value;
        }
        CoopMod.Statistics.NativePickups = native;
    }
    private static int Raw(string key) => ProgressCatalog.IsBoolean(key)
        ? (PlayerData.instance.GetBoolInternal(key) ? 1 : 0) : PlayerData.instance.GetIntInternal(key);
    private static int Native(string key) => CoopMod.Statistics.NativePickups != null &&
        CoopMod.Statistics.NativePickups.TryGetValue(key, out var value) ? value : 0;
    private bool CanAdapt(Fsm fsm, string key) {
        if (!ProgressCatalog.IsPickup(key) || PlayerData.instance == null || CoopMod.Statistics.NativePickups == null ||
            fsm?.GameObject == null || !fsm.GameObject.scene.IsValid() ||
            HeroController.instance != null && fsm.GameObject.transform.IsChildOf(HeroController.instance.transform)) return false;
        return Keys(fsm).Contains(key);
    }
    private HashSet<string> Keys(Fsm fsm) {
        if (pickupFsms.TryGetValue(fsm, out var cached)) return cached;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        // Some pickups keep the visibility controller on a parent and the
        // actual grant on its inactive child (for example Desolate Dive).
        // Limit descendant discovery to the native Pickup controller, rather
        // than adapting arbitrary boss/scene ancestors of an item.
        var components = fsm.Name == "Pickup" ? fsm.GameObject.GetComponentsInChildren<PlayMakerFSM>(true) :
            fsm.GameObject.GetComponents<PlayMakerFSM>();
        foreach (var action in components.SelectMany(f => f.FsmStates).SelectMany(s => s.Actions)) {
            var key = action is SetPlayerDataBool b && b.value.Value ? b.boolName.Value :
                action is SetPlayerDataInt i && i.value.Value > 0 ? i.intName.Value :
                action is IncrementPlayerDataInt increment ? increment.intName.Value :
                action is PlayerDataIntAdd add && add.amount.Value > 0 ? add.intName.Value : null;
            if (ProgressCatalog.IsPickup(key)) keys.Add(key);
        }
        // Activation-only controllers can be capability prerequisites rather
        // than pickups (No Eyes requires the shared lantern). A PD-name
        // variable alone is not evidence of a local acquisition. Keep only
        // the verified native wings scenery on the pickup-history path.
        if (PickupScenery(fsm.GameObject, "hasDoubleJump")) keys.Add("hasDoubleJump");
        // A generic shiny/shop may assign its PD-name variable after Awake.
        // Do not cache the not-yet-configured result.
        if (keys.Count != 0 && fsm.Name != "Confirm Control") pickupFsms[fsm] = keys;
        return keys;
    }
    private static bool PickupScenery(GameObject owner, string key) => owner != null && key == "hasDoubleJump" &&
        owner.scene.name == "Abyss_21" && (owner.name == "Pre_Double_Jump" || owner.name == "Post_Double_Jump");
    private bool ReadBool(string key, bool value) => key == readKey ? Native(key) != 0 : value;
    private int ReadInt(string key, int value) => key == readKey ? Native(key) : value;
    private int WriteInt(string key, int value) {
        if (key == writeKey) return ProgressCatalog.Combine(key, PlayerData.instance.GetIntInternal(key), writeValue);
        if (charmCountWrite && key == "charmsOwned") return Math.Max(PlayerData.instance.GetIntInternal(key),
            ProgressCatalog.CharmCount(PlayerData.instance.GetBoolInternal, PlayerData.instance.GetIntInternal("royalCharmState")));
        return value;
    }
    private void Write(Action<FsmStateAction> orig, FsmStateAction action) {
        var key = action is SetPlayerDataBool b ? b.boolName.Value : action is SetPlayerDataInt i ? i.intName.Value :
            action is IncrementPlayerDataInt increment ? increment.intName.Value : action is PlayerDataIntAdd add ? add.intName.Value : null;
        if (key == "flamesCollected" && action is IncrementPlayerDataInt && playable() &&
            action.Fsm?.Name == "Spawn Control" && action.State.Name == "Get" &&
            action.Fsm.GameObject != null && action.Fsm.GameObject.name.StartsWith("Flamebearer Spawn", StringComparison.Ordinal)) {
            var pd = PlayerData.instance;
            var level = pd.GetInt("grimmChildLevel");
            var location = GrimmFlames.Location(level, action.Fsm.GameObject.scene.name);
            if (location != 0) {
                // AddToFlameList follows this increment in the native Get
                // state. Count this location once even if takeover re-enters it.
                var flameBeforeKey = writeKey; var flameBeforeValue = writeValue;
                writeKey = key;
                writeValue = GrimmFlames.Count(GrimmFlames.Evidence(level, pd.scenesFlameCollected, pd.GetBool("gotBrummsFlame")) | location);
                try { orig(action); } finally { writeKey = flameBeforeKey; writeValue = flameBeforeValue; }
                return;
            }
        }
        if (key == "charmsOwned" && action.Fsm?.GameObject != null && CoopMod.Statistics.NativePickups != null &&
            Keys(action.Fsm).Any(ProgressCatalog.IsCharm)) {
            var before = charmCountWrite; charmCountWrite = true;
            try { orig(action); } finally { charmCountWrite = before; }
            return;
        }
        if (!CanAdapt(action.Fsm, key)) { orig(action); return; }
        var target = action is SetPlayerDataBool flag ? (flag.value.Value ? 1 : 0) :
            action is SetPlayerDataInt set ? set.value.Value : Native(key) + (action is PlayerDataIntAdd amount ? amount.amount.Value : 1);
        if (target <= 0) { orig(action); return; }
        target = Math.Min(target, ProgressCatalog.Limits[key]);
        var beforeKey = writeKey; var beforeValue = writeValue;
        writeKey = key; writeValue = target;
        try { orig(action); } finally { writeKey = beforeKey; writeValue = beforeValue; }
        if (Raw(key) >= target) CoopMod.Statistics.NativePickups[key] = Math.Max(Native(key), target);
    }
    private void SceneChanged(UnityEngine.SceneManagement.Scene oldScene, UnityEngine.SceneManagement.Scene next) {
        sceneRevision++; pickupFsms.Clear(); waitingPickups.Clear();
    }
    public void Dispose() {
        disposed = true; sceneRevision++; waitingPickups.Clear();
        Scenes.activeSceneChanged -= SceneChanged;
        ModHooks.GetPlayerBoolHook -= ReadBool; ModHooks.GetPlayerIntHook -= ReadInt;
        ModHooks.SetPlayerIntHook -= WriteInt;
        foreach (var hook in hooks) hook.Dispose();
        hooks.Clear(); pickupFsms.Clear();
    }
}
