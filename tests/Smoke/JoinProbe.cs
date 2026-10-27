using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client;
using Hkmp.Game.Client.Entity;
using HollowConnect;
using Modding;
using UnityEngine;
using Scenes = UnityEngine.SceneManagement.SceneManager;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Covers the real menu -> connection -> new save -> opening -> Tutorial path.
// ProbeMod suppresses every SaveGame overload; no player save is read or written.
public sealed partial class JoinProbe : MonoBehaviour {
    private string role, root;
    private bool host;
    private Hkmp.Game.GameManager engine;
    private CoopRuntime runtime;
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private void Mark(string s) => File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + s + "\n");
    private void Flag(string s) => File.WriteAllText(Path.Combine(root, s), "ready");
    private bool Has(string s) => File.Exists(Path.Combine(root, s));
    private IEnumerator Until(Func<bool> condition, string name, float seconds = 45) {
        var deadline = Time.realtimeSinceStartup + seconds;
        while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
        if (!condition()) { Mark("FAIL " + name); Flag(role + ".failed"); }
        else Mark("PASS " + name);
    }
    private string Scope => "active=" + Scenes.GetActiveScene().name + " scope=" + CoopContext.LocalScene + "/" + CoopContext.LocalVisit + "/" + CoopContext.Epoch;
    private bool InTutorial => HeroController.instance != null && HeroController.instance.isHeroInPosition &&
        !GameManager.instance.IsInSceneTransition && Scenes.GetActiveScene().name == "Tutorial_01";
    private void Connect() => typeof(ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic)
        .Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "join" + role });
    private void Freeze() {
        var hero = HeroController.instance;
        hero.GetComponent<Rigidbody2D>().gravityScale = 0;
        hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        hero.takeNoDamage = true; hero.RelinquishControl();
    }
    private bool VisiblePeer() {
        var players = Field<Dictionary<ushort, ClientPlayerData>>(engine.ClientManager, "_playerData");
        return players.Values.Any(p => p.IsInLocalScene && p.PlayerObject != null && p.PlayerObject.activeInHierarchy &&
            p.PlayerObject.GetComponent<Renderer>().enabled && !p.PlayerObject.GetComponent<Renderer>().forceRenderingOff);
    }
    private IEnumerator FreshGame(string prefix) {
        yield return Until(() => runtime.Connected, prefix + " connected from menu");
        if (Has(role + ".failed")) yield break;
        yield return new WaitForSecondsRealtime(2);
        yield return CheckSaveJoinCancellation(prefix);
        if (Has(role + ".failed")) yield break;
        if (!host && prefix == "initial") {
            yield return CheckDisconnectDuringJoinPrompt();
            if (Has(role + ".failed")) yield break;
        }
        GameManager.instance.profileID = 99;
        GameManager.instance.StartNewGame(false, false);
        yield return AcceptSaveJoin(prefix);
        if (Has(role + ".failed")) yield break;
        yield return Until(() => Scenes.GetActiveScene().name == "Opening_Sequence", prefix + " opening sequence");
        if (Has(role + ".failed")) yield break;
        yield return new WaitForSecondsRealtime(3);
        StartCoroutine(SkipOpening());
        yield return Until(() => InTutorial, prefix + " new game in Tutorial");
        if (Has(role + ".failed")) yield break;
        Freeze(); Mark(prefix + " positioned " + Scope); Flag(role + "." + prefix + ".room");
    }
    private IEnumerator SkipOpening() {
        while (Scenes.GetActiveScene().name == "Opening_Sequence") {
            GameManager.instance.SkipCutscene();
            yield return new WaitForSecondsRealtime(3);
        }
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT");
        role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); host = role == "host";
        yield return Until(() => GameManager.instance != null && Object.FindObjectOfType<CoopRuntime>() != null &&
            Scenes.GetActiveScene().name == "Menu_Title", "menu ready");
        if (Has(role + ".failed")) yield break;
        runtime = Object.FindObjectOfType<CoopRuntime>();
        engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        var settings = Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings");
        settings.AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        var sharedRoom = Environment.GetEnvironmentVariable("HOLLOWCONNECT_JOIN_SHARE") == "1";
        CoopMod.Settings.ShareProgress = host ? sharedRoom : !sharedRoom;
        Hkmp.Logging.Logger.AddLogger(new FileLogger(Path.Combine(root, role + "-engine.log")));
        CustomHooks.AfterEnterSceneHeroTransformed += () => Mark("HERO POSITION EVENT " + Scope);
        Scenes.activeSceneChanged += (a, b) => Mark("SCENE " + a.name + " -> " + b.name + " " + Scope);
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("host.initial.room"), "host waiting in Tutorial");
        Connect(); yield return FreshGame("initial");
        if (Has(role + ".failed")) yield break;
        yield return Until(() => Has(host ? "guest.initial.room" : "host.initial.room"), "both players in Tutorial", 90);
        yield return Until(VisiblePeer, "initial peer has active visible knight renderer", 15);
        var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
        yield return Until(() => manager.IsSceneHostDetermined, "initial scene authority initialized", 5);
        if (Has(role + ".failed")) { Mark("FAILED SCOPE " + Scope); yield break; }
        Flag(role + ".initial.visible");
        yield return Until(() => Has(host ? "guest.initial.visible" : "host.initial.visible"), "both directions visible");
        if (host) {
            yield return Until(() => Has("guest.reconnect.room"), "guest rejoined from menu");
        } else {
            engine.ClientManager.Disconnect();
            yield return GameManager.instance.ReturnToMainMenu(GameManager.ReturnToMainMenuSaveModes.DontSave);
            yield return Until(() => Scenes.GetActiveScene().name == "Menu_Title", "guest returned to title");
            Connect(); yield return FreshGame("reconnect");
        }
        yield return Until(VisiblePeer, "reconnected peer has active visible knight renderer", 15);
        yield return Until(() => manager.IsSceneHostDetermined, "reconnected scene authority initialized", 5);
        if (Has(role + ".failed")) yield break;
        Flag(role + ".reconnect.visible");
        yield return Until(() => Has(host ? "guest.reconnect.visible" : "host.reconnect.visible"), "both reconnected directions visible");
        var oldSession = runtime.Session;
        if (host) {
            // Exercise exactly the native timeout -> title path while the
            // embedded listener is still running, then reopen the same port.
            typeof(ClientManager).GetMethod("OnTimeout", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, null);
            yield return Until(() => !runtime.Connected && Scenes.GetActiveScene().name == "Menu_Title", "host timeout returns to title");
            if (Has(role + ".failed")) yield break;
            engine.ServerManager.Start(29650, false); Flag("server.reopened");
        } else {
            yield return Until(() => Has("server.reopened") && !runtime.Connected && Scenes.GetActiveScene().name == "Menu_Title",
                "old room closes and guest returns to title");
        }
        if (Has(role + ".failed")) yield break;
        Connect(); yield return FreshGame("rehost");
        yield return Until(() => Has(host ? "guest.rehost.room" : "host.rehost.room"), "both joined reopened room");
        yield return Until(VisiblePeer, "reopened room has visible teammate", 15);
        yield return Until(() => manager.IsSceneHostDetermined && runtime.Session != oldSession,
            "reopened room has fresh session and scene authority", 5);
        if (Has(role + ".failed")) yield break;
        yield return ProbeShutdown.Run(engine,root,role);
        Mark("FINAL PASS"); Flag(role + ".done");
    }
    private sealed class FileLogger : Hkmp.Logging.ILogger {
        private readonly string path;
        private readonly object sync = new object();
        public FileLogger(string path) { this.path = path; }
        private void Write(string level, string s) { lock (sync) File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + level + " " + s + "\n"); }
        public void Info(string s) => Write("INFO", s);
        public void Debug(string s) => Write("DEBUG", s);
        public void Fine(string s) { }
        public void Warn(string s) => Write("WARN", s);
        public void Error(string s) => Write("ERROR", s);
    }
}
