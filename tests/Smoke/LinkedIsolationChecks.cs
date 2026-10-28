using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowConnect;
using Modding;
using Modding.Patches;
using Newtonsoft.Json;
using UnityEngine;
using Scenes = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator LinkedIsolationChecks() {
        var pd = PlayerData.instance;
        if (LinkedSpecial.StartsWith("mismatch-", StringComparison.Ordinal)) {
            if (!host) {
                yield return Until(() => !engine.NetClient.IsConnected, "mismatched save rejected by closing transport");
                ResourceCheck(pd.permadeathMode == (LinkedSpecial == "mismatch-normal" ? 1 : 0) && pd.maxHealth == 5 && pd.health == 5,
                    "type rejection leaves save type and personal masks intact");
                Flag("guest.special.rejected");
            } else {
                yield return Until(() => Has("guest.special.rejected"), "guest checked rejection");
                ResourceCheck(runtime.Connected && pd.GetInt("health") == 7 && pd.permadeathMode == (LinkedSpecial == "mismatch-steel" ? 1 : 0),
                    "host room survives mismatched join without healing or changing type");
            }
            yield break;
        }
        yield return Until(() => Field<bool>(runtime, "linkedHealthStateReceived"), "special authoritative state");
        if (LinkedSpecial == "host-first") {
            ResourceCheck(pd.GetInt("health") == 6 && pd.GetInt("maxHealth") == 9,
                "first guest uses host native partial-save baseline before host handshake");
            yield return ResourceBarrier("host-baseline");
            if (!host) typeof(CoopRuntime).GetMethod("Send", ResourcePrivate).Invoke(runtime, new object[] {
                new Message { Kind = Kind.LinkedHealthInit, LinkedHealth = 9, LinkedMaxHealth = 9 } });
            yield return new WaitForSecondsRealtime(.5f);
            ResourceCheck(pd.GetInt("health") == 6 && pd.GetInt("maxHealth") == 9, "guest reinitialization cannot replace host bar");
            yield return ResourceBarrier("host-immutable"); yield break;
        }
        yield return Until(() => pd.GetInt("health") == 7, "special initial bar");
        yield return ResourceBarrier("special-joined");
        if (LinkedSpecial == "zero-drop") {
            var origin = Scenes.GetActiveScene().handle;
            if (!host) {
                using (new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("ReceiveLinkedHealthState", ResourcePrivate),
                    new Action<Action<CoopRuntime, Message>, CoopRuntime, Message>((orig, self, message) => {
                        orig(self, message);
                        if (message.LinkedHealth == 0 && runtime.Connected) engine.ClientManager.Disconnect();
                    }))) {
                    Flag("guest.special.zero-armed");
                    yield return Until(() => !runtime.Connected, "disconnect in authoritative zero callback before next runtime tick");
                }
                yield return Until(() => Scenes.GetActiveScene().handle != origin && runtime.Playable && !HeroController.instance.cState.dead && pd.health == 5,
                    "accepted zero still completes native death after same-frame disconnect", 60);
            } else {
                yield return Until(() => Has("guest.special.zero-armed"), "zero callback disconnection armed"); pd.TakeHealth(99);
                yield return Until(() => runtime.Resetting, "remaining host enters checkpoint barrier");
                yield return Until(() => !runtime.Resetting && runtime.Playable && pd.GetInt("health") == 7,
                    "remaining host can finish wipe after zero-callback disconnect", 60);
            }
            yield return ResourceBarrier("zero-drop-done"); yield break;
        }
        if (LinkedSpecial.StartsWith("steel", StringComparison.Ordinal)) {
            ResourceCheck(pd.permadeathMode == 1 && Field<int>(runtime, "linkedSaveMode") == 1, "steel members align without rewriting type");
            var dream = LinkedSpecial == "steel-dream" || LinkedSpecial == "steel-mixed" && !host;
            if (dream) {
                pd.SetString("dreamReturnScene", "Crossroads_10");
                GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = "Dream_01_False_Knight", EntryGateName = "door1",
                    Visualization = GameManager.SceneLoadVisualizations.Dream, WaitForSceneTransitionCameraFade = true });
                yield return Until(() => Scenes.GetActiveScene().name == "Dream_01_False_Knight" && runtime.Playable && HeroController.instance.acceptingInput,
                    "native dream entrance before steel death", 30);
                ResourceCheck(GameManager.instance.GetCurrentMapZone() == "DREAM_WORLD", "native map zone identifies dream death route");
                Freeze();
            }
            yield return ResourceBarrier("steel-death-positions");
            if (host) pd.TakeHealth(99);
            if (LinkedSpecial == "steel-dream") {
                yield return Until(() => runtime.Resetting, "all dream steel party starts recoverable native death barrier", 20);
                yield return Until(() => !runtime.Resetting && runtime.Playable && Scenes.GetActiveScene().name == "Crossroads_10" && pd.GetInt("health") == 7,
                    "all dream steel party returns and refills without terminal death", 70);
                ResourceCheck(pd.permadeathMode == 1 && runtime.Connected, "native dream steel retry preserves live save type and room");
            } else if (dream) {
                yield return Until(() => Scenes.GetActiveScene().name == "Crossroads_10" && runtime.Playable && !runtime.Connected,
                    "mixed steel party dream member completes native return and leaves terminal room", 60);
                ResourceCheck(pd.permadeathMode == 1, "mixed dream member is not incorrectly marked permanently dead");
            } else {
                yield return Until(() => pd.permadeathMode == 2, "native steel death commits to disposable save", 20);
                yield return Until(() => Scenes.GetActiveScene().name == "PermaDeath" && !runtime.Connected,
                    "steel death reaches native death screen and leaves terminal room", 60);
            }
            ResourceCheck(!runtime.Resetting && !runtime.Down, "steel death leaves no checkpoint barrier or locked controls");
            yield return ResourceBarrier("steel-ended"); yield break;
        }
        if (LinkedSpecial == "host-swap") {
            if (host) CoopMod.Statistics = new HollowConnect.Core.DeathStatistics();
            yield return Until(() => !runtime.Connected, "host save identity change closes linked room", 30);
            ResourceCheck(!runtime.LinkedHealthRuleEnabled && pd.GetInt("maxHealth") == 5 && pd.GetInt("health") == 5,
                "save change clears transient health and hooks");
            yield return ResourceBarrier("host-swap-ended"); yield break;
        }
        ResourceCheck(LinkedSpecial == "save-off", "recognized isolation specialty");
        ResourceCheck(pd.equippedCharms.SequenceEqual(new[] { 1 }) && !pd.equippedCharm_23, "pre-equipped heart suspended");
        pd.SetInt("healthBlue", 0);
        if (host) pd.TakeHealth(2);
        yield return Until(() => pd.GetInt("health") == 5, "shared damage before save");
        var sequence = ScriptableObject.CreateInstance<BossSequence>();
        BossSequenceController.SetupNewSequence(sequence, BossSequenceController.ChallengeBindings.Shell, "bossDoorStateTier1");
        ResourceCheck(!BossSequenceController.IsInSequence && !BossSequenceController.BoundShell && HeroController.instance.acceptingInput,
            "native setup refuses life binding without starting or mutating a sequence");
        UnityEngine.Object.Destroy(sequence);
        pd.SetBool("hasDash", true);
        var save = new SaveGameData(pd, SceneData.instance);
        typeof(CoopRuntime).GetMethod("SaveLinkedHealth", ResourcePrivate).Invoke(runtime, new object[] { save });
        var path = Path.Combine(root, role + ".linked-save.json");
        var jsonSettings = new JsonSerializerSettings { ContractResolver = ShouldSerializeContractResolver.Instance,
            TypeNameHandling = TypeNameHandling.Auto, ObjectCreationHandling = ObjectCreationHandling.Replace,
            Converters = JsonConverterTypes.ConverterTypes };
        File.WriteAllText(path, JsonConvert.SerializeObject(save, jsonSettings));
        var loaded = JsonConvert.DeserializeObject<SaveGameData>(File.ReadAllText(path), jsonSettings);
        ResourceCheck(loaded.playerData.maxHealthBase == 5 && loaded.playerData.maxHealth == 7 && loaded.playerData.health == 7 &&
            loaded.playerData.permadeathMode == 0 && loaded.playerData.equippedCharms.SequenceEqual(new[] { 1, 8, 23, 29 }) &&
            loaded.playerData.hasDash && pd.GetInt("health") == 5, "serialized save retains personal bar, original charms and real progress independently of shared damage");
        yield return ResourceBarrier("isolation-saved");
        engine.ClientManager.Disconnect(); if (host) engine.ServerManager.Stop();
        CoopMod.Settings.LinkedHealth = false;
        PlayerData.instance = loaded.playerData; GameManager.instance.playerData = loaded.playerData;
        SceneData.instance = loaded.sceneData; GameManager.instance.sceneData = loaded.sceneData;
        GameManager.instance.ReadyForRespawn(false);
        yield return Until(() => runtime.Playable && Scenes.GetActiveScene().name == "Town", "deserialized personal save returns through native respawn", 40);
        pd = PlayerData.instance;
        ResourceCheck(!runtime.LinkedHealthRuleEnabled && pd.maxHealthBase == 5 && pd.GetInt("maxHealth") == 7 && pd.GetInt("health") == 7 &&
            pd.equippedCharm_8 && pd.equippedCharm_23 && pd.equippedCharm_29 && pd.healthBlue == 2 && pd.hasDash,
            "mode off and reload retain native heart/blue effects and real progress without room bonus or shared health");
        yield return ResourceBarrier("isolation-reloaded");
    }
}
