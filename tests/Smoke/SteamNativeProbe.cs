using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowConnect.Steam;
using HollowConnect.Steam.Bridge;
using Hkmp.Game;
using Steamworks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HollowConnect.Smoke;

// Real game-owned Steam API, real single-account lobby lifecycle and native
// network-loopback socket pairs. This does NOT certify friend invitations/SDR.
public sealed class SteamNativeProbe : MonoBehaviour {
    private string root, role;
    private SteamRoomRuntime runtime;
    private string LogPath => Path.Combine(root, role + ".log");
    private void Mark(string text) => File.AppendAllText(LogPath, DateTime.UtcNow.ToString("O") + " " + text + "\n");
    private void Check(bool value, string reason) {
        if (!value) { File.WriteAllText(Path.Combine(root, role + ".failed"), reason); throw new Exception(reason); }
        Mark("PASS " + reason);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private void Invoke(string name, params object[] args) => typeof(SteamRoomRuntime).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, args);
    private IEnumerator Until(Func<bool> condition, string label, float seconds = 45) {
        var deadline = Time.realtimeSinceStartup + seconds;
        var satisfied = condition();
        while (!satisfied && Time.realtimeSinceStartup < deadline) { yield return null; satisfied = condition(); }
        Check(satisfied, label);
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE");
        Mark("Real Steam single-account fixture; save writes intercepted; no friend invitation sent");
        yield return Until(() => (runtime = UnityEngine.Object.FindObjectOfType<SteamRoomRuntime>()) != null &&
            ExternalConnectionGateway.AtTitle && UIManager.instance != null && UIManager.instance.mainMenuScreen.alpha > .99f &&
            InputHandler.Instance.acceptingInput, "native title and Steam module loaded");
        yield return new WaitForSecondsRealtime(1);
        yield return Until(() => Field<bool>(runtime, "ready"), "module detects actual game-owned Steam subsystem", 20);
        if (role != "host") {
            yield return Until(() => File.Exists(Path.Combine(root, "host.native-complete")), "host native Steam checks completed", 180);
            Check(ExternalConnectionGateway.Current.IsIdle, "idle Steam module leaves guest IP gateway available");
            Mark("FINAL PASS"); File.WriteAllText(Path.Combine(root, role + ".done"), "pass"); yield break;
        }
        yield return NativeSocketPair();
        typeof(SteamRoomRuntime).GetField("username", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, "SteamFixture");
        var nativeMenu = Environment.GetEnvironmentVariable("HOLLOWCONNECT_STEAM_NATIVE_MENU") == "1";
        if (nativeMenu) {
            var entry = UIManager.instance.mainMenuButtons.GetComponentsInChildren<MenuButton>(true).Single(b => b.name == "StartSteamMultiplayerButton");
            ExecuteEvents.Execute(entry.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            yield return Until(() => runtime.Visible && InputHandler.Instance.acceptingInput, "Steam title entry opens native room menu");
            var menu = Field<MenuScreen>(runtime, "roomMenu");
            Field<InputField>(runtime, "nameInput").text = "SteamFixture";
            var create = Field<MenuButton>(runtime, "hostButton");
            ExecuteEvents.Execute(create.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            yield return Until(() => UIManager.instance.menuState == GlobalEnums.MainMenuState.SAVE_PROFILES && InputHandler.Instance.acceptingInput,
                "native create button opens native save menu");
            var engine = (Hkmp.Game.GameManager)Modding.ModHooks.GetMod("HKMP").GetType().GetField("_gameManager", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Modding.ModHooks.GetMod("HKMP"));
            var ui = Field<Hkmp.Ui.UiManager>(engine.ClientManager, "_uiManager");
            var back = Field<EventTrigger>(ui, "_saveMenuBackTrigger");
            ExecuteEvents.Execute(back.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            yield return Until(() => runtime.Visible && UIManager.instance.currentDynamicMenu == menu && InputHandler.Instance.acceptingInput && ExternalConnectionGateway.Current.IsIdle,
                "cancelled Steam host returns to its native Steam page");
            Check(!ExternalConnectionGateway.Current.ClientActive && !ExternalConnectionGateway.Current.ServerActive, "Steam menu cancellation leaves no server or client");
            yield return Until(() => create.gameObject.activeInHierarchy && create.IsInteractable(), "native create button is available after cancellation");
            ExecuteEvents.Execute(create.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        } else Invoke("Host");
        yield return Until(() => UIManager.instance.menuState == GlobalEnums.MainMenuState.SAVE_PROFILES &&
            InputHandler.Instance.acceptingInput, "Steam host waits in native save menu");
        GameManager.instance.profileID = 99;
        GameManager.instance.StartNewGame(false, false);
        StartCoroutine(SkipOpening());
        yield return Until(() => Field<CSteamID>(runtime, "lobby").m_SteamID != 0 &&
            Field<object>(runtime, "phase").ToString() == "Playing", "real Steam lobby created through native host flow", 60);
        var lobby = Field<CSteamID>(runtime, "lobby");
        var nonce = Field<Guid>(runtime, "session");
        Check(ExternalConnectionGateway.Current.IsConnected && ExternalConnectionGateway.Current.ServerActive,
            "Steam host runs existing game client and server");
        Check(SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID() &&
            SteamMatchmaking.GetNumLobbyMembers(lobby) == 1, "real lobby has the single local test host");
        Check(SteamMatchmaking.GetLobbyData(lobby, SteamRoomRules.KeyReady) == "1" &&
            SteamMatchmaking.GetLobbyData(lobby, SteamRoomRules.KeySession) == nonce.ToString("N"), "real lobby publishes current session and readiness");
        Check(SteamFriends.GetFriendRichPresence(SteamUser.GetSteamID(), "connect") == SteamRoomRules.ConnectString(lobby.m_SteamID),
            "real Steam connect presence points to current lobby");
        Check(Field<HSteamListenSocket>(runtime, "listener").m_HSteamListenSocket != 0, "actual Steam P2P listen socket opens");
        yield return Until(() => !GameManager.instance.IsInSceneTransition && HeroController.instance != null &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Tutorial_01", "native new game reaches Tutorial", 60);
        if (nativeMenu) {
            yield return Until(() => HeroController.instance.isHeroInPosition && GameManager.instance.gameState == GlobalEnums.GameState.PLAYING &&
                InputHandler.Instance.acceptingInput && InputHandler.Instance.pauseAllowed && !PlayerData.instance.GetBool("disablePause") &&
                !GameManager.instance.TimeSlowed && !UIManager.instance.IsAnimatingMenus,
                "Steam game is ready for native pause menu");
            Mark("Native pause prerequisites ready; UI=" + UIManager.instance.uiState + "/" + UIManager.instance.menuState);
            Invoke("ToggleRoomMenu");
            yield return Until(() => runtime.Visible && InputHandler.Instance.acceptingInput, "F10 action opens native Steam menu during play");
            Check(Field<MenuButton>(runtime, "inviteButton").gameObject.activeSelf && ExternalConnectionGateway.Current.IsConnected,
                "native invite control is available while room remains connected");
            Check(TimeController.GenericTimeScale > .99f && GameManager.instance.gameState == GlobalEnums.GameState.PAUSED,
                "native Steam menu preserves the existing live multiplayer simulation");
            Invoke("CloseRoomMenu");
            yield return Until(() => !runtime.Visible && UIManager.instance.menuState == GlobalEnums.MainMenuState.PAUSE_MENU && InputHandler.Instance.acceptingInput,
                "native Steam back returns to pause menu");
            yield return GameManager.instance.PauseGameToggle();
            yield return Until(() => GameManager.instance.gameState == GlobalEnums.GameState.PLAYING, "native pause can resume after Steam page");
        }
        Invoke("BackendLost", "Native fixture simulates account offline while SteamAPI remains initialized");
        Check(ExternalConnectionGateway.Current.IsIdle && Field<CSteamID>(runtime, "lobby").m_SteamID == 0 &&
            Field<HSteamListenSocket>(runtime, "listener").m_HSteamListenSocket == 0 && Field<SteamBridgeWorker>(runtime, "worker") == null,
            "real lobby exit retires owned game session listener and bridge worker");
        Check(SteamFriends.GetFriendRichPresence(SteamUser.GetSteamID(), "connect") != SteamRoomRules.ConnectString(lobby.m_SteamID),
            "real lobby exit clears only its own connect presence");
        var reopened = SteamNetworkingSockets.CreateListenSocketP2P(SteamRoomRules.VirtualPort, 0, null);
        Check(reopened.m_HSteamListenSocket != 0, "offline cleanup releases the actual Steam virtual port for rehosting");
        SteamNetworkingSockets.CloseListenSocket(reopened);
        yield return Until(() => Field<bool>(runtime, "ready"), "Steam module recovers after simulated account-offline cleanup", 5);
        yield return GameManager.instance.ReturnToMainMenu(GameManager.ReturnToMainMenuSaveModes.DontSave);
        yield return Until(() => ExternalConnectionGateway.AtTitle, "native test returns to title");
        File.WriteAllText(Path.Combine(root, "host.native-complete"), "pass");
        Mark("FINAL PASS"); File.WriteAllText(Path.Combine(root, role + ".done"), "pass");
    }
    private IEnumerator SkipOpening() {
        var deadline = Time.realtimeSinceStartup + 90;
        while (Time.realtimeSinceStartup < deadline) {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Opening_Sequence") GameManager.instance.SkipCutscene();
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Tutorial_01") yield break;
            yield return new WaitForSecondsRealtime(1);
        }
    }
    private IEnumerator NativeSocketPair() {
        var identity = new SteamNetworkingIdentity(); identity.SetSteamID64(SteamUser.GetSteamID().m_SteamID);
        var identity2 = identity;
        Check(SteamNetworkingSockets.CreateSocketPair(out var first, out var second, true, ref identity, ref identity2),
            "old Steam SDK creates native network-loopback socket pair");
        var a = new SteamNativeLink(first, SteamUser.GetSteamID().m_SteamID);
        var b = new SteamNativeLink(second, SteamUser.GetSteamID().m_SteamID);
        try {
            yield return Until(() => a.State == SteamBridgeLinkState.Connected && b.State == SteamBridgeLinkState.Connected,
                "native pair is connected");
            for (var i = 0; i < 12; i++) {
                var payload = Enumerable.Range(0, i % 2 == 0 ? 64 : 1400).Select(n => (byte)(n + i)).ToArray();
                Check(a.Send(payload, payload.Length, i % 2 == 0), "native adapter sends packet " + i);
                byte[] received = null;
                yield return Until(() => b.TryReceive(out received), "native adapter receives packet " + i, 5);
                Check(received.SequenceEqual(payload), "native message bytes and boundaries preserved " + i);
                Check(b.Send(received, received.Length, true), "native adapter sends reverse packet " + i);
                yield return Until(() => a.TryReceive(out received), "native adapter receives reverse packet " + i, 5);
                Check(received.SequenceEqual(payload), "native reverse message preserved " + i);
            }
        } finally { a.Close(1000, "Native fixture finished"); b.Close(1000, "Native fixture finished"); }
        Check(a.State == SteamBridgeLinkState.Closed && b.State == SteamBridgeLinkState.Closed, "native adapter closes idempotently");
    }
}
