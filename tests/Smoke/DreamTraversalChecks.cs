using System;
using System.Collections;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class CoverageProbe {
    private string DreamTraversalScene => Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_TRAVERSAL_SCENE");
    private bool DreamTraversalCase => !string.IsNullOrEmpty(DreamTraversalScene);

    private IEnumerator DreamTraversalChecks() {
        var scene = DreamTraversalScene;
        var pd = PlayerData.instance;
        pd.SetBool("hasDreamNail", true); pd.SetBool("hasDash", true); pd.SetBool("canDash", true);
        pd.SetBool("hasWalljump", true); pd.SetBool("canWallJump", true); pd.SetBool("hasDoubleJump", true);
        pd.SetBool("visitedDirtmouth", true); pd.SetString("dreamReturnScene", "Town");
        if (!host) yield return Until(() => Has("host.traversal-entered"), "first player entered native dream scene");
        Enter(scene, scene == "Dream_04_White_Defender" ? "dreamGate" : "door1");
        yield return Until(() => UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene).isLoaded &&
            Hkmp.CoopContext.LocalScene == scene && engine.ClientManager.SceneSnapshotReady, "dream traversal asset and snapshot " + scene);
        Flag(role + ".traversal-entered");
        yield return Until(() => Has((host ? "guest" : "host") + ".traversal-entered"), "both entered dream traversal scene");
        yield return new WaitForSecondsRealtime(2);
        var platforms = Object.FindObjectsOfType<DreamPlatform>(true).Where(p => p.gameObject.scene.name == scene).ToArray();
        foreach (var p in platforms) Mark("TRAVERSAL PLATFORM " + EntityIdentity.Path(p.transform) + " showOnEnable=" + p.showOnEnable +
            " active=" + p.gameObject.activeInHierarchy + " pos=" + p.transform.position + " owner=" + EntityReferences.Owner(p.gameObject)?.Type +
            " parents=" + string.Join("/", p.GetComponentsInParent<Transform>(true).Select(t => t.name)));
        foreach (var e in Entities.Values.Where(e => BossDefeatSignals.IsBoss(e.Type)))
            Mark("TRAVERSAL BOSS " + e.Type + " host=" + e.Object.Host?.name + " client=" + e.Object.Client?.name);
        Mark("TRAVERSAL TOTAL native-components=" + platforms.Length + " entities=" + Entities.Count);
        Flag(role + ".traversal-observed");
        yield return Until(() => Has((host ? "guest" : "host") + ".traversal-observed"), "both recorded native traversal structure");
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_TRAVERSAL_STAGE") == "local")
            yield return DreamTraversalLocalChecks(scene, platforms);
    }

    private IEnumerator DreamTraversalLocalChecks(string scene, DreamPlatform[] platforms) {
        var expected = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(
            Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_TRAVERSAL_BASELINE")))[scene];
        var actualBosses = Entities.Values.Where(e => BossDefeatSignals.IsBoss(e.Type)).Select(e => e.Type.ToString()).OrderBy(s => s).ToArray();
        var expectedBosses = expected["bosses"].Values<string>().OrderBy(s => s).ToArray();
        if (!actualBosses.SequenceEqual(expectedBosses)) throw new Exception("Boss entity registration changed: " + string.Join(",", actualBosses));
        if (platforms.Length != (int)expected["platforms"]) throw new Exception("Native platform count changed: " + platforms.Length);
        if (platforms.Any(p => p.name.Contains("(Clone)") || EntityReferences.Owner(p.gameObject) != null))
            throw new Exception("Traversal platform still cloned or network-owned");
        foreach (var p in platforms) {
            if (new EntityProcessor { GameObject = p.gameObject, LateLoad = true, IsSceneHostDetermined = true,
                IsSceneHost = manager.IsSceneHost }.Process().Success) throw new Exception("Late load took over traversal platform " + p.name);
        }
        Mark("PASS all native traversal platforms remain unowned/uncloned; boss entity registration matches baseline");
        var hero = HeroController.instance;
        yield return Until(() => hero.acceptingInput && !hero.cState.transitioning && hero.isHeroInPosition &&
            !hero.GetComponent<Rigidbody2D>().isKinematic, "native traversal input and physics ready");
        var body = hero.GetComponent<Rigidbody2D>();
        var originalConstraints = body.constraints;
        // One actual small/medium/large representative of each size present;
        // every platform above still receives structural/late-load assertions.
        // Both players must use the same order regardless of entry timing/position.
        var samples = platforms.OrderBy(p => EntityIdentity.Path(p.transform))
            .GroupBy(p => p.name.Split(' ')[0]).Select(g => g.First()).ToArray();
        var index = 0;
        foreach (var platform in samples) {
            bool Visible() => Field<bool>(platform, "visible");
            IEnumerator Check(Func<bool> ready, string label) {
                var end = Time.realtimeSinceStartup + 12;
                while (!ready() && Time.realtimeSinceStartup < end) yield return null;
                if (!ready()) throw new Exception(label + " platform=" + platform.name + " hero=" + hero.transform.position + " visible=" + Visible());
                Mark("PASS " + label);
            }
            void Move(Vector3 position) {
                Freeze(); body.constraints = RigidbodyConstraints2D.FreezeAll;
                hero.transform.position = position; Physics2D.SyncTransforms();
            }
            IEnumerator Together(string label) {
                var key = "traversal." + index + "." + label;
                Flag(role + "." + key);
                yield return Check(() => Has((host ? "guest" : "host") + "." + key), "both " + key);
            }
            Mark("TRAVERSAL SAMPLE " + index + " " + platform.name + " inner=" + EntityIdentity.Path(platform.innerCollider.transform) +
                " outer=" + EntityIdentity.Path(platform.outerCollider.transform));
            var far = platform.transform.position + Vector3.up * 35;
            var near = platform.innerCollider.GetComponent<Collider2D>().bounds.center + Vector3.up * 4;
            Move(far); yield return Together("far"); yield return new WaitForSecondsRealtime(1);
            if (Visible()) throw new Exception("Native traversal platform did not hide " + platform.name);
            foreach (var approaching in new[] { "host", "guest" }) {
                if (role == approaching) Move(near);
                yield return Together("approach-" + approaching);
                yield return new WaitForSecondsRealtime(1);
                if (Visible() != (role == approaching)) throw new Exception("Peer changed local traversal visibility " + platform.name);
                if (role == approaching) {
                    var floor = platform.GetComponentsInChildren<Collider2D>().First(c => !c.isTrigger && c.enabled);
                    body.constraints = originalConstraints;
                    hero.transform.position = new Vector3(floor.bounds.center.x, floor.bounds.max.y + 3, hero.transform.position.z);
                    hero.cState.onGround = false; hero.AffectedByGravity(true);
                    hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero; Physics2D.SyncTransforms();
                    yield return Check(() => hero.cState.onGround && Math.Abs(hero.GetComponent<Collider2D>().bounds.min.y - floor.bounds.max.y) < .4f,
                        "real landing on native traversal platform " + platform.name);
                    Freeze(); body.constraints = RigidbodyConstraints2D.FreezeAll;
                }
                yield return Together("stood-" + approaching);
                if (role == approaching) Move(far);
                yield return Together("leave-" + approaching);
                yield return Check(() => !Visible(), "native leave hides traversal platform " + platform.name);
            }
            Mark("PASS independent native traversal visibility and real landing " + platform.name);
            index++;
        }
        body.constraints = originalConstraints;
    }
}
