using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowConnect.Core;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityScene = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private IEnumerator WarpReturnRepro() {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const string Arena = "Fungus1_02";
        var hero = HeroController.instance;
        var initialKeys = runtime.KeyPressed;
        var canWarp = typeof(CoopRuntime).GetProperty("CanStartWarp", Flags);
        var other = host ? "guest" : "host";
        var cycle = -1;

        bool Ready() => (bool)canWarp.GetValue(runtime, null) && engine.ClientManager.SceneSnapshotReady;
        string Scene() => UnityScene.GetActiveScene().name;
        uint Serial(string field) => Field<uint>(runtime, field);
        string State() => "cycle=" + cycle + " scene=" + Scene() + " xy=" + hero.transform.position +
            " ready=" + Ready() + " change=" + Field<bool>(runtime, "changingScene") +
            " input=" + hero.acceptingInput + " locked=" + hero.controlReqlinquished +
            " request=" + Serial("warpSerial") + " handled=" + Serial("handledWarp") +
            " notice=" + Field<string>(runtime, "status");

        IEnumerator Wait(Func<bool> condition, string label, float timeout = 14) {
            var deadline = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            if (!condition()) throw new Exception("F8 return " + label + " " + State());
            Mark("F8 RETURN " + label + " " + State());
        }
        string CycleKey(string label) => "f8-return-" + cycle + "-" + label;
        string Reply(ushort id, uint serial) => CycleKey("server-" + id + "-" + serial);
        string ReadReply(string key) => File.ReadAllText(Path.Combine(root, key));
        void WriteReply(string key, string report) {
            var path = Path.Combine(root, key);
            File.WriteAllText(path + ".partial", report);
            File.Move(path + ".partial", path);
        }
        IEnumerator Both(string label) {
            Flag(CycleKey(role + "-" + label));
            yield return Wait(() => Has(CycleKey(other + "-" + label)), label);
        }
        IEnumerator Press(KeyCode expected, string label) {
            var pressed = false;
            runtime.KeyPressed = key => key == expected && !pressed && (pressed = true);
            yield return Wait(() => pressed, label);
            runtime.KeyPressed = _ => false;
        }
        IEnumerator Select() {
            var target = runtime.Peers.Keys.Single();
            runtime.Selected = ushort.MaxValue;
            yield return Press(KeyCode.F7, "F7 read");
            yield return Wait(() => runtime.Selected == target, "F7 selected peer " + target);
        }
        IEnumerator GoodWarp(string label, bool crossRoom, Vector2 target) {
            var serial = Serial("warpSerial");
            var start = (Vector2)hero.transform.position;
            if (crossRoom ? Scene() == Arena : Vector2.Distance(start, target) < 4)
                throw new Exception("F8 return source not separated " + label + " " + State());
            yield return Press(KeyCode.F8, label + " F8 read");
            yield return Wait(() => Serial("warpSerial") == serial + 1, label + " request sent");
            var reply = Reply(runtime.LocalId, serial + 1);
            yield return Wait(() => Has(reply), label + " server saw request");
            if (!ReadReply(reply).StartsWith("grant=True"))
                throw new Exception("F8 return " + label + " rejected " + ReadReply(reply) + " " + State());
            yield return Wait(() => Serial("handledWarp") == serial + 1, label + " client accepted grant");
            yield return Wait(() => Scene() == Arena && Ready(), label + " native scene and input recovered");
            hero.AffectedByGravity(true);
            yield return Wait(() => hero.cState.onGround && Ready() && hero.CanQuickMap(), label + " controllable after landing");
            if (Field<string>(runtime, "status") != "已到达队友身边" ||
                Vector2.Distance(hero.transform.position, target) > 2 ||
                (!crossRoom && Vector2.Distance(start, hero.transform.position) < 3))
                throw new Exception("F8 return " + label + " missed destination " + target + " " + State());
            Freeze();
        }
        Collider2D Floor() => UnityEngine.Object.FindObjectsOfType<Collider2D>().Single(c =>
            c.gameObject.scene.name == Arena && c.name == "fung_plat_float_12");
        IEnumerator LandTarget() {
            var bounds = Floor().bounds;
            hero.transform.position = new Vector3(bounds.center.x, bounds.max.y + 3, hero.transform.position.z);
            Physics2D.SyncTransforms();
            hero.cState.onGround = false;
            hero.AffectedByGravity(true);
            yield return Wait(() => hero.cState.onGround && Ready() &&
                Mathf.Abs(hero.GetComponent<Collider2D>().bounds.min.y - Floor().bounds.max.y) < .4f, "target on native platform");
            Freeze();
        }
        Vector2 PlatformCenter() => (Vector2)Floor().bounds.center;
        Vector2 ReadTarget() {
            var parts = ReadReply(CycleKey("landed-target")).Split(',');
            return new Vector2(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        var grantCount = 0;
        using var serverSend = new Hook(typeof(CoopServerAddon).GetMethod("Send", Flags),
            (Func<Func<CoopServerAddon, ushort, Message, bool>, CoopServerAddon, ushort, Message, bool>)((orig, self, id, message) => {
                var sent = orig(self, id, message);
                if (message.Kind == Kind.Warp && sent && message.Value == 0) {
                    grantCount++;
                    Mark("F8 SERVER grant id=" + id + " serial=" + message.Serial + " destination=" + message.Scene);
                }
                return sent;
            }));
        using var serverReceive = new Hook(typeof(CoopServerAddon).GetMethod("Receive", Flags),
            (Action<Action<CoopServerAddon, ushort, Message>, CoopServerAddon, ushort, Message>)((orig, self, id, message) => {
                if (!host || cycle < 0) { orig(self, id, message); return; }
                var party = Field<Party>(self, "party");
                var destinations = Field<Dictionary<ushort, Message>>(self, "destinations");
                var grantBefore = grantCount;
                var trace = "";
                if (message.Kind == Kind.WarpRequest) {
                    party.Members.TryGetValue(id, out var requester);
                    party.Members.TryGetValue(message.Target, out var target);
                    destinations.TryGetValue(message.Target, out var destination);
                    var cooldown = Field<Dictionary<ushort, double>>(self, "lastWarp");
                    var now = CoopRuntime.Now;
                    trace = " requester=" + requester?.Life + " target=" + target?.Life +
                        " targetReady=" + destination?.IsReady + " targetScene=" + target?.Scene +
                        " targetAge=" + (target == null ? -1 : now - target.Seen).ToString("F3", CultureInfo.InvariantCulture) +
                        " cooldown=" + (cooldown.TryGetValue(id, out var last) && now - last < 3) +
                        " hostChanging=" + Field<bool>(runtime, "changingScene");
                }
                orig(self, id, message);
                if (message.Kind == Kind.Presence && id == runtime.LocalId &&
                    !message.IsReady && Field<bool>(runtime, "changingScene") && Field<string>(runtime, "warpScene") == Arena &&
                    destinations.TryGetValue(id, out var stored) && stored.Serial == message.Serial) {
                    var flag = CycleKey("server-host-in-flight");
                    if (!Has(flag)) {
                        Flag(flag);
                        Mark("F8 SERVER host destination unavailable during real cross-room warp");
                    }
                }
                if (message.Kind == Kind.WarpRequest) {
                    var result = "grant=" + (grantCount > grantBefore) + trace;
                    WriteReply(Reply(id, message.Serial), result);
                    Mark("F8 SERVER request id=" + id + " serial=" + message.Serial + " " + result);
                }
            }));

        try {
            runtime.KeyPressed = _ => false;
            yield return Wait(() => runtime.Connected && runtime.WarpEnabled &&
                runtime.Peers.Count == 1 && runtime.Peers.Values.Single().State != null && Ready(), "initialized");
            for (cycle = 0; cycle < 2; cycle++) {
                if (!host) yield return LandTarget();
                yield return Both("initial");
                var guestTarget = PlatformCenter() + Vector2.up * 1.75f;
                if (host) {
                    Enter("Town");
                    yield return Wait(() => In("Town") && Ready(), "host at Town");
                }
                yield return Both("split");
                yield return Select();
                yield return Both("selected");
                yield return new WaitForSecondsRealtime(.5f);
                if (host) {
                    yield return GoodWarp("host across", true, guestTarget);
                    var pos = hero.transform.position;
                    WriteReply(CycleKey("landed-target"), pos.x.ToString("R", CultureInfo.InvariantCulture) +
                        "," + pos.y.ToString("R", CultureInfo.InvariantCulture));
                } else {
                    yield return Wait(() => Has(CycleKey("server-host-in-flight")), "host loading observed by server");
                    var before = Serial("warpSerial");
                    var handled = Serial("handledWarp");
                    var start = hero.transform.position;
                    yield return Press(KeyCode.F8, "guest immediate F8 read");
                    yield return Wait(() => Serial("warpSerial") == before + 1, "guest immediate request sent");
                    var reply = Reply(runtime.LocalId, before + 1);
                    yield return Wait(() => Has(reply), "guest immediate request reached server");
                    var result = ReadReply(reply);
                    if (!result.StartsWith("grant=False requester=Alive target=Loading targetReady=False") || !result.Contains("cooldown=False"))
                        throw new Exception("F8 return immediate guest did not hit the expected in-flight denial " + result + " " + State());
                    yield return Wait(() => Field<string>(runtime, "warpNotice") == "正在等待传送条件恢复…",
                        "guest sees waiting feedback");
                    if (Serial("handledWarp") != handled || Vector2.Distance(start, hero.transform.position) > .25f)
                        throw new Exception("F8 return moved before host was ready " + State());
                    yield return Wait(() => Serial("handledWarp") > before + 1, "single guest press retries and receives grant");
                    yield return Wait(() => In(Arena) && Ready(), "automatic return completed");
                    hero.AffectedByGravity(true);
                    yield return Wait(() => hero.cState.onGround && Ready() && hero.CanQuickMap(), "automatic return grounded");
                    yield return Wait(() => Has(CycleKey("landed-target")), "host landing recorded");
                    if (Field<string>(runtime, "status") != "已到达队友身边" ||
                        Vector2.Distance(hero.transform.position, ReadTarget()) > 2)
                        throw new Exception("F8 return did not land safely beside the host " + State());
                    Freeze();
                }
                yield return Both("post-host-arrival");
                if (!host) {
                    // A second manual press after an accepted warp should get
                    // cooldown feedback and leave the player where they are.
                    var before = Serial("handledWarp");
                    yield return Press(KeyCode.F8, "guest immediate second F8 read");
                    yield return Wait(() => Field<string>(runtime, "warpNotice") == "传送冷却中，请稍后重试", "guest sees cooldown");
                    if (Serial("handledWarp") != before) throw new Exception("F8 cooldown consumed a grant");
                }
                yield return Both("cooldown feedback complete");
                yield return new WaitForSecondsRealtime(3.25f);
                if (!host && cycle == 1) {
                    Enter("Town");
                    yield return Wait(() => In("Town") && Ready(), "guest at Town");
                    yield return new WaitForSecondsRealtime(.5f);
                    yield return GoodWarp("guest settled cross-room", true, ReadTarget());
                }
                yield return Both("post-guest-arrival");
                if (cycle == 0) yield return new WaitForSecondsRealtime(3.25f);
            }
        } finally {
            runtime.KeyPressed = initialKeys;
        }
    }
}
