using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp;
using HollowConnect.Core;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityScene = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    // Separate short fixture. It models native control relinquishment, a
    // malformed reliable grant, a lying cinematic presence over actual UDP,
    // interrupted wait cleanup, and successful F8 in both directions. It does
    // not claim to run the complete native stag-ride cinematic.
    private IEnumerator WarpTravelAssertions(int round) {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const string cinematic = "Cinematic_Stag_travel";
        var hero = HeroController.instance;
        var other = host ? "guest" : "host";
        var hooks = new List<IDisposable>();
        var originalKeys = runtime.KeyPressed;
        var canWarp = typeof(CoopRuntime).GetProperty("CanStartWarp", flags);
        var sendPresence = typeof(CoopRuntime).GetMethod("SendPresence", flags);
        var receive = typeof(CoopRuntime).GetMethod("Receive", flags);
        var warpTo = typeof(CoopRuntime).GetMethod("WarpTo", flags);
        Message lastPresence = null;
        var outgoingRequests = 0;
        var serverResponses = 0;
        var blockedTransitions = 0;
        var poisonPresence = false;
        var suppressTransition = false;
        var stage = "";
        bool CanWarp() => (bool)canWarp.GetValue(runtime, null);
        void Assert(bool condition, string label) {
            if (!condition) throw new Exception("Warp travel " + label + " scene=" + UnityScene.GetActiveScene().name +
                " changing=" + Field<bool>(runtime, "changingScene") + " input=" + hero.acceptingInput +
                " relinquished=" + hero.controlReqlinquished);
            Mark("PASS warp travel " + label);
        }
        IEnumerator Wait(Func<bool> ready, string label, float seconds = 12) {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert(ready(), label);
        }
        IEnumerator Together(string label) {
            var suffix = ".warp-travel." + round + "." + label;
            Flag(role + suffix);
            yield return Wait(() => Has(other + suffix), "both " + label);
        }
        string ServerFlag(string label) => "server.warp-travel." + round + "." + label;
        void Presence() => sendPresence.Invoke(runtime, null);

        hooks.Add(new Hook(typeof(CoopRuntime).GetMethod("Send", flags),
            (Action<Action<CoopRuntime, Message>, CoopRuntime, Message>)((orig, self, m) => {
                if (ReferenceEquals(self, runtime)) {
                    if (m.Kind == Kind.Presence) {
                        if (poisonPresence) { m.Scene = cinematic; m.IsReady = true; m.HasMap = false; }
                        lastPresence = m;
                    }
                    if (m.Kind == Kind.WarpRequest) {
                        outgoingRequests++;
                        Mark("WARP TRAVEL UDP REQUEST target=" + m.Target + " serial=" + m.Serial);
                    }
                }
                orig(self, m);
            })));
        hooks.Add(new Hook(typeof(CoopServerAddon).GetMethod("Send", flags),
            (Action<Action<CoopServerAddon, ushort, Message>, CoopServerAddon, ushort, Message>)((orig, self, id, m) => {
                if (m.Kind == Kind.Warp) { serverResponses++; Mark("WARP TRAVEL UDP GRANT id=" + id + " serial=" + m.Serial); }
                orig(self, id, m);
            })));
        hooks.Add(new Hook(typeof(CoopServerAddon).GetMethod("Receive", flags),
            (Action<Action<CoopServerAddon, ushort, Message>, CoopServerAddon, ushort, Message>)((orig, self, id, m) => {
                var before = serverResponses;
                var party = Field<Party>(self, "party");
                var destinations = Field<Dictionary<ushort, Message>>(self, "destinations");
                var eligible = false;
                if (m.Kind == Kind.WarpRequest && stage.StartsWith("poison-")) {
                    var now = CoopRuntime.Now;
                    var last = Field<Dictionary<ushort, double>>(self, "lastWarp");
                    eligible = Field<bool>(self, "warp") && m.Session == party.Session && m.Round == party.Round &&
                        !party.Resetting && party.Members.TryGetValue(id, out var member) && member.Life == Life.Alive &&
                        party.Members.TryGetValue(m.Target, out var target) && target.Life == Life.Alive &&
                        now - target.Seen <= 1.5 && target.Scene == cinematic &&
                        destinations.TryGetValue(m.Target, out var destination) && destination.IsReady &&
                        (!last.TryGetValue(id, out var cooldown) || now - cooldown >= 3);
                }
                orig(self, id, m);
                if (m.Kind == Kind.Presence && m.Scene == cinematic && m.IsReady &&
                    destinations.TryGetValue(id, out var accepted) && accepted.Serial == m.Serial)
                    Flag(ServerFlag(stage + "-stored"));
                if (m.Kind == Kind.WarpRequest && stage.StartsWith("poison-")) {
                    Assert(eligible, "server rejection fixture reaches cinematic guard with other preconditions satisfied");
                    Assert(serverResponses == before, "server rejects cinematic destination despite IsReady=true");
                    Flag(ServerFlag(stage + "-rejected"));
                }
            })));
        On.GameManager.hook_BeginSceneTransition transition = (orig, self, info) => {
            if (suppressTransition || info.SceneName == cinematic) {
                blockedTransitions++;
                Mark("WARP TRAVEL FIXTURE intercepted transition " + info.SceneName);
                return;
            }
            orig(self, info);
        };
        On.GameManager.BeginSceneTransition += transition;

        try {
            runtime.KeyPressed = _ => false;
            yield return Wait(() => runtime.Connected && runtime.WarpEnabled && runtime.Peers.Count == 1 &&
                engine.ClientManager.SceneSnapshotReady && CanWarp(), "native entry and control ready");
            hero.AffectedByGravity(true);
            yield return Wait(() => hero.cState.onGround, "native target lands before travel checks");
            Freeze();
            yield return Together("ready");

            // Native method, not field injection: this is the important part
            // shared by stag/dialogue/cutscene control ownership.
            var requestsBefore = outgoingRequests;
            var serialBefore = Field<uint>(runtime, "warpSerial");
            hero.RelinquishControl();
            Presence();
            Assert(lastPresence != null && !lastPresence.IsReady, "relinquished hero advertises IsReady=false");
            var pressed = false;
            runtime.KeyPressed = key => key == CoopMod.Settings.TeleportKey && !pressed && (pressed = true);
            yield return Wait(() => pressed, "F8 evaluated during native relinquishment");
            runtime.KeyPressed = _ => false;
            Assert(outgoingRequests == requestsBefore && Field<uint>(runtime, "warpSerial") == serialBefore,
                "relinquished local F8 does not send or consume request serial");
            hero.RegainControl(); Presence();
            yield return Wait(CanWarp, "native regain restores local eligibility");
            yield return Together("regained");

            // Fail-safe transition observer would prevent even a regressed
            // client from loading a cinematic during this negative fixture.
            var handled = Field<uint>(runtime, "handledWarp");
            var blockedBefore = blockedTransitions;
            receive.Invoke(runtime, new object[] { new Message {
                Kind = Kind.Warp, Session = runtime.Session, Round = CoopContext.Epoch,
                Serial = handled + 1, Target = runtime.Peers.Keys.Single(), Scene = cinematic, X = 20, Y = 10
            } });
            Assert(Field<uint>(runtime, "handledWarp") == handled && !Field<bool>(runtime, "changingScene") &&
                blockedTransitions == blockedBefore, "reliable cinematic grant rejected before serial or transition mutation");
            yield return Together("bad-grant");

            // Lie only in coop presence; the real Unity clients remain safely
            // in their gameplay scene. The negative request crosses real UDP.
            foreach (var destinationRole in new[] { "host", "guest" }) {
                stage = "poison-" + destinationRole;
                poisonPresence = role == destinationRole;
                Presence();
                yield return Together(stage + "-start");
                yield return Wait(() => Has(ServerFlag(stage + "-stored")), "server received cinematic ready presence " + destinationRole);
                if (role != destinationRole) {
                    runtime.Selected = runtime.Peers.Keys.Single();
                    handled = Field<uint>(runtime, "handledWarp");
                    requestsBefore = outgoingRequests;
                    pressed = false;
                    runtime.KeyPressed = key => key == CoopMod.Settings.TeleportKey && !pressed && (pressed = true);
                    yield return Wait(() => pressed && outgoingRequests > requestsBefore, "negative F8 request sent over UDP " + role);
                    runtime.KeyPressed = _ => false;
                    yield return Wait(() => Has(ServerFlag(stage + "-rejected")), "server observed and rejected negative request " + role);
                    yield return new WaitForSecondsRealtime(.3f);
                    Assert(Field<uint>(runtime, "handledWarp") == handled && !Field<bool>(runtime, "changingScene"),
                        "negative UDP request leaves requester usable " + role);
                }
                yield return Together(stage + "-checked");
                poisonPresence = false; Presence();
                yield return Together(stage + "-restored");
                yield return new WaitForSecondsRealtime(.25f);
            }
            stage = "cleanup";

            // Hold one native load call in this explicitly synthetic fixture,
            // manually advance the production IEnumerator and expire its real
            // lifted deadline. No game scene, control flag or clock is forged.
            foreach (var disposal in new[] { false, true }) {
                yield return Wait(CanWarp, "eligible before interrupted-wait fixture");
                var scene = UnityScene.GetActiveScene().name == "Town" ? "Crossroads_01" : "Town";
                var routine = (IEnumerator)warpTo.Invoke(runtime, new object[] { new Message {
                    Kind = Kind.Warp, Session = runtime.Session, Round = CoopContext.Epoch,
                    Scene = scene, Target = runtime.Peers.Keys.Single(), X = 20, Y = 10
                } });
                suppressTransition = true;
                try {
                    blockedBefore = blockedTransitions;
                    Assert(routine.MoveNext() && Field<bool>(runtime, "changingScene") && blockedTransitions == blockedBefore + 1,
                        "production warp enters interrupted cross-room wait");
                    if (!disposal) {
                        var deadline = routine.GetType().GetFields(flags | BindingFlags.Public)
                            .Single(f => f.FieldType == typeof(float) && f.Name.Contains("deadline"));
                        deadline.SetValue(routine, Time.realtimeSinceStartup - 1);
                        Assert(!routine.MoveNext(), "expired production deadline exits instead of waiting forever");
                    }
                } finally {
                    (routine as IDisposable)?.Dispose();
                    suppressTransition = false;
                }
                Assert(!Field<bool>(runtime, "changingScene") && Field<Coroutine>(runtime, "teammateWarp") == null &&
                    Field<Vector2?>(runtime, "warpEntry") == null && Field<string>(runtime, "warpScene") == null,
                    disposal ? "iterator disposal executes finally cleanup" : "timeout executes finally cleanup");
            }
            yield return Together("cleanup");

            // Positive path: every request and grant below uses the production
            // runtime, server addon and actual UDP transport; no Receive call.
            foreach (var actor in new[] { "host", "guest" }) {
                if (role != actor) {
                    hero.AffectedByGravity(true);
                    yield return Wait(() => hero.cState.onGround, "destination naturally grounded " + role);
                    Freeze(); Presence();
                }
                yield return Together("udp-" + actor + "-ready");
                yield return new WaitForSecondsRealtime(.3f);
                if (role == actor) {
                    runtime.Selected = runtime.Peers.Keys.Single();
                    handled = Field<uint>(runtime, "handledWarp");
                    hero.transform.position += new Vector3(0, 6, 0);
                    Physics2D.SyncTransforms();
                    var start = hero.transform.position;
                    pressed = false;
                    runtime.KeyPressed = key => key == CoopMod.Settings.TeleportKey && !pressed && (pressed = true);
                    yield return Wait(() => pressed && Field<uint>(runtime, "handledWarp") > handled, "regained F8 accepted over real UDP " + actor);
                    runtime.KeyPressed = _ => false;
                    yield return Wait(() => !Field<bool>(runtime, "changingScene"), "regained F8 completes " + actor);
                    Assert(Vector2.Distance(start, hero.transform.position) > 2 && CanWarp(), "regained F8 relocates and leaves control eligible " + actor);
                }
                yield return Together("udp-" + actor + "-complete");
            }
            Mark("PASS warp travel all short assertions; full native stag cinematic not exercised");
        } finally {
            poisonPresence = false; suppressTransition = false;
            runtime.KeyPressed = originalKeys;
            hero.RegainControl();
            On.GameManager.BeginSceneTransition -= transition;
            foreach (var hook in hooks) hook.Dispose();
            Freeze();
        }
    }
}
