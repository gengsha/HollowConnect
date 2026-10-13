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
    internal static void Register() { On.BossSequence.GetBossScene += GetBoss; On.BossSequence.CanLoad += CanLoad; On.BossChallengeUI.LoadBoss_int_bool += LoadHall; }
    internal static void Unregister() { On.BossSequence.GetBossScene -= GetBoss; On.BossSequence.CanLoad -= CanLoad; On.BossChallengeUI.LoadBoss_int_bool -= LoadHall; plan = null; configured = null; statueKey = ""; }
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
    internal static void Capture(Message m) {
        if (BossSceneController.Instance != null) m.BossLevel = BossSceneController.Instance.BossLevel;
        if (m.BossLevel >= 0 && !BossSequenceController.IsInSequence) {
            m.BossStatueKey = statueKey; m.BossReturnGate = PlayerData.instance.GetString("bossReturnEntryGate");
        }
        if (!BossSequenceController.IsInSequence) return;
        var sequence = (BossSequence)Sequence.GetValue(null);
        var index = (int)Index.GetValue(null);
        var data = (BossSequenceController.BossSequenceData)Data.GetValue(null);
        if (data == null || !(Matches(sequence, index, m.Scene) || Matches(sequence, index - 1, m.Scene))) return;
        m.BossSequence = sequence.name; m.BossIndex = index;
        m.BossBindings = (int)data.bindings; m.BossDoor = data.playerData;
        m.BossPlan = JsonConvert.SerializeObject(Enumerable.Range(0, sequence.Count).Select(i => new Step {
            Name = sequence.GetBossScene(i).name, Scene = sequence.GetSceneAt(i), Load = sequence.CanLoad(i) }).ToArray());
    }
    internal static string CaptureContext() { var m = new Message { Scene = Hkmp.CoopContext.LocalScene }; Capture(m); return JsonConvert.SerializeObject(m); }
    internal static bool ApplyContext(string context) {
        if (context.Length > 32768) return false;
        var m = JsonConvert.DeserializeObject<Message>(context);
        return m != null && m.Scene == Hkmp.CoopContext.LocalScene && Prepare(m);
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
        var saved = PlayerData.instance.GetVariable<BossSequenceController.BossSequenceData>("currentBossSequence");
        return () => {
            Sequence.SetValue(null, sequence); Data.SetValue(null, data); Index.SetValue(null, index); BossSceneController.SetupEvent = setup;
            plan = oldPlan; planSequence = oldName; configured = oldConfigured;
            statueKey = oldStatue; if (oldController != null) restoreField.SetValue(oldController, oldRestore);
            pd.SetVariable("equippedCharms", charms.ToList()); pd.SetBool("overcharmed", overcharmed);
            for (var i = 1; i <= 40; i++) pd.SetBool("equippedCharm_" + i, equipped[i - 1]);
            HeroController.instance.CharmUpdate(); pd.SetInt("health", health); pd.SetInt("healthBlue", blue); pd.SetInt("MPCharge", mp); pd.SetInt("MPReserve", reserve); pd.SetString("bossReturnEntryGate", gate);
            EventRegister.SendEvent(BossSequenceController.BoundNail ? "SHOW BOUND NAIL" : "HIDE BOUND NAIL");
            EventRegister.SendEvent(BossSequenceController.BoundCharms ? "SHOW BOUND CHARMS" : "HIDE BOUND CHARMS");
            EventRegister.SendEvent(BossSequenceController.BoundSoul ? "BIND VESSEL ORB" : "UNBIND VESSEL ORB");
            PlayerData.instance.SetVariable("currentBossSequence", saved);
        };
    }
    internal static bool Prepare(Message m) {
        if (m.BossLevel < -1 || m.BossLevel > 2 || m.BossBindings < 0 || m.BossBindings > 15 || m.BossIndex < 0 || m.BossIndex > 256) return false;
        if (!Application.CanStreamedLevelBeLoaded(m.Scene)) return false;
        var sameRoom = Hkmp.CoopContext.LocalScene == m.Scene;
        var controller = sameRoom ? BossSceneController.Instance : null;
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
        if (changed) {
            if (BossSequenceController.IsInSequence) BossSequenceController.RestoreBindings();
            BossSequenceController.SetupNewSequence(sequence, (BossSequenceController.ChallengeBindings)m.BossBindings, m.BossDoor);
        }
        Index.SetValue(null, m.BossIndex);
        PlayerData.instance.SetVariable("currentBossSequence", (BossSequenceController.BossSequenceData)Data.GetValue(null));
        if (!sameRoom || changed) {
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
