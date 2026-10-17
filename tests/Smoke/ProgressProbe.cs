using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp;
using Hkmp.Game.Client;
using HollowConnect;
using HutongGames.PlayMaker;
using Modding;
using UnityEngine;
using Scenes = UnityEngine.SceneManagement.SceneManager;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Actual native purchase FSMs in disposable clients; ProbeMod blocks save writes.
public sealed partial class ProgressProbe : MonoBehaviour {
    private string root, role;
    private bool host;
    private bool ResourcesCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESOURCE_CHECK") == "1";
    private bool TeammateDreamCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_TEAMMATE_DREAM") == "1";
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
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private void Mark(string s) => File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + s + "\n");
    private void Flag(string s) => File.WriteAllText(Path.Combine(root, s), "ready");
    private bool Has(string s) => File.Exists(Path.Combine(root, s));
    private IEnumerator Until(Func<bool> ready, string name, float seconds = 45) {
        var deadline = Time.realtimeSinceStartup + seconds;
        while (!ready() && !Has(role + ".failed") && Time.realtimeSinceStartup < deadline) yield return null;
        if (!ready()) {
            Mark("FAIL " + name); Flag(role + ".failed");
            if (ResourcesCase) {
                Mark("RESOURCE WAIT hero=" + HeroController.instance?.transform.position + " input=" + HeroController.instance?.acceptingInput +
                    " relinquished=" + HeroController.instance?.controlReqlinquished + " pause=" + PlayerData.instance?.GetBool("disablePause") +
                    " inFlight=" + (runtime == null ? -1 : ((System.Collections.IDictionary)ResourceService.GetType().GetField("inFlight", ResourcePrivate).GetValue(ResourceService)).Count));
                foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == GameManager.instance.sceneName &&
                    (f.name.Contains("Shop") || f.name == "UI List" || f.name == "Item List")))
                    Mark("RESOURCE WAIT FSM " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " state=" + f.ActiveStateName);
                throw new TimeoutException("Resource prerequisite: " + name);
            }
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null) {
                Mark("HERO pos=" + HeroController.instance?.transform.position + " control=" + HeroController.instance?.acceptingInput);
                foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.name == "Ghost Warrior NPC")) Dump(f);
            }
        }
        else Mark("PASS " + name);
    }
    private bool AliveScene(string scene) => HeroController.instance != null && HeroController.instance.isHeroInPosition &&
        !GameManager.instance.IsInSceneTransition && Scenes.GetActiveScene().name == scene &&
        HeroController.instance.acceptingInput && !HeroController.instance.cState.transitioning &&
        HeroController.instance.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION;
    private void Enter(string scene) => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
        SceneName = scene, EntryGateName = scene == "Crossroads_47" ? "right1" : "left1", Visualization = GameManager.SceneLoadVisualizations.Default,
        WaitForSceneTransitionCameraFade = true
    });
    private void Freeze() {
        var hero = HeroController.instance; hero.takeNoDamage = true;
        hero.AffectedByGravity(false); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
    }
    private bool VisiblePeer() => Field<Dictionary<ushort, ClientPlayerData>>(engine.ClientManager, "_playerData").Values.Any(p =>
        p.IsInLocalScene && p.PlayerObject != null && p.PlayerObject.activeInHierarchy &&
        p.PlayerObject.GetComponent<Renderer>().enabled && !p.PlayerObject.GetComponent<Renderer>().forceRenderingOff &&
        p.PlayerObject.GetComponent<tk2dSprite>().color.a > .1f && p.PlayerObject.transform.lossyScale.sqrMagnitude > .5f &&
        p.PlayerObject.GetComponent<MeshFilter>().sharedMesh.vertexCount > 0 &&
        p.PlayerObject.GetComponent<MeshFilter>().sharedMesh.bounds.size.sqrMagnitude > .01f);
    private string Describe(object o) {
        if (o == null) return "null";
        if (o is FsmGameObject g) return Describe(g.Value);
        if (o is FsmObject f) return Describe(f.Value);
        if (o is Object u) return u == null ? "destroyed" : u.GetType().Name + ":" + u.name;
        if (o is Array a) return "[" + string.Join(",", a.Cast<object>().Select(Describe)) + "]";
        return o.ToString();
    }
    private void Dump(PlayMakerFSM fsm) {
        var path = Path.Combine(root, role + ".fsm.txt");
        void Write(string s) => File.AppendAllText(path, s + "\n");
        Write("FSM " + Scenes.GetActiveScene().name + "/" + fsm.name + "/" + fsm.FsmName + "=" + fsm.ActiveStateName);
        Write("GLOBALS " + string.Join(",", fsm.Fsm.GlobalTransitions.Select(t => t.EventName + ":" + t.ToState)));
        foreach (var state in fsm.FsmStates) {
            Write("STATE " + state.Name + " transitions=" + string.Join(",", state.Transitions.Select(t => t.EventName + ":" + t.ToState)));
            foreach (var a in state.Actions) Write("ACTION " + a.GetType().Name + " " + string.Join(";", a.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name + "=" + Describe(f.GetValue(a)))));
        }
        foreach (var v in fsm.FsmVariables.StringVariables) Write("VAR " + v.Name + "=" + v.Value);
        foreach (var v in fsm.FsmVariables.IntVariables) Write("VAR " + v.Name + "=" + v.Value);
        foreach (var v in fsm.FsmVariables.GameObjectVariables) Write("VAR " + v.Name + "=" + Describe(v.Value));
    }
    private IEnumerator Room(string scene) {
        if (!host && Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null)
            yield return Until(() => Has("host." + scene), "host enters warrior room first");
        Enter(scene); yield return Until(() => AliveScene(scene), "entered " + scene); Freeze();
        Flag(role + "." + scene); yield return Until(() => Has((host ? "guest" : "host") + "." + scene), "both in " + scene);
        yield return Until(() => VisiblePeer() && engine.ClientManager.SceneSnapshotReady, "visible peer and committed room snapshot in " + scene);
        yield return new WaitForSecondsRealtime(1);
    }
    private PlayMakerFSM Dialogue => Object.FindObjectsOfType<PlayMakerFSM>(true).First(f => f.name == "Text YN" && f.FsmName == "Dialogue Page Control");
    private void ChooseYes() {
        var dialogue = Dialogue;
        var list = dialogue.FsmVariables.GetFsmGameObject("UI List").Value.GetComponents<PlayMakerFSM>().First(f => f.FsmName == "ui_list");
        list.FsmVariables.GetFsmInt("Current Item").Value = 1;
        dialogue.SendEvent("UI SELECTION MADE");
    }
    private IEnumerator Station() {
        var bell = GameObject.Find("Station Bell").GetComponents<PlayMakerFSM>().First(f => f.FsmName == "Stag Bell");
        var buyer = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_BUYER") ?? "host");
        var prompt = Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_PROMPT") == "1";
        var hero = HeroController.instance;
        hero.transform.position = bell.transform.position + new Vector3(1, 0, 0);
        if (buyer || prompt) {
            // Start only the native interaction; all payment/grant/animation actions remain original.
            bell.SetState("Take Control");
            yield return Until(() => bell.ActiveStateName == "Send Text" && Dialogue.ActiveStateName == "Ready for Input", "native station purchase prompt");
            if (Has(role + ".failed")) yield break;
        }
        var geo = PlayerData.instance.GetInt("geo");
        Flag(role + ".purchase-ready"); yield return Until(() => Has((host ? "guest" : "host") + ".purchase-ready"), "both ready for station grant");
        if (buyer) ChooseYes();
        yield return Until(() => PlayerData.instance.GetBool("openedCrossroads"), "station data granted over UDP");
        Mark("STATION after grant bell=" + bell.ActiveStateName + " dialogue=" + Dialogue.ActiveStateName + " geo=" + PlayerData.instance.GetInt("geo"));
        if (!buyer && Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_BASELINE") == "1") {
            if (!prompt) bell.SetState("Take Control");
            yield return Until(() => Dialogue.ActiveStateName == "Ready for Input", "baseline stale paid station still offers purchase");
            ChooseYes(); yield return new WaitForSecondsRealtime(2);
            Mark("BASELINE repeated charge=" + (geo - PlayerData.instance.GetInt("geo")));
            Mark("FAIL stale station allowed second payment"); Flag(role + ".failed"); yield break;
        }
        yield return Until(() => bell.ActiveStateName == "Opened", "station usable immediately without leaving scene", 15);
        if (Has(role + ".failed")) yield break;
        var charge = geo - PlayerData.instance.GetInt("geo");
        if (charge != (buyer ? 50 : 0) || !hero.acceptingInput || PlayerData.instance.GetBool("disablePause") ||
            Dialogue.ActiveStateName != "Idle" || !VisiblePeer()) {
            Mark("FAIL station charge/control/peer charge=" + charge + " input=" + hero.acceptingInput + " pause=" + PlayerData.instance.GetBool("disablePause") + " dialogue=" + Dialogue.ActiveStateName);
            Flag(role + ".failed"); yield break;
        }
        Mark("PASS station single native payment and recipient control/peer restored charge=" + charge);
        Flag(role + ".station-pass"); yield return Until(() => Has((host ? "guest" : "host") + ".station-pass"), "both station assertions passed");
        var actions = bell.FsmStates.First(s => s.Name == "Box Disappear Anim").Actions.Length;
        typeof(CoopRuntime).GetMethod("Send", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime,
            new object[] { new Message { Kind = Kind.ProgressOffer, Key = "openedCrossroads", Value = 1 } });
        yield return new WaitForSecondsRealtime(1);
        if (bell.ActiveStateName != "Opened" || PlayerData.instance.GetInt("geo") != geo - charge ||
            bell.FsmStates.First(s => s.Name == "Box Disappear Anim").Actions.Length != actions) {
            Mark("FAIL repeated station offer replayed payment or changed native actions"); Flag(role + ".failed"); yield break;
        }
        Mark("PASS repeated station offer leaves money and native actions unchanged");
    }
    private IEnumerator MapPurchase() {
        var buyer = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_BUYER") ?? "host");
        var cornifer = GameObject.Find("Cornifer");
        var conversation = cornifer.GetComponents<PlayMakerFSM>().First(f => f.FsmName == "Conversation Control");
        var control = cornifer.GetComponents<PlayMakerFSM>().First(f => f.FsmName == "npc_control");
        var geo = PlayerData.instance.GetInt("geo");
        if (buyer || Environment.GetEnvironmentVariable("HOLLOWCONNECT_MAP_BOTH") == "1") {
            Mark("MAP NPC position=" + cornifer.transform.position);
            HeroController.instance.transform.position = cornifer.transform.position + new Vector3(-1, 2, 0);
            control.SetState("Take Control");
            yield return Until(() => conversation.ActiveStateName == "Meet", "native first Cornifer conversation");
            if (Has(role + ".failed")) yield break;
            conversation.SendEvent("CONVO_FINISH");
            yield return Until(() => conversation.ActiveStateName == "Send Text" && Dialogue.ActiveStateName == "Ready for Input", "native map purchase prompt");
            if (Has(role + ".failed")) yield break;
            ChooseYes();
            yield return Until(() => PlayerData.instance.GetBool("mapCrossroads") && conversation.ActiveStateName == "Map Input", "first map actually bought and prompt displayed");
            if (Has(role + ".failed")) yield break;
            conversation.SendEvent("CONVO END");
            yield return Until(() => conversation.ActiveStateName == "Iselda Mention", "first map prompt closed");
            if (Has(role + ".failed")) yield break;
            conversation.SendEvent("CONVO_FINISH");
            yield return Until(() => HeroController.instance.acceptingInput && !PlayerData.instance.GetBool("disablePause"), "native map purchase returns hero control");
            Mark("MAP charge=" + (geo - PlayerData.instance.GetInt("geo")) + " state=" + conversation.ActiveStateName + " npc=" + control.ActiveStateName);
            if (geo - PlayerData.instance.GetInt("geo") != 30) { Mark("FAIL wrong map charge"); Flag(role + ".failed"); yield break; }
        } else {
            yield return Until(() => Has((host ? "guest" : "host") + ".map-bought"), "peer bought map");
            if (PlayerData.instance.GetBool("mapCrossroads") || PlayerData.instance.GetInt("geo") != geo) {
                Mark("FAIL map or currency incorrectly shared"); Flag(role + ".failed"); yield break;
            }
            Mark("PASS map and currency remain personal");
        }
        Flag(role + ".map-bought"); yield return Until(() => Has((host ? "guest" : "host") + ".map-bought"), "both map assertions done");
        yield return Until(VisiblePeer, "peer visible after actual map purchase");
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); host = role == "host";
        yield return Until(() => GameManager.instance != null && Object.FindObjectOfType<CoopRuntime>() != null && Scenes.GetActiveScene().name == "Menu_Title", "menu ready");
        if (Has(role + ".failed")) yield break;
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        Hkmp.Logging.Logger.AddLogger(new FileLogger(Path.Combine(root, role + "-engine.log")));
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null || ResourcesCase)
            On.GameManager.EnterHero += (orig, gm, additive) => {
                var gates = Object.FindObjectsOfType<TransitionPoint>();
                if (!gates.Any(g => g.name == gm.entryGateName) && gates.Length > 0) {
                    gm.entryGateName = gates.OrderBy(g => g.name, StringComparer.Ordinal).First().name;
                    Mark("NATIVE TEST ENTRANCE " + gm.sceneName + "/" + gm.entryGateName);
                }
                orig(gm, additive);
            };
        CoopMod.Settings.ShareProgress = true; CoopMod.Settings.EntryProtection = false;
        if (TeammateDreamCase) CoopMod.Settings.ShareCharms = host
            ? Environment.GetEnvironmentVariable("HOLLOWCONNECT_CHARM_SHARE") != "0"
            : Environment.GetEnvironmentVariable("HOLLOWCONNECT_CHARM_SHARE") == "0";
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1); pd.SetInt("geo", 500);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => HeroController.instance != null && HeroController.instance.isHeroInPosition && !GameManager.instance.IsInSceneTransition && Scenes.GetActiveScene().name == "Town", "fresh unsaved hero");
        if (Has(role + ".failed")) yield break;
        if ((Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null || ResourcesCase || TeammateDreamCase) && pd.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "initial native bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => !pd.GetBool("atBench") && HeroController.instance.acceptingInput &&
                !HeroController.instance.GetComponent<Rigidbody2D>().isKinematic, "initial native bench get-up complete");
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null) {
            pd.SetBool("hasDreamNail", true);
            pd.SetBool("hasLantern", true);
        }
        HeroController.instance.RegainControl(); HeroController.instance.StartAnimationControl(); Freeze();
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "server ready");
        typeof(ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "progress" + role });
        yield return Until(() => runtime.Connected, "real UDP handshake");
        if (TeammateDreamCase) {
            var checks = new System.Collections.Generic.Stack<IEnumerator>(); checks.Push(TeammateDream());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            if (Has(role + ".failed")) yield break;
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS teammate dream and charm rules"); Flag(role + ".done"); }
            yield break;
        }
        if (ResourcesCase) {
            var checks = new System.Collections.Generic.Stack<IEnumerator>(); checks.Push(Resources());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS native resources"); Flag(role + ".done"); }
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null) {
            yield return DreamWarriors();
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS native dream warriors"); Flag(role + ".done"); }
            yield break;
        }
        yield return Room("Crossroads_47");
        if (Has(role + ".failed")) yield break;
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_INSPECT") == "1") {
            foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.name == "Station Bell")) Dump(fsm);
            Dump(Dialogue);
        } else {
            yield return Station();
            if (Has(role + ".failed")) yield break;
        }
        yield return Room("Crossroads_33");
        if (Has(role + ".failed")) yield break;
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_PROGRESS_INSPECT") == "1") {
            foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.name == "Cornifer")) Dump(fsm);
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS native scene inspection only"); Flag(role + ".done"); }
            yield break;
        }
        yield return MapPurchase();
        if (Has(role + ".failed")) yield break;
        yield return Room("Crossroads_08");
        if (Has(role + ".failed")) yield break;
        yield return Room("Crossroads_30");
        if (Has(role + ".failed")) yield break;
        yield return ProbeShutdown.Run(engine, root, role);
        if (Has(role + ".failed")) yield break;
        Mark("FINAL PASS shared station refresh, native map purchase and visible peers across two subsequent rooms"); Flag(role + ".done");
    }
}
