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
    private bool SkinCompatibilityCase => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_CASE"));
    private bool LinkedHealthCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_CASE") != null;
    private string LinkedSpecial => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_SPECIAL")) ? null :
        Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_SPECIAL");
    private bool EmergencyBenchCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_EMERGENCY_BENCH") == "1";
    private bool DefenderCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEFENDER_CHECK") == "1";
    private bool TeammateDreamCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_TEAMMATE_DREAM") == "1";
    private bool MapExitCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_MAP_EXIT_CHECK") == "1";
    private bool RemoteFeedbackCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_REMOTE_FEEDBACK") == "1";
    private bool BankerCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_BANKER_CASE") != null;
    private bool GrimmFlameCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_GRIMM_FLAME") != null;
    private bool ColosseumProgressCase => Environment.GetEnvironmentVariable("HOLLOWCONNECT_COLOSSEUM_PROGRESS") != null;
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
            if (SkinCompatibilityCase) throw new TimeoutException("Skin compatibility prerequisite: " + name);
            if (LinkedHealthCase) throw new TimeoutException("Linked health prerequisite: " + name);
            if(SimpleKeyCase) {
                Mark("KEY WAIT keys="+PlayerData.instance.GetInt("simpleKeys")+" legacyWaterwaysKey="+PlayerData.instance.GetBool("hasWaterwaysKey"));
                foreach(var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f=>f.gameObject.scene.name==GameManager.instance.sceneName &&
                    (f.FsmName=="npc_control" || f.FsmName=="Conversation Control"))) {
                    Mark("KEY WAIT FSM "+f.name+"/"+f.FsmName+" state="+f.ActiveStateName); Dump(f);
                }
                Mark("KEY WAIT Dialogue "+Dialogue.ActiveStateName+" requester="+Dialogue.FsmVariables.FindFsmGameObject("Requester")?.Value?.name);
            }
            if (ResourcesCase || DefenderCase || ProgressAuditCase || LocalWorldCase || SimpleKeyCase || BankerCase) {
                Mark("RESOURCE WAIT hero=" + HeroController.instance?.transform.position + " input=" + HeroController.instance?.acceptingInput +
                    " relinquished=" + HeroController.instance?.controlReqlinquished + " pause=" + PlayerData.instance?.GetBool("disablePause") +
                    " inFlight=" + (runtime == null ? -1 : ((System.Collections.IDictionary)ResourceService.GetType().GetField("inFlight", ResourcePrivate).GetValue(ResourceService)).Count));
                foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.gameObject.scene.name == GameManager.instance.sceneName &&
                    (f.name.Contains("Shop") || f.name == "UI List" || f.name == "Item List")))
                    { Mark("RESOURCE WAIT FSM " + f.name + "/" + f.FsmName + " active=" + f.gameObject.activeInHierarchy + " state=" + f.ActiveStateName); if(SimpleKeyCase) Dump(f); }
                throw new TimeoutException("Resource prerequisite: " + name);
            }
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null) {
                TraceDreamLoad("timeout " + name);
                foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.name == "Ghost Warrior NPC")) Dump(f);
                throw new TimeoutException("Dream probe prerequisite: " + name);
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
        if (o is FsmBool b) return b.Name + "=" + b.Value;
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
        foreach (var v in fsm.FsmVariables.BoolVariables) Write("VAR " + v.Name + "=" + v.Value);
        foreach (var v in fsm.FsmVariables.GameObjectVariables) Write("VAR " + v.Name + "=" + Describe(v.Value));
    }
    private IEnumerator Room(string scene) {
        if (!host && (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null || DefenderCase))
            yield return Until(() => Has("host." + scene), "host enters warrior room first");
        Enter(scene); yield return Until(() => AliveScene(scene), "entered " + scene);
        if (MapExitCase) {
            var anchor = GameObject.Find(scene == "Crossroads_33" ? "Cornifer" : "Shop Region");
            if (anchor != null) HeroController.instance.transform.position = anchor.transform.position + new Vector3(-1, 1, 0);
        }
        Freeze();
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
            yield return Until(() => PlayerData.instance.GetBool("mapCrossroads") && conversation.ActiveStateName != "Send Text" &&
                (conversation.ActiveStateName == "Map Input" || HeroController.instance.acceptingInput && !PlayerData.instance.GetBool("disablePause")),
                "native map purchase or first-map prompt completes");
            if (Has(role + ".failed")) yield break;
            if (conversation.ActiveStateName == "Map Input") {
                conversation.SendEvent("CONVO END");
                yield return Until(() => conversation.ActiveStateName == "Iselda Mention", "first map prompt closed");
                if (Has(role + ".failed")) yield break;
                conversation.SendEvent("CONVO_FINISH");
            }
            yield return Until(() => HeroController.instance.acceptingInput && !PlayerData.instance.GetBool("disablePause"), "native map purchase returns hero control");
            Mark("MAP charge=" + (geo - PlayerData.instance.GetInt("geo")) + " state=" + conversation.ActiveStateName + " npc=" + control.ActiveStateName);
            var charge = geo - PlayerData.instance.GetInt("geo");
            if (charge != 30 && !(charge == 0 && Environment.GetEnvironmentVariable("HOLLOWCONNECT_MAP_BOTH") == "1")) {
                Mark("FAIL wrong map charge"); Flag(role + ".failed"); yield break;
            }
        } else {
            yield return Until(() => Has((host ? "guest" : "host") + ".map-bought"), "peer bought map");
            yield return Until(() => PlayerData.instance.GetBool("mapCrossroads") && PlayerData.instance.GetBool("hasMap"), "purchased map shared");
            if (PlayerData.instance.GetInt("geo") != geo) {
                Mark("FAIL map purchase shared currency"); Flag(role + ".failed"); yield break;
            }
            Mark("PASS purchased map shared while currency remains personal");
        }
        Flag(role + ".map-bought"); yield return Until(() => Has((host ? "guest" : "host") + ".map-bought"), "both map assertions done");
        yield return Until(VisiblePeer, "peer visible after actual map purchase");
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); host = role == "host";
        yield return Until(() => GameManager.instance != null && Object.FindObjectOfType<CoopRuntime>() != null && Scenes.GetActiveScene().name == "Menu_Title", "menu ready");
        if (Has(role + ".failed")) yield break;
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        if (MapExitCase) VerifyMenuDoesNotSave();
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null) ObserveDreamLoads();
        if (EmergencyBenchCase) yield return EmergencyMenuShortcut();
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        Hkmp.Logging.Logger.AddLogger(new FileLogger(Path.Combine(root, role + "-engine.log")));
        if (KeyEssenceCase || Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null || ResourcesCase || DefenderCase || ProgressAuditCase || LocalWorldCase || StagNestCase || MapExitCase || SimpleKeyCase || BankerCase || GrimmFlameCase || ColosseumProgressCase)
            On.GameManager.EnterHero += (orig, gm, additive) => {
                var gates = Object.FindObjectsOfType<TransitionPoint>();
                if (MapExitCase && gm.sceneName == "Crossroads_33") {
                    Mark("MAP ENTRY GATES " + string.Join(",", gates.Select(g => g.name + "=" + g.transform.position + "/" + g.GetGatePosition())));
                    var alternate = gates.FirstOrDefault(g => g.name == "right1") ?? gates.FirstOrDefault(g => g.name == "top1");
                    if (alternate != null) gm.entryGateName = alternate.name;
                }
                if (!gates.Any(g => g.name == gm.entryGateName) && gates.Length > 0) {
                    gm.entryGateName = gates.OrderBy(g => g.name, StringComparer.Ordinal).First().name;
                    Mark("NATIVE TEST ENTRANCE " + gm.sceneName + "/" + gm.entryGateName);
                }
                orig(gm, additive);
            };
        CoopMod.Settings.ShareProgress = !ProgressAuditDisabled; CoopMod.Settings.EntryProtection = false;
        if (LinkedHealthCase) {
            CoopMod.Settings.LinkedHealth = Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_CASE") != "off";
            CoopMod.Settings.LinkedHealthBonusMasks = int.Parse(Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_BONUS") ?? "2");
            CoopMod.Settings.ShareCharms = Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_CASE") != "no-charms";
            CoopMod.Settings.ShareProgress = Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_CASE") != "no-progress";
            CoopMod.Settings.Rescue = CoopMod.Settings.SpectateAfterExhaustion = true;
        }
        if (LocalWorldCase) CoopMod.Settings.ShareProgress = !host;
        if (ProgressAuditCase) CoopMod.Settings.ShareCharms = !ProgressAuditNoCharms;
        if (TeammateDreamCase) CoopMod.Settings.ShareCharms = host
            ? Environment.GetEnvironmentVariable("HOLLOWCONNECT_CHARM_SHARE") != "0"
            : Environment.GetEnvironmentVariable("HOLLOWCONNECT_CHARM_SHARE") == "0";
        if (!host && MenuResourceCase) {
            yield return MenuResourceEntry();
            if (Has(role + ".failed")) yield break;
        } else {
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1); pd.SetInt("geo", 500);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => HeroController.instance != null && HeroController.instance.isHeroInPosition && !GameManager.instance.IsInSceneTransition && Scenes.GetActiveScene().name == "Town", "fresh unsaved hero");
        if (Has(role + ".failed")) yield break;
        if ((SkinCompatibilityCase || LinkedHealthCase || KeyEssenceCase || Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null || ResourcesCase || TeammateDreamCase || DefenderCase || EmergencyBenchCase || ProgressAuditCase || LocalWorldCase || StagNestCase || MapExitCase || RemoteFeedbackCase || SimpleKeyCase || BankerCase || GrimmFlameCase || ColosseumProgressCase) && pd.GetBool("atBench")) {
            var bench = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.name == "RestBench" && f.FsmName == "Bench Control");
            yield return Until(() => bench.ActiveStateName == "Resting", "initial native bench ready");
            bench.Fsm.Event(bench.Fsm.ActiveState.Actions.OfType<HutongGames.PlayMaker.Actions.ListenForJump>().Single().wasPressed);
            yield return Until(() => !pd.GetBool("atBench") && HeroController.instance.acceptingInput &&
                !HeroController.instance.GetComponent<Rigidbody2D>().isKinematic, "initial native bench get-up complete");
        }
        if (SkinCompatibilityCase) {
            pd.SetBool("openedTown", true); pd.SetBool("openedCrossroads", true); pd.SetBool("metStag", true);
            pd.SetInt("stagPosition", 0);
            yield return PrepareCompatibilitySkin();
        }
        if (StagNestCase) {
            pd.SetBool("openedStagNest", host);
            pd.SetBool("openedHiddenStation", false);
            pd.SetBool("openedTown", host);
            pd.SetBool("metStag", true);
            pd.SetInt("stagPosition", host ? 9 : 0);
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_STAG_SPLIT") == "1") {
                for (var i = 0; i < HollowConnect.Core.StagStations.Original.Length; i++)
                    pd.SetBool(HollowConnect.Core.StagStations.Original[i], host ? i < 5 : i >= 5);
                pd.SetInt("stationsOpened", host ? 5 : 4); pd.SetBool("openedStagNest", false);
                pd.SetBool("stagRemember1", true); pd.SetBool("stagRemember2", true);
            }
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_GRUB_CHECK") == "1") {
                pd.SetInt("grubsCollected", 0); pd.scenesGrubRescued.Clear();
            }
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null) {
            pd.SetBool("hasDreamNail", true);
            pd.SetBool("hasLantern", true);
            PrepareWarriorHistory();
            var lanternOwner = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_LANTERN_OWNER");
            if (!string.IsNullOrEmpty(lanternOwner)) {
                // Establish local acquisition history separately from the real
                // UDP ability union, without changing any defeat/save flags.
                CoopMod.Statistics.NativePickups["hasDreamNail"] = 1;
                if (lanternOwner == "both" || lanternOwner == role) CoopMod.Statistics.NativePickups["hasLantern"] = 1;
                else { pd.SetBool("hasLantern", false); CoopMod.Statistics.NativePickups.Remove("hasLantern"); }
            }
        }
        HeroController.instance.RegainControl(); HeroController.instance.StartAnimationControl(); Freeze();
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_REST") == "on") {
            foreach (var id in new[] { 1, 8, 29 }) { pd.SetBool("equippedCharm_" + id, true); pd.EquipCharm(id); }
            pd.CalculateNotchesUsed(); pd.overcharmed = true; HeroController.instance.CharmUpdate();
            pd.healthBlue += 3; // Three cocoon masks above the two charm masks.
        }
        if (LinkedSpecial == "save-off") {
            foreach (var id in new[] { 1, 8, 23, 29 }) { pd.SetBool("equippedCharm_" + id, true); pd.EquipCharm(id); }
            pd.CalculateNotchesUsed(); HeroController.instance.CharmUpdate();
        }
        if (LinkedSpecial?.StartsWith("steel", StringComparison.Ordinal) == true || LinkedSpecial == "mismatch-steel" && host || LinkedSpecial == "mismatch-normal" && !host)
            pd.SetInt("permadeathMode", 1);
        if (LinkedSpecial == "host-first") {
            pd.SetInt("maxHealthBase", host ? 7 : 5); pd.SetInt("maxHealth", host ? 7 : 5);
            pd.SetInt("health", host ? 4 : 1); pd.SetInt("heartPieces", host ? 3 : 0);
        }
        if (SharedHealthUiCase) {
            pd.SetInt("maxHealthBase", host ? 9 : 5); pd.SetInt("maxHealth", host ? 9 : 5);
            pd.SetInt("health", host ? 9 : 5); pd.SetInt("healthBlue", 0);
        }
        if (ProgressAuditCase) PrepareProgressAudit();
        if (SimpleKeyCase) PrepareSimpleKeys();
        if (KeyEssenceCase) PrepareKeyEssence();
        if (BankerCase) {
            pd.SetInt("bankerTheft", 1); pd.SetBool("bankerSpaMet", false);
            pd.SetBool("metBanker", true); pd.SetBool("bankerAccountPurchased", true);
            pd.SetInt("bankerBalance", 4500);
        }
        if (GrimmFlameCase) {
            pd.SetInt("grimmChildLevel", 1); pd.SetInt("flamesCollected", 0);
            pd.SetBool("gotCharm_40", true); pd.SetBool("equippedCharm_40", true);
            pd.scenesFlameCollected.Clear();
            if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_GRIMM_FLAME") == "split-flow") {
                // First-round start after receiving Grimmchild; subsequent
                // collection, upgrade and reset must all run natively.
                pd.SetBool("metGrimm", true); pd.SetBool("killedGrimm", false);
                pd.SetInt("flamesRequired", 3);
            }
        }
        if (ColosseumProgressCase) {
            pd.SetBool("colosseumBronzeOpened", true); pd.SetBool("colosseumBronzeCompleted", true);
            pd.SetBool("colosseumSilverOpened", host); pd.SetBool("colosseumSilverCompleted", host);
            pd.SetBool("colosseumGoldOpened", false); pd.SetBool("colosseumGoldCompleted", false);
            pd.SetInt("charmSlots", 4); pd.SetInt("ore", 0);
            CoopMod.Statistics.Resources.TryClaim("notch:colosseumBronze", out _);
            if (host) CoopMod.Statistics.Resources.TryClaim("ore:colosseumSilver", out _);
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_CATCHUP") != null) PrepareDreamCatchup();
        if (host && MenuResourceCase) {
            PlayerData.instance.SetBool("bossRushMode", false);
            PlayerData.instance.SetFloat("completionPercentage", 112);
        }
        if (LocalWorldCase) PrepareLocalWorld();
        if (host) { engine.ServerManager.Start(29650, LocalWorldCase); Flag("server.started"); }
        else yield return Until(() => Has("server.started"), "server ready");
        if (host && LinkedSpecial == "host-first") { Flag("host.linked.join-ready"); yield return Until(() => Has("guest.special.connected"), "guest connects before host"); }
        if (LinkedHealthCase && !host) yield return Until(() => Has("host.linked.join-ready"), "late linked guest waits for live host damage");
        if (SimpleKeyMode == "flow" && !host) yield return Until(() => Has("host.keys.spent"), "late guest waits for native key acquisition and expenditure", 240);
        if (ProgressAuditCase && !host) yield return Until(() => Has("host.audit.first-ready"), "host audit complete before actual guest joins");
        if (host && Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_CATCHUP") != null)
            yield return Until(() => Has("guest.catchup.loaded"), "missing-record guest loads native entrance before source client joins");
        typeof(ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        if (LinkedSpecial?.StartsWith("mismatch-", StringComparison.Ordinal) == true && !host)
            engine.ClientManager.ConnectEvent += () => Flag("guest.special.connected");
        typeof(ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "progress" + role });
        if (LinkedSpecial?.StartsWith("mismatch-", StringComparison.Ordinal) == true && !host)
            yield return Until(() => Has("guest.special.connected"), "real mismatched transport connected before rule rejection");
        else yield return Until(() => runtime.Connected, "real UDP handshake");
        if (!host && LinkedSpecial == "host-first") Flag("guest.special.connected");
        }
        if (SkinCompatibilityCase) {
            var checks = new Stack<IEnumerator>(); checks.Push(SkinCompatibilityChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            if (Has(role + ".failed")) yield break;
            yield return ProbeShutdown.Run(engine, root, role);
            Mark("FINAL PASS skin compatibility " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_SKIN_CASE")); Flag(role + ".done"); yield break;
        }
        if (LinkedHealthCase) {
            var checks = new Stack<IEnumerator>(); checks.Push(LinkedHealthChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS linked health " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_LINKED_CASE")); Flag(role + ".done"); }
            yield break;
        }
        if (KeyEssenceCase) {
            var checks = new Stack<IEnumerator>(); checks.Push(KeyEssenceChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            if (Has(role + ".failed")) yield break;
            yield return ProbeShutdown.Run(engine, root, role);
            Mark("FINAL PASS keys and essence " + KeyEssenceMode); Flag(role + ".done"); yield break;
        }
        if (ColosseumProgressCase) {
            var checks = new Stack<IEnumerator>(); checks.Push(ColosseumProgressChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS colosseum completion registration"); Flag(role + ".done"); }
            yield break;
        }
        if (GrimmFlameCase) {
            var checks = new Stack<IEnumerator>(); checks.Push(GrimmFlameChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS Grimm flames"); Flag(role + ".done"); }
            yield break;
        }
        if (BankerCase) {
            var checks = new Stack<IEnumerator>(); checks.Push(BankerChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS banker dialogue " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_BANKER_CASE")); Flag(role + ".done"); }
            yield break;
        }
        if (SimpleKeyCase) {
            var checks = new Stack<IEnumerator>(); checks.Push(SimpleKeyChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS simple key sources and personal doors " + SimpleKeyMode); Flag(role + ".done"); }
            yield break;
        }
        if (RemoteFeedbackCase) {
            var checks = new Stack<IEnumerator>(); checks.Push(RemoteFeedbackChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            if (!Has(role + ".failed")) yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS remote feedback isolation"); Flag(role + ".done"); }
            yield break;
        }
        if (MapExitCase) {
            var checks = new Stack<IEnumerator>(); checks.Push(MapExitChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            if (!Has(role + ".failed")) { Mark("FINAL PASS shared map purchase and disconnect save ordering"); Flag(role + ".done"); }
            yield break;
        }
        if (StagNestCase) {
            var checks = new Stack<IEnumerator>(); checks.Push(StagNestChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            if (Has(role + ".failed")) yield break;
            yield return ProbeShutdown.Run(engine, root, role);
            Mark("FINAL PASS stag nest targeted checks"); Flag(role + ".done"); yield break;
        }
        if (LocalWorldCase) {
            var checks = new System.Collections.Generic.Stack<IEnumerator>(); checks.Push(LocalWorldChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS local world " + LocalWorldScenario); Flag(role + ".done"); }
            yield break;
        }
        if (ProgressAuditCase) {
            var checks = new System.Collections.Generic.Stack<IEnumerator>(); checks.Push(ProgressAuditChecks());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark(ProgressAuditInspect ? "FINAL PASS pickup scene inspection only" : "FINAL PASS join audit, union and local pickup continuation"); Flag(role + ".done"); }
            yield break;
        }
        if (EmergencyBenchCase) {
            var checks = new System.Collections.Generic.Stack<IEnumerator>(); checks.Push(EmergencyBench());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            if (!Has(role + ".failed")) yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark("FINAL PASS emergency bench recovery"); Flag(role + ".done"); }
            yield break;
        }
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
        if (ResourcesCase || DefenderCase) {
            var checks = new System.Collections.Generic.Stack<IEnumerator>(); checks.Push(DefenderCase ? Defender() : Resources());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark(DefenderCase ? (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEFENDER_BARRIER_CHECK")) ?
                "FINAL PASS defender barrier " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEFENDER_BARRIER_CHECK") :
                Environment.GetEnvironmentVariable("HOLLOWCONNECT_DEFENDER_ROUTE_INSPECT") == "1" ? "FINAL PASS native route inspection only" : "FINAL PASS defender regression") : "FINAL PASS native resources"); Flag(role + ".done"); }
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_WARRIORS") != null) {
            var checks = new Stack<IEnumerator>(); checks.Push(Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_CATCHUP") != null ? DreamCatchup() : DreamWarriors());
            while (checks.Count > 0) {
                object current = null; bool more;
                try { more = checks.Peek().MoveNext(); if (more) current = checks.Peek().Current; }
                catch (Exception e) { Mark("FAIL " + e); Flag(role + ".failed"); yield break; }
                if (!more) { checks.Pop(); continue; }
                if (current is IEnumerator nested) checks.Push(nested); else yield return current;
            }
            if (Has(role + ".failed")) yield break;
            yield return ProbeShutdown.Run(engine, root, role);
            if (!Has(role + ".failed")) { Mark(Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_CATCHUP") != null
                ? "FINAL PASS shared dream victory catchup and personal essence " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_CATCHUP")
                : Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SETTLEMENT") != null
                ? "FINAL PASS dream settlement " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_SETTLEMENT") + " " + Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_VARIANT_SCENE")
                : Environment.GetEnvironmentVariable("HOLLOWCONNECT_LOST_KIN_LIFECYCLE") != null
                ? "FINAL PASS Lost Kin native death return and personal essence"
                : Environment.GetEnvironmentVariable("HOLLOWCONNECT_CHAMPION_LIFECYCLE") == "1"
                ? "FINAL PASS champion native phases personal essence and dream return"
                : Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_VARIANT_DAMAGE") == "1"
                ? "FINAL PASS dream variant registration and non-lethal HP convergence only"
                : Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_VARIANT_SCHEMA") == "1"
                ? "FINAL PASS dream variant schema observation only" : "FINAL PASS native dream warriors"); Flag(role + ".done"); }
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
