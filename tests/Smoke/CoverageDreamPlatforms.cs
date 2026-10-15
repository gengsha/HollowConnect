using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private IEnumerator DreamPlatformAssertions(int round) {
        var e = Entities.Values.First(v => v.Type == EntityType.DreamPlatform && v.Object.Host.name == "dream_plat_small");
        var platform = (manager.IsSceneHost ? e.Object.Host : e.Object.Client).GetComponent<DreamPlatform>();
        void Refresh() {
            e = Entities.Values.First(v => v.Type == EntityType.DreamPlatform && v.Object.Host.name == "dream_plat_small");
            platform = (manager.IsSceneHost ? e.Object.Host : e.Object.Client).GetComponent<DreamPlatform>();
        }
        int Count() => Field<ushort>(Field<Dictionary<EntityComponentType, EntityComponent>>(e, "_components")[EntityComponentType.DreamPlatform], "_numInRange");
        void Request(byte action) {
            var data = new EntityNetworkData { Type = EntityComponentType.DreamPlatform };
            data.Packet.Write(ushort.MaxValue); // The server must ignore a claimed total.
            data.Packet.Write(action);
            Field<NetClient>(engine.ClientManager, "_netClient").UpdateManager.AddEntityData(e.Id, data);
        }
        var first = manager.IsSceneHost ? (host ? "guest" : "host") : role;
        var second = first == "host" ? "guest" : "host";
        var hero = HeroController.instance;
        bool Ready(string scene) => In(scene) && manager.IsSceneHostDetermined && engine.ClientManager.SceneSnapshotReady &&
            hero.acceptingInput && !hero.cState.transitioning && hero.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION;
        var far = platform.transform.position + new Vector3(0, 35, 0);
        var near = platform.innerCollider.GetComponent<Collider2D>().bounds.center + Vector3.up * 4;
        var triggered = 0;
        var leaving = false;
        On.TriggerEnterEvent.hook_OnTriggerEnter2D observer = (orig, self, collider) => {
            if (self == platform.innerCollider && collider.transform.IsChildOf(hero.transform)) triggered++;
            orig(self, collider);
        };
        On.TriggerEnterEvent.OnTriggerEnter2D += observer;
        On.TriggerEnterEvent.hook_OnTriggerExit2D exit = (orig, self, collider) => {
            // An unload must remove membership even without a collider exit.
            if (leaving && platform != null && self == platform.outerCollider) return;
            orig(self, collider);
        };
        On.TriggerEnterEvent.OnTriggerExit2D += exit;
        On.GameManager.hook_BeginSceneTransition transition = (orig, self, info) => {
            Mark("DREAM TRANSITION " + self.sceneName + " -> " + info.SceneName + " hero=" + hero.transform.position);
            orig(self, info);
        };
        On.GameManager.BeginSceneTransition += transition;
        bool Visible() => Field<bool>(platform, "visible");
        void Move(Vector3 pos) { Freeze(); hero.transform.position = pos; Physics2D.SyncTransforms(); }
        string Describe() => "scene=" + GameManager.instance.sceneName + " hero=" + hero.transform.position + " authority=" + manager.IsSceneHost +
            (platform == null ? " platform destroyed" : " visible=" + Visible() + " count=" + Count() + " triggered=" + triggered +
            " colliders=" + string.Join(";", platform.GetComponentsInChildren<Collider2D>(true).Select(c => c.name + ":" + c.enabled + "/" + c.isTrigger + "/" + c.bounds)));
        IEnumerator Check(Func<bool> ready, string label) {
            var end = Time.realtimeSinceStartup + 12;
            while (!ready() && Time.realtimeSinceStartup < end) yield return null;
            if (!ready()) throw new Exception("Dream platform " + label + " " + Describe());
            Mark("PASS dream platform " + label);
        }
        IEnumerator Together(string label) {
            Flag(role + ".dream." + round + "." + label);
            yield return Check(() => Has((host ? "guest" : "host") + ".dream." + round + "." + label), "both " + label);
        }
        IEnumerator Stand(string label) {
            var floor = platform.GetComponentsInChildren<Collider2D>().First(c => !c.isTrigger && c.enabled);
            hero.transform.position = new Vector3(floor.bounds.center.x, floor.bounds.max.y + 3, hero.transform.position.z);
            hero.cState.onGround = false; hero.AffectedByGravity(true); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            Physics2D.SyncTransforms();
            yield return Check(() => hero.cState.onGround && Math.Abs(hero.GetComponent<Collider2D>().bounds.min.y - floor.bounds.max.y) < .4f, label);
            Freeze();
        }
        try {
            yield return Check(() => Ready("Dream_Nailcollection"), "native entry and snapshot ready");
            Move(far);
            yield return Together("far");
            yield return new WaitForSecondsRealtime(2);
            Mark("DREAM INITIAL " + Describe());
            if (Visible() || Count() != 0) throw new Exception("Dream target did not start empty and hidden");
            if (role == first) Move(near);
            yield return Check(() => Visible() && Count() == 1, "non-authority approach shows on both peers");
            if (role == first && triggered == 0) throw new Exception("Dream platform was not entered by native collision");
            yield return Together("first-visible");
            yield return new WaitForSecondsRealtime(1);
            if (role == first) yield return Stand("non-authority stands on native platform collider");
            yield return Together("stood");
            if (role == first) { Request(DreamPlatformComponent.Entered); Request(DreamPlatformComponent.Entered); }
            yield return new WaitForSecondsRealtime(1);
            if (Count() != 1) throw new Exception("Duplicate enters changed membership " + Describe());
            Mark("PASS dream platform duplicate enters and claimed totals are ignored");
            if (role == second) Move(near);
            yield return Check(() => Count() == 2, "two distinct players counted");
            yield return Together("both-near");
            yield return new WaitForSecondsRealtime(1);
            if (role == first) Move(far);
            yield return Together("first-left");
            yield return new WaitForSecondsRealtime(1);
            if (!Visible()) throw new Exception("Dream platform hid under remaining player " + Describe());
            Mark("PASS dream platform stays shown for remaining player");
            if (role == first) {
                Request(DreamPlatformComponent.Exited); Request(DreamPlatformComponent.Exited);
                Request(DreamPlatformComponent.Register); Request(DreamPlatformComponent.State);
            }
            yield return new WaitForSecondsRealtime(1);
            if (Count() != 1) throw new Exception("Duplicate exits or peer state changed remaining membership " + Describe());
            Mark("PASS dream platform duplicate exits and unauthorized state are ignored");
            if (role == second) Move(far);
            yield return Check(() => !Visible(), "last player leaves hides both peers");
            yield return Together("hidden");
            if (role == second) Move(near);
            yield return Check(Visible, "authority approach shows both peers");
            yield return Together("second-visible");
            if (role == second) Move(far);
            yield return Check(() => !Visible() && Count() == 0, "authority leaves hides both peers");
            yield return Together("authority-left-range");

            // Keep one peer in range while the other unloads and rejoins.
            Move(near);
            yield return Check(() => Count() == 2 && Visible(), "both near before scene leave");
            yield return Together("before-rejoin");
            IEnumerator LeaveAndReturn(string label) {
                leaving = true;
                GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
                    SceneName = "Town", EntryGateName = "left1",
                    Visualization = GameManager.SceneLoadVisualizations.Default, WaitForSceneTransitionCameraFade = true
                });
                yield return Until(() => Ready("Town"), "dream leave to Town " + label);
                leaving = false;
                Freeze(); Flag(role + ".dream." + round + ".away-" + label);
                yield return Check(() => Has((host ? "guest" : "host") + ".dream." + round + ".retained-" + label), "other peer retained platform " + label);
                Enter("Dream_Nailcollection");
                yield return Until(() => Ready("Dream_Nailcollection"), "dream rejoin " + label);
                Freeze(); Refresh();
                yield return Check(() => Visible() && Count() == 1, "rejoin snapshot " + label);
            }
            IEnumerator Retain(string label, bool takeover) {
                yield return Check(() => Has((host ? "guest" : "host") + ".dream." + round + ".away-" + label), "other peer away " + label);
                if (takeover) {
                    yield return Check(() => manager.IsSceneHost, "scene authority transferred");
                    Refresh();
                    Mark("DREAM TAKEOVER " + Describe());
                }
                yield return Check(() => Visible() && Count() == 1, "remaining player retained " + label);
                Flag(role + ".dream." + round + ".retained-" + label);
            }
            if (role == first) yield return LeaveAndReturn("peer"); else yield return Retain("peer", false);
            yield return Together("peer-rejoined");
            if (role == first) Move(near);
            yield return Check(() => Count() == 2, "both near before authority leave");
            yield return Together("before-takeover");
            if (role == second) yield return LeaveAndReturn("authority"); else yield return Retain("authority", true);
            yield return Together("authority-rejoined");
            if (role == first) yield return Stand("new authority stands on native platform after takeover");
            yield return Together("takeover-stood");
            if (role == first) Move(far);
            yield return Check(() => !Visible() && Count() == 0, "last player leaves after authority transfer");
            yield return Together("finished");
        } finally {
            On.TriggerEnterEvent.OnTriggerEnter2D -= observer;
            On.TriggerEnterEvent.OnTriggerExit2D -= exit;
            On.GameManager.BeginSceneTransition -= transition;
        }
    }
}
