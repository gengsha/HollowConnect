using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowConnect;

// Pantheon progress is runtime context, separate from shared ability grants.
// Warping must restore it before the destination controller's Awake/Setup.
internal static class BossTravel {
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly FieldInfo Sequence = typeof(BossSequenceController).GetField("currentSequence", Flags);
    private static readonly FieldInfo Index = typeof(BossSequenceController).GetField("bossIndex", Flags);
    private static readonly FieldInfo Data = typeof(BossSequenceController).GetField("currentData", Flags);
    private static readonly MethodInfo Setup = typeof(BossSequenceController).GetMethod("SetupBossScene", Flags);
    private static readonly FieldInfo Scenes = typeof(BossSequence).GetField("bossScenes", BindingFlags.Instance | BindingFlags.NonPublic);
    private sealed class Step { public string Name = "", Scene = ""; public bool Load; }
    private static Step[] plan;
    private static string planSequence;
    private static BossSceneController configured;
    private static string statueKey = "";
    private static Action transientRestore;
    internal static string CurrentSequenceName {
        get {
            try { return ((BossSequence)Sequence.GetValue(null))?.name ?? ""; }
            catch { return ""; }
        }
    }
    internal static bool HasTransientContext => transientRestore != null;
    internal static void Register() { On.BossSequence.GetBossScene += GetBoss; On.BossSequence.CanLoad += CanLoad; On.BossChallengeUI.LoadBoss_int_bool += LoadHall; }
    internal static void Unregister() {
        RestoreTransientContext();
        On.BossSequence.GetBossScene -= GetBoss; On.BossSequence.CanLoad -= CanLoad; On.BossChallengeUI.LoadBoss_int_bool -= LoadHall;
        plan = null; configured = null; statueKey = "";
    }
    // Remote boss context is a runtime overlay. Keep one pre-overlay snapshot
    // until the caller has either completed the native sequence or accepted a
    // native scene load. This is the last line of defence when a disconnect or
    // exception interrupts a synchronizer tick.
    internal static void RememberTransientContext() {
        if (transientRestore == null && PlayerData.instance != null) transientRestore = SaveState();
    }
    internal static void CommitTransientContext() { transientRestore = null; }
    internal static void RestoreTransientContext() {
        var restore = transientRestore; transientRestore = null;
        if (restore == null) return;
        try { restore(); } catch (Exception e) { UnityEngine.Debug.LogError("HollowConnect boss context restore: " + e); }
    }
    // Emergency recovery abandons the current room/sequence deliberately.
    // Clear only runtime BossTravel/BossSequence state so the next native load
    // cannot re-enter a stale Pantheon or dream callback. Completion fields in
    // PlayerData remain untouched.
    internal static void ResetForEmergency() {
        RestoreTransientContext();
        try {
            if (BossSequenceController.IsInSequence) {
                BossSequenceController.RestoreBindings();
            }
        } catch (Exception e) { UnityEngine.Debug.LogError("HollowConnect emergency boss reset failed: " + e); }
        BossSequenceController.Reset();
        PlayerData.instance.SetVariable<BossSequenceController.BossSequenceData>("currentBossSequence", null);
        if (BossSceneController.Instance != null)
            typeof(BossSceneController).GetField("restoreBindingsOnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(BossSceneController.Instance, false);
        plan = null; planSequence = null; configured = null; statueKey = "";
        BossSceneController.SetupEvent = null;
    }
    private static void LoadHall(On.BossChallengeUI.orig_LoadBoss_int_bool orig, BossChallengeUI self, int level, bool hide) {
        orig(self, level, hide);
        var statue = (BossStatue)typeof(BossChallengeUI).GetField("bossStatue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(self);
        statueKey = statue == null ? "" : statue.UsingDreamVersion ? statue.dreamStatueStatePD : statue.statueStatePD;
    }
    private static void RemoveNativeCompletion(BossSceneController controller, string eventName) {
        var field = typeof(BossSceneController).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        var value = field.GetValue(controller) as Action;
        if (value == null) return;
        foreach (var callback in value.GetInvocationList()) {
            var type = callback.Method.DeclaringType?.FullName ?? "";
            if (type.StartsWith("BossSequenceController+") || type.StartsWith("BossChallengeUI+") || type.StartsWith("HollowConnect.BossTravel+") ||
                ReferenceEquals(callback.Target, controller) && callback.Method.Name == "DoDreamReturn") value = (Action)Delegate.Remove(value, callback);
        }
        field.SetValue(controller, value);
    }
    private static void ConfigureHall(BossSceneController self, Message m) {
        RemoveNativeCompletion(self, "OnBossSceneComplete"); RemoveNativeCompletion(self, "OnBossesDead");
        self.BossLevel = m.BossLevel; self.DreamReturnEvent = "DREAM RETURN";
        self.OnBossSceneComplete += self.DoDreamReturn;
        var field = typeof(PlayerData).GetField(m.BossStatueKey);
        if (field?.FieldType == typeof(BossStatue.Completion)) self.OnBossesDead += () => {
            var state = PlayerData.instance.GetVariable<BossStatue.Completion>(m.BossStatueKey);
            if (m.BossLevel == 0) state.completedTier1 = true;
            if (m.BossLevel == 1) state.completedTier2 = true;
            if (m.BossLevel == 2) state.completedTier3 = true;
            PlayerData.instance.SetVariable(m.BossStatueKey, state);
            PlayerData.instance.SetString("currentBossStatueCompletionKey", m.BossStatueKey); PlayerData.instance.SetInt("bossStatueTargetLevel", m.BossLevel);
        };
    }
    private static BossScene Resolve(BossSequence sequence, Step step) => ((BossScene[])Scenes.GetValue(sequence))
        .SelectMany(s => s.baseBoss == null ? new[] { s } : new[] { s, s.baseBoss }).FirstOrDefault(s => s.name == step.Name && s.sceneName == step.Scene);
    private static BossScene GetBoss(On.BossSequence.orig_GetBossScene orig, BossSequence self, int index) =>
        plan != null && self.name == planSequence && index >= 0 && index < plan.Length ? Resolve(self, plan[index]) ?? orig(self, index) : orig(self, index);
    private static bool CanLoad(On.BossSequence.orig_CanLoad orig, BossSequence self, int index) =>
        plan != null && self.name == planSequence && index >= 0 && index < plan.Length ? plan[index].Load : orig(self, index);
    private static bool Matches(BossSequence sequence, int index, string scene) => sequence != null && index >= 0 && index < sequence.Count && sequence.GetSceneAt(index) == scene;
    private static bool NeedsSequenceSetup(BossSceneController controller, BossSequence sequence, int index) {
        if (controller.customExitPoint != null) {
            // Native rest-room setup already advances the index to its exit.
            // Reusing that setup must not increment it a second time.
            return !Matches(sequence, index - 1, controller.gameObject.scene.name) || index >= sequence.Count ||
                controller.customExitPoint.targetScene != sequence.GetSceneAt(index) || controller.customExitPoint.entryPoint != "door_dreamEnter";
        }
        var callbacks = typeof(BossSceneController).GetField("OnBossSceneComplete", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller) as Action;
        return callbacks == null || !callbacks.GetInvocationList().Any(d => (d.Method.DeclaringType?.FullName ?? "").StartsWith("BossSequenceController+"));
    }
    internal static bool Capture(Message m) {
        if (BossSceneController.Instance != null) m.BossLevel = BossSceneController.Instance.BossLevel;
        if (m.BossLevel >= 0 || BossSequenceController.IsInSequence) {
            m.BossReturnScene = PlayerData.instance.GetString("dreamReturnScene") ?? "";
            m.BossReturnGate = PlayerData.instance.GetString("bossReturnEntryGate") ?? "";
        }
        if (m.BossLevel >= 0 && !BossSequenceController.IsInSequence) {
            m.BossStatueKey = statueKey;
        }
        if (!BossSequenceController.IsInSequence) return true;
        var sequence = (BossSequence)Sequence.GetValue(null);
        var index = (int)Index.GetValue(null);
        var data = (BossSequenceController.BossSequenceData)Data.GetValue(null);
        if (data == null || !(Matches(sequence, index, m.Scene) || Matches(sequence, index - 1, m.Scene))) return false;
        m.BossSequence = sequence.name; m.BossIndex = index;
        m.BossBindings = (int)data.bindings; m.BossDoor = data.playerData;
        m.BossPlan = JsonConvert.SerializeObject(Enumerable.Range(0, sequence.Count).Select(i => new Step {
            Name = sequence.GetBossScene(i).name, Scene = sequence.GetSceneAt(i), Load = sequence.CanLoad(i) }).ToArray());
        return true;
    }
    internal static string CaptureContext() { var m = new Message { Scene = Hkmp.CoopContext.LocalScene }; return Capture(m) ? JsonConvert.SerializeObject(m) : null; }
    internal static bool ApplyContext(string context) {
        if (string.IsNullOrEmpty(context) || context.Length > 32768) return false;
        var m = JsonConvert.DeserializeObject<Message>(context);
        if (m == null || m.Scene != Hkmp.CoopContext.LocalScene) return false;
        // A rejected update must restore only its own changes, preserving any
        // earlier accepted overlay instead of rolling the entire room back.
        var rollback = SaveState();
        var previousRestore = transientRestore;
        try {
            var applied = Prepare(m);
            if (!applied) { rollback(); transientRestore = previousRestore; }
            return applied;
        } catch {
            rollback(); transientRestore = previousRestore;
            throw;
        }
    }
    internal static Action SaveState() {
        var sequence = Sequence.GetValue(null); var data = JsonConvert.DeserializeObject<BossSequenceController.BossSequenceData>(JsonConvert.SerializeObject(Data.GetValue(null))); var index = Index.GetValue(null);
        var setup = BossSceneController.SetupEvent; var oldPlan = plan; var oldName = planSequence; var oldConfigured = configured;
        var oldStatue = statueKey; var oldController = BossSceneController.Instance;
        var restoreField = typeof(BossSceneController).GetField("restoreBindingsOnDestroy", BindingFlags.Instance | BindingFlags.NonPublic);
        var oldRestore = oldController != null && (bool)restoreField.GetValue(oldController);
        var pd = PlayerData.instance;
        var charms = pd.GetVariable<System.Collections.Generic.List<int>>("equippedCharms").ToArray();
        var equipped = Enumerable.Range(1, 40).Select(i => pd.GetBool("equippedCharm_" + i)).ToArray();
        var overcharmed = pd.GetBool("overcharmed"); var health = pd.GetInt("health"); var blue = pd.GetInt("healthBlue");
        var mp = pd.GetInt("MPCharge"); var reserve = pd.GetInt("MPReserve"); var gate = pd.GetString("bossReturnEntryGate");
        var returnScene = pd.GetString("dreamReturnScene");
        var saved = PlayerData.instance.GetVariable<BossSequenceController.BossSequenceData>("currentBossSequence");
        return () => {
            Sequence.SetValue(null, sequence); Data.SetValue(null, data); Index.SetValue(null, index); BossSceneController.SetupEvent = setup;
            plan = oldPlan; planSequence = oldName; configured = oldConfigured;
            statueKey = oldStatue; if (oldController != null) restoreField.SetValue(oldController, oldRestore);
            if (pd != null) {
                pd.SetVariable("equippedCharms", charms.ToList()); pd.SetBool("overcharmed", overcharmed);
                for (var i = 1; i <= 40; i++) pd.SetBool("equippedCharm_" + i, equipped[i - 1]);
                HeroController.instance?.CharmUpdate(); pd.SetInt("health", health); pd.SetInt("healthBlue", blue); pd.SetInt("MPCharge", mp); pd.SetInt("MPReserve", reserve); pd.SetString("bossReturnEntryGate", gate);
                pd.SetVariable("currentBossSequence", saved);
                pd.SetString("dreamReturnScene", returnScene);
            }
            try { EventRegister.SendEvent(BossSequenceController.BoundNail ? "SHOW BOUND NAIL" : "HIDE BOUND NAIL"); } catch { }
            try { EventRegister.SendEvent(BossSequenceController.BoundCharms ? "SHOW BOUND CHARMS" : "HIDE BOUND CHARMS"); } catch { }
            try { EventRegister.SendEvent(BossSequenceController.BoundSoul ? "BIND VESSEL ORB" : "UNBIND VESSEL ORB"); } catch { }
        };
    }
    internal static bool Prepare(Message m) {
        if (m.BossLevel < -1 || m.BossLevel > 2 || m.BossBindings < 0 || m.BossBindings > 15 || m.BossIndex < 0 || m.BossIndex > 256) return false;
        if (!Application.CanStreamedLevelBeLoaded(m.Scene)) return false;
        // The native result screen reads these together. A guest joining by
        // warp never runs the source door's actions that normally set them.
        // Empty legacy/test context leaves the local pair intact; a partial
        // or invalid route must not replace a usable return destination.
        var hasReturn = m.BossReturnScene.Length != 0;
        if (hasReturn && (m.BossReturnGate.Length == 0 ||
            !Application.CanStreamedLevelBeLoaded(m.BossReturnScene) || Hkmp.Util.SceneUtil.IsNonGameplayScene(m.BossReturnScene))) return false;
        var sameRoom = Hkmp.CoopContext.LocalScene == m.Scene;
        var controller = sameRoom ? BossSceneController.Instance : null;
        // Same-room heartbeats cannot turn an active Pantheon into a statue
        // fight or reconfigure a controller whose native completion has begun.
        if (sameRoom && BossSequenceController.IsInSequence && m.BossSequence.Length == 0) return false;
        if (controller != null && (bool)typeof(BossSceneController).GetField("endedScene", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller)) return false;
        RememberTransientContext();
        if (m.BossSequence.Length == 0) {
            if (!sameRoom && BossSceneController.Instance != null)
                typeof(BossSceneController).GetField("restoreBindingsOnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(BossSceneController.Instance, false);
            if (BossSequenceController.IsInSequence) {
                BossSequenceController.RestoreBindings(); BossSequenceController.Reset();
                BossSceneController.SetupEvent = null;
                PlayerData.instance.SetVariable<BossSequenceController.BossSequenceData>("currentBossSequence", null);
            }
            plan = null; planSequence = null;
            PlayerData.instance.SetVariable<BossSequenceController.BossSequenceData>("currentBossSequence", null);
            if (m.BossLevel >= 0) {
                statueKey = m.BossStatueKey;
                if (hasReturn) PlayerData.instance.SetString("dreamReturnScene", m.BossReturnScene);
                if (m.BossReturnGate.Length != 0) PlayerData.instance.SetString("bossReturnEntryGate", m.BossReturnGate);
                if (controller != null) ConfigureHall(controller, m);
                else BossSceneController.SetupEvent = self => ConfigureHall(self, m);
                StaticVariableList.SetValue("bossSceneToLoad", m.Scene);
                var asset = Resources.FindObjectsOfTypeAll<BossScene>().FirstOrDefault(s => s.sceneName == m.Scene || s.Tier1Scene == m.Scene || s.Tier2Scene == m.Scene || s.Tier3Scene == m.Scene);
                if (asset != null) BossStatueLoadManager.RecordBossScene(asset);
            }
            return true;
        }
        var sequence = (BossSequence)Sequence.GetValue(null);
        if (sequence == null || sequence.name != m.BossSequence) sequence = Resources.Load<BossSequence>("GG/" + m.BossSequence);
        if (sequence == null || m.BossPlan.Length > 24576) return false;
        var incoming = JsonConvert.DeserializeObject<Step[]>(m.BossPlan);
        if (incoming == null || incoming.Length != sequence.Count || incoming.Any(s => s == null || Resolve(sequence, s) == null)) return false;
        if (!(m.BossIndex < incoming.Length && incoming[m.BossIndex].Scene == m.Scene || m.BossIndex > 0 && m.BossIndex <= incoming.Length && incoming[m.BossIndex - 1].Scene == m.Scene)) return false;
        if (!sameRoom && BossSceneController.Instance != null)
            typeof(BossSceneController).GetField("restoreBindingsOnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(BossSceneController.Instance, false);
        var data = (BossSequenceController.BossSequenceData)Data.GetValue(null);
        var changed = !BossSequenceController.IsInSequence || data == null || data.bossSequenceName != m.BossSequence || (int)data.bindings != m.BossBindings || data.playerData != m.BossDoor;
        plan = incoming; planSequence = m.BossSequence;
        if (hasReturn) {
            PlayerData.instance.SetString("dreamReturnScene", m.BossReturnScene);
            PlayerData.instance.SetString("bossReturnEntryGate", m.BossReturnGate);
        }
        if (changed) {
            if (BossSequenceController.IsInSequence) BossSequenceController.RestoreBindings();
            BossSequenceController.SetupNewSequence(sequence, (BossSequenceController.ChallengeBindings)m.BossBindings, m.BossDoor);
        }
        Index.SetValue(null, m.BossIndex);
        PlayerData.instance.SetVariable("currentBossSequence", (BossSequenceController.BossSequenceData)Data.GetValue(null));
        if (!sameRoom || changed || controller != null && NeedsSequenceSetup(controller, sequence, m.BossIndex)) {
            Setup.Invoke(null, null);
            var native = BossSceneController.SetupEvent;
            if (sameRoom && controller != null) {
                RemoveNativeCompletion(controller, "OnBossSceneComplete"); RemoveNativeCompletion(controller, "OnBossesDead");
                BossSceneController.SetupEvent = null; native(controller); configured = controller;
                if (changed && m.BossIndex > 0) BossSequenceController.ApplyBindings();
            } else if (!sameRoom && changed && m.BossIndex > 0) {
                // Bind only after the accepted destination controller exists.
                BossSceneController.SetupEvent = self => { native(self); BossSequenceController.ApplyBindings(); configured = self; };
            }
        }
        if (controller != null && m.BossLevel >= 0) controller.BossLevel = m.BossLevel;
        return true;
    }
}
