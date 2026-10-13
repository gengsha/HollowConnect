using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using Modding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

// Actual two-client nail collision, accepted rescuer identity, native visuals,
// capped soul, unreliable-roster ordering and replay protection.
public sealed class RescueProbe : MonoBehaviour {
    private string root, role;
    private bool host;
    private CoopRuntime runtime;
    private Hkmp.Game.GameManager engine;
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private void Log(string s) => File.AppendAllText(Path.Combine(root, role + ".log"), DateTime.UtcNow.ToString("O") + " " + s + "\n");
    private bool Has(string n) => File.Exists(Path.Combine(root, n));
    private void Flag(string n) => File.WriteAllText(Path.Combine(root, n), "ok");
    private void Check(bool b, string s) { if (!b) throw new Exception(s); Log("PASS " + s); }
    private IEnumerator Until(Func<bool> test, string s, float seconds = 45) {
        var end = Time.realtimeSinceStartup + seconds;
        while (!test() && Time.realtimeSinceStartup < end) yield return null;
        Check(test(), s);
    }
    private void Inject(Message m) {
        m.Session = runtime.Session; m.Round = Hkmp.CoopContext.Epoch;
        typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, new object[] { m });
    }
    private void Freeze() {
        var h = HeroController.instance; h.RegainControl(); h.StartAnimationControl(); h.takeNoDamage = true;
        h.cState.dead = h.cState.recoiling = h.cState.invulnerable = false; h.cState.recoilingLeft = h.cState.recoilingRight = false;
        HeroBox.inactive = false; h.GetComponent<Rigidbody2D>().gravityScale = 0; h.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        h.transform.position = new Vector3(host ? 20 : 22, 12, h.transform.position.z);
    }
    private void Die() {
        var h = HeroController.instance; PlayerData.instance.SetInt("health", 0);
        h.StartCoroutine((IEnumerator)typeof(HeroController).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(h, null));
    }
    private IEnumerator Start() {
        root = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROOT"); role = Environment.GetEnvironmentVariable("HOLLOWCONNECT_SMOKE_ROLE"); host = role == "host";
        var stack = new Stack<IEnumerator>(); stack.Push(Run());
        while (stack.Count > 0) {
            object current = null; bool more;
            try { more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current; }
            catch (Exception e) { Time.timeScale = 1; Log("FAIL " + e); File.WriteAllText(Path.Combine(root, role + ".failed"), e.ToString()); yield break; }
            if (!more) { stack.Pop(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
    }
    private IEnumerator Run() {
        Log("muted, fresh character, all save overloads intercepted");
        yield return Until(() => GameManager.instance != null && Object.FindObjectOfType<CoopRuntime>() != null &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu_Title", "menu ready");
        runtime = Object.FindObjectOfType<CoopRuntime>(); engine = Field<Hkmp.Game.GameManager>(ModHooks.GetMod("HKMP"), "_gameManager");
        Field<Hkmp.Game.Settings.ModSettings>(engine.ClientManager, "_modSettings").AuthKey = Hkmp.Util.AuthUtil.GenerateAuthKey();
        CoopMod.Settings.Rescue = true; CoopMod.Settings.ShareProgress = false; CoopMod.Settings.EntryProtection = false;
        var pd = PlayerData.instance; pd.SetString("respawnScene", "Town"); pd.SetString("respawnMarkerName", "RestBench"); pd.SetInt("respawnType", 1);
        GameManager.instance.profileID = 99; GameManager.instance.StartCoroutine(GameManager.instance.RunContinueGame());
        yield return Until(() => HeroController.instance != null && HeroController.instance.isHeroInPosition && !GameManager.instance.IsInSceneTransition, "fresh unsaved Town entered");
        if (host) { engine.ServerManager.Start(29650, false); Flag("server.started"); } else yield return Until(() => Has("server.started"), "server ready");
        yield return new WaitForSecondsRealtime(1);
        typeof(Hkmp.Game.Client.ClientManager).GetField("_autoConnect", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine.ClientManager, true);
        typeof(Hkmp.Game.Client.ClientManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine.ClientManager, new object[] { "127.0.0.1", 29650, "rescue" + role });
        yield return Until(() => runtime.Connected, "real UDP handshake"); Flag(role + ".connected");
        yield return Until(() => Has((host ? "guest" : "host") + ".connected"), "both connected");
        GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = "Crossroads_01", EntryGateName = "left1",
            Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true });
        yield return Until(() => runtime.Playable && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Crossroads_01" &&
            HeroController.instance.acceptingInput && !HeroController.instance.cState.transitioning &&
            HeroController.instance.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION && engine.ClientManager.SceneSnapshotReady, "native entry and snapshot ready");
        Freeze(); Flag(role + ".ready"); yield return Until(() => Has((host ? "guest" : "host") + ".ready") && runtime.Peers.Count == 1, "both ready");
        yield return Ordering();
        Flag(role + ".ordered"); yield return Until(() => Has((host ? "guest" : "host") + ".ordered"), "ordering probes complete");
        for (var i = 0; i < 4; i++) yield return Trial(i);
        yield return ProbeShutdown.Run(engine,root,role);
        Log("FINAL PASS rescue soul and native feedback"); Flag(role + ".done");
    }
    private IEnumerator Ordering() {
        var pd = PlayerData.instance; pd.SetInt("MPCharge", 0); pd.SetInt("MPReserve", 0); pd.SetBool("soulLimited", false);
        // Exercise the production handler after its shade view has already
        // been removed by an Alive roster, using a synthetic peer only.
        const ushort target = 65000;
        Inject(new Message { Kind = Kind.Roster, Id = target, Aux = 3, Serial = 10, Key = "1", Value = (int)Life.Down, Scene = "Crossroads_01", X = 20, Y = 12 });
        yield return null;
        Inject(new Message { Kind = Kind.Roster, Id = target, Aux = 4, Serial = 11, Key = "1", Value = (int)Life.Alive, Scene = "Crossroads_01", X = 20, Y = 12 });
        yield return null;
        var accepted = new Message { Kind = Kind.Revived, Id = target, Target = runtime.LocalId, Aux = 4, Serial = 1, Scene = "Crossroads_01", X = 20, Y = 12 };
        Inject(accepted); Inject(accepted);
        Check(pd.GetInt("MPCharge") == 33 && Object.FindObjectsOfType<RescueBreakEffect>().Length == 1, "Alive roster before reliable rescue still rewards and presents exactly once");
        Time.timeScale = 0; yield return new WaitForSecondsRealtime(.6f);
        var fx = Object.FindObjectOfType<RescueBreakEffect>(); Check(fx != null, "native effect survives hit-stop");
        Check(fx.GetComponent<tk2dSpriteAnimator>().CurrentClip.name == "Death" && fx.transform.Find("Death Glow").gameObject.activeSelf, "native Death and Death Glow advance in unscaled time");
        Check(fx.transform.Find("Particle Wave").GetComponent<ParticleSystem>().particleCount > 0, "native shade particle wave emitted");
        var audio = fx.GetComponents<AudioSource>();
        Check(audio.Length == 2 && audio.Any(a => a.clip.name == "boss_explode_clean") && audio.Any(a => a.clip.name == "hero_void_scream_spell"), "both original own-shade break audio assets attached");
        Check(audio.All(a => a.outputAudioMixerGroup != null), "break audio respects native mixer");
        Check(fx.GetComponentsInChildren<PlayMakerFSM>(true).Length == 0 && fx.GetComponentsInChildren<Collider2D>(true).Length == 0, "visual clone has no native recovery FSM or collision");
        Inject(new Message { Kind = Kind.Down, Id = target, Aux = 5, Serial = 2, Scene = "Crossroads_01", X = 20, Y = 12 }); Inject(accepted);
        Check(runtime.Peers[target].Life == Life.Down && pd.GetInt("MPCharge") == 33, "old rescue cannot reward or revive a later death");
        Inject(new Message { Kind = Kind.Revived, Id = target, Target = runtime.LocalId, Aux = 6, Serial = 2, Scene = "Crossroads_01", X = 20, Y = 12 });
        Check(pd.GetInt("MPCharge") == 66, "a new accepted death earns one new reward");
        // Verify the actual session gate separately, without Inject rewriting it.
        var wrong = new Message { Kind = Kind.Revived, Id = target, Target = runtime.LocalId, Aux = 8, Serial = 3, Scene = "Crossroads_01", Session = "wrong", Round = Hkmp.CoopContext.Epoch };
        typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, new object[] { wrong });
        Check(pd.GetInt("MPCharge") == 66, "wrong-session event cannot reward");
        wrong.Session = runtime.Session; wrong.Round++;
        typeof(CoopRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime, new object[] { wrong });
        Check(pd.GetInt("MPCharge") == 66, "wrong-round event cannot reward");
        runtime.Resetting = true;
        Inject(new Message { Kind = Kind.Revived, Id = target, Target = runtime.LocalId, Aux = 8, Serial = 3, Scene = "Crossroads_01" });
        runtime.Resetting = false;
        Check(pd.GetInt("MPCharge") == 66, "wipe in progress rejects rescue reward");
        Inject(new Message { Kind = Kind.Roster, Id = target, Aux = 7, Serial = 12, Key = "3", Value = (int)Life.Returning, Scene = "Crossroads_01" });
        Inject(accepted);
        Check(pd.GetInt("MPCharge") == 66 && runtime.Peers[target].Life == Life.Returning, "old rescue after voluntary death grants no soul");
        Inject(new Message { Kind = Kind.Left, Id = target });
        yield return new WaitForSecondsRealtime(2.7f); Time.timeScale = 1;
        Check(Object.FindObjectsOfType<RescueBreakEffect>().Length == 0, "all accepted effects clean up while paused");
    }
    private IEnumerator Trial(int i) {
        var prefix = "trial" + i; var rescuer = i % 2 == 0 ? host : !host; var pd = PlayerData.instance;
        Freeze(); pd.SetInt("health", 4); pd.SetInt("healthBlue", 0); pd.SetBool("soulLimited", i == 3);
        pd.SetInt("geo", host ? 123 : 456); pd.SetInt("geoPool", 78); pd.SetString("shadeScene", "Crossroads_10");
        pd.SetInt("MPReserveMax", 33); pd.SetInt("MPCharge", rescuer ? (i == 1 ? 90 : i == 2 ? 99 : 0) : 7); pd.SetInt("MPReserve", i == 2 && rescuer ? 33 : 0);
        Flag(prefix + "." + role + ".ready"); yield return Until(() => Has(prefix + "." + (host ? "guest" : "host") + ".ready"), prefix + " ready");
        uint serial; ushort target; uint version;
        if (!rescuer) {
            Die(); yield return Until(() => runtime.Down, prefix + " victim down");
            serial = runtime.DeathSerial; target = runtime.LocalId; Flag(prefix + ".down");
            yield return Until(() => !runtime.Down && pd.GetInt("health") == 1, prefix + " one-health revival");
            Check(pd.GetInt("MPCharge") == 7 && pd.GetInt("MPReserve") == 0, prefix + " victim receives no rescue soul");
            version = 0;
        } else {
            yield return Until(() => Has(prefix + ".down") && runtime.Peers.Values.Any(p => p.Life == Life.Down), prefix + " teammate down received");
            var peer = runtime.Peers.Single(); target = peer.Key; serial = peer.Value.DownSerial;
            var service = Field<ShadeService>(runtime, "shades"); yield return Until(() => service.Get(target) != null, prefix + " visible rescue shade");
            Check(!runtime.RequestShadeHit(target, serial + 1) && !runtime.RequestShadeHit(runtime.LocalId, serial), prefix + " wrong death and self rescue rejected");
            var before = pd.GetInt("MPCharge"); var beforeReserve = pd.GetInt("MPReserve");
            // Send a deliberately invalid request through the real server too.
            typeof(CoopRuntime).GetMethod("Send", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runtime,
                new object[] { new Message { Kind = Kind.ShadeHit, Target = target, Serial = serial + 1, Scene = "Crossroads_01" } });
            var pos = service.Get(target).transform.position; var h = HeroController.instance;
            h.transform.position = pos + new Vector3(-2, 0, h.transform.position.z); h.FaceLeft(); h.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(.75f);
            Check(runtime.Peers[target].Life == Life.Down && pd.GetInt("MPCharge") == before && pd.GetInt("MPReserve") == beforeReserve, prefix + " rejected request and missed swing grant no reward");
            h.FaceRight(); h.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
            yield return Until(() => runtime.Peers[target].Life == Life.Alive, prefix + " actual nail rescue accepted");
            Check(pd.GetInt("MPCharge") == (i == 1 || i == 2 ? 99 : 33) && pd.GetInt("MPReserve") == (i == 1 ? 24 : i == 2 ? 33 : 0), prefix + " rescuer soul and reserve match native cap");
            version = runtime.Peers[target].LifeVersion;
            var effects = Object.FindObjectsOfType<RescueBreakEffect>().Length;
            var replay = new Message { Kind = Kind.Revived, Id = target, Target = runtime.LocalId, Serial = serial, Aux = version, Scene = "Crossroads_01", X = pos.x, Y = pos.y };
            Inject(replay); Inject(replay);
            Check(Object.FindObjectsOfType<RescueBreakEffect>().Length == effects && pd.GetInt("MPCharge") == (i == 1 || i == 2 ? 99 : 33), prefix + " reliable replay grants no additional soul or effect");
        }
        Check(Object.FindObjectsOfType<RescueBreakEffect>().Length == 1, prefix + " both clients see one native rescue break");
        Check(pd.GetInt("geo") == (host ? 123 : 456) && pd.GetInt("geoPool") == 78 && pd.GetString("shadeScene") == "Crossroads_10" && pd.GetBool("soulLimited") == (i == 3), prefix + " personal geo shade and soul limitation remain intact");
        Flag(prefix + "." + role + ".checked"); yield return Until(() => Has(prefix + "." + (host ? "guest" : "host") + ".checked"), prefix + " both checked");
        yield return new WaitForSecondsRealtime(2.7f);
        Check(Object.FindObjectsOfType<RescueBreakEffect>().Length == 0, prefix + " feedback fully retired");
    }
}
