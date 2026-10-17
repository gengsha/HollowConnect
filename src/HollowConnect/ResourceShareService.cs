using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Save;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Modding;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Scenes = UnityEngine.SceneManagement.SceneManager;
using Object = UnityEngine.Object;

namespace HollowConnect;

// Caller owns connection/slot lifecycle, wire validation and the existing modded
// JSON container. Neither an early native bool nor a server claim is a local award.
internal sealed class ResourceShareService : IDisposable {
    private readonly Func<bool> enabled, ready;
    private readonly Func<ResourceClaims> local;
    private readonly Action<string> offer, log;
    private readonly List<Hook> hooks = new List<Hook>();
    private readonly Queue<string> pending = new Queue<string>();
    private readonly HashSet<string> queued = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeFlow> inFlight = new Dictionary<string, NativeFlow>(StringComparer.Ordinal);
    private readonly HashSet<string> dirtyShop = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> unrecognized = new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<string> announcements = new Queue<string>();
    private ResourceSource creating;
    private NativeGrant grant;
    private bool applying, registered, refresh;
    private sealed class NativeFlow {
        internal ResourceSource Source;
        internal readonly List<GameObject> RewardUi = new List<GameObject>();
        internal int LastActivityFrame, ReadyFrame = -1;
    }
    private sealed class NativeGrant {
        internal ResourceSource Source;
        internal string Field;
        internal bool Mutated;
    }
    internal ResourceShareService(Func<bool> enabled, Func<bool> ready, Func<ResourceClaims> local,
        Action<string> offer, Action<string> log) {
        this.enabled = enabled; this.ready = ready; this.local = local;
        this.offer = offer; this.log = log;
    }
    internal void Register() {
        if (registered) return;
        registered = true;
        hooks.Add(new Hook(typeof(IncrementPlayerDataInt).GetMethod("OnEnter"),
            (Action<Action<IncrementPlayerDataInt>, IncrementPlayerDataInt>)((orig, action) => NativeAction(a => orig((IncrementPlayerDataInt)a), action))));
        hooks.Add(new Hook(typeof(PlayerDataIntAdd).GetMethod("OnEnter"),
            (Action<Action<PlayerDataIntAdd>, PlayerDataIntAdd>)((orig, action) => NativeAction(a => orig((PlayerDataIntAdd)a), action))));
        hooks.Add(new Hook(typeof(SetPlayerDataInt).GetMethod("OnEnter"),
            (Action<Action<SetPlayerDataInt>, SetPlayerDataInt>)((orig, action) => NativeAction(a => orig((SetPlayerDataInt)a), action))));
        hooks.Add(new Hook(typeof(CreateObject).GetMethod("OnEnter"),
            (Action<Action<CreateObject>, CreateObject>)((orig, action) => CreateReward(a => orig((CreateObject)a), action))));
        // Full-save mode may replay this auth key's older resource balances or
        // pickup markers before this slot's source ledger is imported. The
        // source adapter owns these fields even with permanent sharing off.
        hooks.Add(new Hook(typeof(SaveManager).GetMethod("UpdateSaveWithData", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(ushort), typeof(byte[]) }, null),
            (Action<Action<SaveManager, ushort, byte[]>, SaveManager, ushort, byte[]>)PreserveLocalResource));
        ModHooks.SetPlayerIntHook += SetInt;
        On.HutongGames.PlayMaker.Fsm.EnterState += EnterState;
        Scenes.activeSceneChanged += SceneChanged;
    }
    public void Dispose() {
        if (!registered) return;
        registered = false;
        ModHooks.SetPlayerIntHook -= SetInt;
        On.HutongGames.PlayMaker.Fsm.EnterState -= EnterState;
        Scenes.activeSceneChanged -= SceneChanged;
        foreach (var hook in hooks) hook.Dispose();
        hooks.Clear(); ResetSlot();
    }
    private void PreserveLocalResource(Action<SaveManager, ushort, byte[]> orig, SaveManager manager, ushort index, byte[] value) {
        var mapping = SaveDataMapping.Instance;
        // Charm ownership belongs to the opt-in, monotonic progress ledger.
        // Full-save/incremental replay must not bypass its room rule.
        if (mapping.PlayerDataIndices.TryGetValue(index, out var name) &&
            (ResourceCatalog.PreservesPlayerData(name) || ProgressCatalog.IsCharm(name) || name == "charmsOwned")) return;
        if (mapping.PersistentBoolIndices.TryGetValue(index, out var item) && ResourceCatalog.PreservesPersistent(item.SceneName, item.Id)) return;
        orig(manager, index, value);
    }
    internal void ResetConnection() {
        // Disconnecting must not interrupt an original pickup still releasing
        // control. Its actual receipt belongs to the slot even while offline.
        pending.Clear(); queued.Clear(); announcements.Clear();
    }
    internal void ResetSlot() {
        ResetConnection(); inFlight.Clear(); dirtyShop.Clear();
        creating = null; grant = null; applying = refresh = false;
    }
    private void SceneChanged(UnityEngine.SceneManagement.Scene oldScene, UnityEngine.SceneManagement.Scene next) {
        inFlight.Clear(); creating = null; grant = null; refresh = true;
    }
    private bool Active => registered && PlayerData.instance != null;
    private static bool NativeIdle {
        get {
            var hero = HeroController.instance; var gm = GameManager.instance;
            return hero != null && gm != null && hero.isHeroInPosition && !gm.IsInSceneTransition &&
                !gm.isPaused && Time.timeScale > 0 && hero.acceptingInput && !hero.controlReqlinquished &&
                !hero.cState.dead && !hero.cState.hazardDeath && !hero.cState.hazardRespawning && !hero.cState.transitioning;
        }
    }
    private static string NativeName(string name) => name != null && name.EndsWith("(Clone)", StringComparison.Ordinal)
        ? name.Substring(0, name.Length - 7).TrimEnd() : name;
    private static ResourceKind? Kind(string field) {
        switch (field) {
            case "charmSlots": return ResourceKind.CharmNotch;
            case "heartPieces": return ResourceKind.MaskShard;
            case "vesselFragments": return ResourceKind.VesselFragment;
            case "ore": return ResourceKind.PaleOre;
            default: return null;
        }
    }
    private static string ActionField(FsmStateAction action) => action is IncrementPlayerDataInt increment ? increment.intName.Value :
        action is PlayerDataIntAdd add ? add.intName.Value : action is SetPlayerDataInt set ? set.intName.Value : null;

    // Run once after the slot is loaded and before advertising initial claims.
    // Saved NPC flags are evidence of prior acquisition only at this boundary;
    // during a conversation they are merely an intention to award later.
    // For old arena saves, completed without a shiny receipt is ambiguous.
    // Conservatively regard it as already received, never infer from balances.
    // This cannot safely compensate an old reward that was actually missed.
    internal bool ImportNativeReceipts() {
        if (!Active || SceneData.instance == null || !NativeIdle || inFlight.Count != 0) return false;
        foreach (var source in ResourceCatalog.Sources) {
            var savedPickup = source.PersistentId != null && SceneData.instance.FindMyState(new PersistentBoolData {
                sceneName = source.Scene, id = source.PersistentId
            })?.activated == true;
            var savedFlag = source.PlayerFlag != null && PlayerData.instance.GetBool(source.PlayerFlag);
            if (savedPickup || savedFlag) local().TryClaim(source.Id, out _);
        }
        return true;
    }
    internal void Receive(string sourceId) {
        if (!Active || !enabled() || !ResourceCatalog.TryGet(sourceId, out _) || !queued.Add(sourceId)) return;
        pending.Enqueue(sourceId);
    }
    // Call from the existing runtime update. No per-frame scene/component scan:
    // refresh scans run only after a real grant or room entry.
    internal void Tick() {
        if (!Active) return;
        CompleteNativeFlows();
        while (announcements.Count > 0) {
            var sourceId = announcements.Dequeue();
            // Initial connection advertises all persisted receipts. An offline
            // acquisition is remembered but is not sent through a stale sender.
            if (enabled()) offer(sourceId);
        }
        // A different source's fragment UI can still hold a cached count and
        // perform rollover later. Serialize all remote grants behind that UI.
        if (!enabled() || !ready() || !NativeIdle || inFlight.Count != 0) return;
        var count = pending.Count;
        while (count-- > 0) {
            var id = pending.Dequeue();
            queued.Remove(id);
            if (!ResourceCatalog.TryGet(id, out var source)) continue;
            if (!local().Contains(id)) {
                applying = true;
                try {
                    GrantRemote(source);
                    local().TryClaim(id, out _);
                } finally { applying = false; }
            }
            SetNativeReceipt(source);
            MarkShopDirty(source);
            refresh = true;
        }
        if (refresh) { refresh = false; RefreshScene(); }
    }

    private NativeFlow BeginNativeFlow(ResourceSource source) {
        if (!inFlight.TryGetValue(source.Id, out var flow)) {
            flow = new NativeFlow { Source = source };
            inFlight.Add(source.Id, flow);
        }
        flow.LastActivityFrame = Time.frameCount;
        flow.ReadyFrame = -1;
        return flow;
    }
    private void CompleteNativeFlows() {
        // Numeric mutation is deliberately not completion. In particular the
        // native mask/vessel UI resets counters and calls RegainControl only
        // in its final Destroy Self state. Never disable its pickup early.
        if (inFlight.Count == 0) return;
        foreach (var flow in inFlight.Values.ToArray()) {
            if (!NativeIdle || flow.RewardUi.Any(ui => ui != null && ui.activeInHierarchy) ||
                Time.frameCount <= flow.LastActivityFrame) { flow.ReadyFrame = -1; continue; }
            if (flow.ReadyFrame < 0) { flow.ReadyFrame = Time.frameCount; continue; }
            if (Time.frameCount <= flow.ReadyFrame) continue;
            inFlight.Remove(flow.Source.Id);
            if (local().Contains(flow.Source.Id)) {
                // A native SaveState during the animation may have overwritten
                // an early persistent marker; commit it again after completion.
                SetNativeReceipt(flow.Source);
                MarkShopDirty(flow.Source);
                refresh = true;
            }
        }
    }
    private void MarkShopDirty(ResourceSource source) {
        if (source.Scene == "Room_shop" || source.Scene == "Room_Charm_Shop") dirtyShop.Add(source.Id);
    }

    private void NativeAction(Action<FsmStateAction> orig, FsmStateAction action) {
        if (!Active || applying) { orig(action); return; }
        var field = ActionField(action); var kind = Kind(field);
        if (!kind.HasValue) { orig(action); return; }
        var old = PlayerData.instance.GetIntInternal(field);
        var increasing = action is IncrementPlayerDataInt || action is PlayerDataIntAdd add && add.amount.Value > 0 ||
            action is SetPlayerDataInt set && set.value.Value > old;
        if (!increasing) { orig(action); return; }
        var source = Resolve(action.Fsm, kind.Value) ?? (creating?.Kind == kind ? creating : null);
        if (source == null) {
            var key = action.Fsm?.GameObject?.scene.name + "/" + action.Fsm?.GameObject?.name + "/" + action.Fsm?.Name + "/" + action.State?.Name + "/" + field;
            if (unrecognized.Add(key)) log("Resource native endpoint not identified: " + key);
            orig(action); return;
        }
        var before = grant;
        var current = new NativeGrant { Source = source, Field = field };
        BeginNativeFlow(source);
        grant = current;
        try { orig(action); } finally { grant = before; }
        if (current.Mutated && local().TryClaim(source.Id, out _)) {
            announcements.Enqueue(source.Id);
            // Keep inFlight through native UI completion. The source receipt
            // is immediate, but no object/control cleanup happens here.
        }
    }
    private int SetInt(string field, int value) {
        if (!Active || applying || grant == null || grant.Field != field) return value;
        var old = PlayerData.instance.GetIntInternal(field);
        if (value <= old) return value; // Native fragment rollover and ore spending remain ordinary writes.
        if (local().Contains(grant.Source.Id)) return old;
        grant.Mutated = true;
        return value;
    }
    private void CreateReward(Action<FsmStateAction> orig, FsmStateAction stateAction) {
        var action = (CreateObject)stateAction;
        if (!Active || action.gameObject?.Value == null) { orig(action); return; }
        var prefab = action.gameObject.Value;
        var name = NativeName(prefab.name);
        var isUi = name == "Heart Container UI" || name == "Vessel Fragment UI";
        var kind = name == "Heart Piece Instant" || name == "Heart Container UI" ? ResourceKind.MaskShard :
            name == "Vessel Fragment Instant" || name == "Vessel Fragment UI" ? (ResourceKind?)ResourceKind.VesselFragment : null;
        if (!kind.HasValue) { orig(action); return; }
        var source = isUi ? Resolve(action.Fsm, kind.Value) ?? (creating?.Kind == kind ? creating : null) : ResolveCreated(action.Fsm, kind.Value);
        if (source == null) { orig(action); return; }
        // The original UI must still be created after its numeric award has
        // been recorded; only a new instant acquisition may be redundant.
        if (!isUi && local().Contains(source.Id)) { action.Finish(); return; }
        var previous = creating; creating = source; var flow = BeginNativeFlow(source);
        try {
            orig(action);
            var spawned = action.storeObject?.Value;
            if (spawned != null) {
                var tag = spawned.GetComponent<ResourceSourceTag>() ?? spawned.AddComponent<ResourceSourceTag>();
                tag.SourceId = source.Id;
                if (isUi) flow.RewardUi.Add(spawned);
            } else {
                log("Resource reward was not captured: " + source.Id);
            }
        } finally { creating = previous; }
    }
    private ResourceSource ResolveCreated(Fsm fsm, ResourceKind kind) {
        if (fsm?.Name == "Confirm Control") return ShopSource(fsm, kind);
        if (fsm?.GameObject?.scene.name == "RestingGrounds_07" && fsm.GameObject.name == "Dream Moth" && fsm.Name == "Conversation Control") {
            var expected = kind == ResourceKind.MaskShard ? "Get Mask Frag" : "Get Vessel Frag";
            if (fsm.ActiveState?.Name == expected) {
                ResourceCatalog.TryGet(kind == ResourceKind.MaskShard ? "mask:dreamReward7" : "vessel:dreamReward5", out var source);
                return source;
            }
        }
        return null;
    }
    private ResourceSource ShopSource(Fsm fsm, ResourceKind kind) {
        var flag = fsm.Variables.FindFsmString("PD Bool Name")?.Value;
        return ResourceCatalog.TryPlayerFlag(flag, out var source) && source.Kind == kind ? source : null;
    }
    private ResourceSource Resolve(Fsm fsm, ResourceKind kind) {
        if (fsm?.GameObject == null) return null;
        var owner = fsm.GameObject;
        var tag = owner.GetComponent<ResourceSourceTag>();
        if (tag != null && ResourceCatalog.TryGet(tag.SourceId, out var tagged) && tagged.Kind == kind) return tagged;
        if (fsm.Name == "Confirm Control") return ShopSource(fsm, kind);
        var scene = owner.scene.name;
        // Some configured native shinies have a PD flag but no persistent
        // component. Match both source scene and resource kind; the generic
        // Shiny Control contains grant branches for unrelated item types.
        if (fsm.Name == "Shiny Control") {
            var flag = fsm.Variables.FindFsmString("PD Bool Name")?.Value;
            if (ResourceCatalog.TryPlayerFlag(flag, out var flagged) && flagged.Kind == kind && flagged.Scene == scene) return flagged;
        }
        var persistent = owner.GetComponent<PersistentBoolItem>();
        if (persistent != null) {
            var id = persistent.persistentBoolData.id;
            if (string.IsNullOrEmpty(id)) id = NativeName(owner.name);
            if (ResourceCatalog.TryPersistent(scene, id, kind, out var pickup)) return pickup;
        }
        if (scene == "Cliffs_03" && owner.name == "Vessel Fragment Stagnest (1)" && kind == ResourceKind.VesselFragment) {
            ResourceCatalog.TryGet("vessel:vesselFragStagNest", out var source); return source;
        }
        if (scene == "RestingGrounds_07" && owner.name == "Dream Moth" && fsm.Name == "Conversation Control" &&
            fsm.ActiveState?.Name == "Get Ore" && kind == ResourceKind.PaleOre) {
            ResourceCatalog.TryGet("ore:dreamReward3", out var source); return source;
        }
        return null;
    }
    private void EnterState(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, Fsm fsm, FsmState state) {
        if (!Active || applying || fsm.GameObject == null) { orig(fsm, state); return; }
        if (fsm.Name == "Confirm Control" && state.Name == "Deduct Geo and set PD") {
            var flag = fsm.Variables.FindFsmString("PD Bool Name")?.Value;
            if (ResourceCatalog.TryPlayerFlag(flag, out var bought) && (local().Contains(bought.Id) || queued.Contains(bought.Id))) {
                var reset = fsm.States.FirstOrDefault(s => s.Name == "Reset");
                if (reset != null) { orig(fsm, reset); return; }
            }
        }
        // Get is the native control-taking state. Retire an already-awarded
        // physical pickup before it can relinquish control or play a duplicate UI.
        if (state.Name == "Get" && (fsm.Name == "Heart Container Control" || fsm.Name == "Vessel Fragment Control" || fsm.Name == "Shiny Control")) {
            foreach (var kind in new[] { ResourceKind.MaskShard, ResourceKind.VesselFragment, ResourceKind.CharmNotch, ResourceKind.PaleOre }) {
                var source = Resolve(fsm, kind);
                if (source == null) continue;
                if (local().Contains(source.Id)) { fsm.GameObject.SetActive(false); return; }
                BeginNativeFlow(source); break;
            }
        }
        orig(fsm, state);
    }

    private void GrantRemote(ResourceSource source) {
        var pd = PlayerData.instance; var hero = HeroController.instance;
        switch (source.Kind) {
            case ResourceKind.PaleOre:
                pd.SetInt("ore", pd.GetInt("ore") + 1); break;
            case ResourceKind.CharmNotch:
                pd.SetInt("charmSlots", Math.Min(11, pd.GetInt("charmSlots") + 1));
                GameManager.instance.RefreshOvercharm();
                PlayMakerFSM.BroadcastEvent("CHARM INDICATOR CHECK"); break;
            case ResourceKind.MaskShard:
                pd.SetBool("heartPieceCollected", true);
                var masks = (pd.GetInt("maxHealthBase") - 5) * 4 + pd.GetInt("heartPieces");
                if (masks >= 16) break;
                var pieces = pd.GetInt("heartPieces") + 1;
                pd.SetInt("heartPieces", pieces % 4);
                if (pieces >= 4) { hero.AddToMaxHealth(1); PlayMakerFSM.BroadcastEvent("MAX HP UP"); }
                break;
            case ResourceKind.VesselFragment:
                pd.SetBool("vesselFragmentCollected", true);
                var vessels = pd.GetInt("MPReserveMax") / 33 * 3 + pd.GetInt("vesselFragments");
                if (vessels >= 9) break;
                var fragments = pd.GetInt("vesselFragments") + 1;
                pd.SetInt("vesselFragments", fragments % 3);
                if (fragments >= 3) { hero.AddToMaxMPReserve(33); PlayMakerFSM.BroadcastEvent("NEW SOUL ORB"); }
                break;
        }
    }
    private void SetNativeReceipt(ResourceSource source) {
        var before = applying; applying = true;
        try {
            if (source.PlayerFlag != null) PlayerData.instance.SetBool(source.PlayerFlag, true);
            if (source.PersistentId != null) SceneData.instance.SaveMyState(new PersistentBoolData {
                sceneName = source.Scene, id = source.PersistentId, activated = true, semiPersistent = false
            });
        } finally { applying = before; }
    }
    private void RefreshScene() {
        var scene = Scenes.GetActiveScene();
        foreach (var persistent in Object.FindObjectsOfType<PersistentBoolItem>(true)) {
            if (persistent.gameObject.scene != scene) continue;
            var id = persistent.persistentBoolData.id;
            if (string.IsNullOrEmpty(id)) id = NativeName(persistent.name);
            foreach (var kind in new[] { ResourceKind.MaskShard, ResourceKind.VesselFragment, ResourceKind.CharmNotch, ResourceKind.PaleOre }) {
                if (!ResourceCatalog.TryPersistent(scene.name, id, kind, out var source) || !local().Contains(source.Id) || inFlight.ContainsKey(source.Id)) continue;
                // SaveState reads this FSM field, not just persistentBoolData.
                // Commit both before OnDisable/unload can overwrite the marker.
                foreach (var nativeFsm in persistent.GetComponents<PlayMakerFSM>()) {
                    var activated = nativeFsm.FsmVariables.FindFsmBool("Activated");
                    if (activated != null) activated.Value = true;
                }
                persistent.persistentBoolData.activated = true;
                persistent.gameObject.SetActive(false);
            }
        }
        // Stagnest's native vessel does not have PersistentBoolItem.
        if (scene.name == "Cliffs_03" && local().Contains("vessel:vesselFragStagNest")) {
            var stagVessel = GameObject.Find("Vessel Fragment Stagnest (1)");
            if (stagVessel != null && stagVessel.scene == scene) stagVessel.SetActive(false);
        }
        foreach (var shiny in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene == scene && f.FsmName == "Shiny Control")) {
            var flag = shiny.FsmVariables.FindFsmString("PD Bool Name")?.Value;
            if (ResourceCatalog.TryPlayerFlag(flag, out var source) && source.Scene == scene.name &&
                local().Contains(source.Id) && !inFlight.ContainsKey(source.Id)) shiny.gameObject.SetActive(false);
        }
        // A world ore/shard pickup must not rebuild an unrelated open shop.
        if (dirtyShop.Count == 0) return;
        foreach (var confirm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene == scene && f.FsmName == "Confirm Control")) {
            var flag = confirm.FsmVariables.FindFsmString("PD Bool Name")?.Value;
            if (ResourceCatalog.TryPlayerFlag(flag, out var source) && dirtyShop.Contains(source.Id) && local().Contains(source.Id) && confirm.gameObject.activeInHierarchy)
                confirm.SetState("Reset");
        }
        foreach (var itemList in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene == scene && f.FsmName == "Item List Control" && f.gameObject.activeInHierarchy))
            itemList.SendEvent("RESET");
        dirtyShop.Clear();
    }
}

internal sealed class ResourceSourceTag : MonoBehaviour {
    internal string SourceId;
}
