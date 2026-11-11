using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator MawlekResourceChecks() {
        const string scene = "Crossroads_09", id = "mask:Crossroads_09/Heart Piece";
        var collector = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESOURCE_COLLECTOR") ?? "host";
        var pd = PlayerData.instance;
        var initial = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_RESOURCE_INITIAL") == "3" ? 3 : 0;
        var expectedPieces = (initial + 1) % 4;
        var expectedHealth = initial == 3 ? 6 : 5;
        var linked = Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_RESOURCE_LINKED") == "1";
        pd.SetInt("heartPieces", initial);
        ResourceCheck(!LocalResourceClaims.Contains(id) && pd.GetInt("maxHealthBase") == 5,
            "Mawlek starts with an unclaimed source and five masks");
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm.GameObject != null && (fsm.GameObject.name.Contains("Heart") ||
                fsm.GameObject.name.Contains("Mawlek") || fsm.GameObject.name == "Battle Scene"))
                File.AppendAllText(Path.Combine(root, role + ".reward-trace.txt"), Time.frameCount + " " +
                    fsm.GameObject.name + "/" + fsm.Name + " -> " + state.Name + " pieces=" + pd.GetInt("heartPieces") +
                    " claimed=" + LocalResourceClaims.Contains(id) + "\n");
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        try {
            yield return Room(scene);
            if (linked) yield return Until(() => runtime.LinkedHealthRuleEnabled && Field<bool>(runtime, "linkedHealthStateReceived"),
                "Mawlek shared health pool is initialized before native combat", 10);
            var sharedMax = pd.GetInt("maxHealth");
            var sharedHealth = pd.GetInt("health");
            Mark("MAWLEK BALANCE initial=" + initial + " personal=" + pd.GetInt("maxHealthBase") +
                " sharedMax=" + sharedMax + " health=" + sharedHealth + " linked=" + linked);
            var manager = Field<EntityManager>(engine.ClientManager, "_entityManager");
            var boss = Field<Dictionary<ulong, Entity>>(manager, "_entities").Values.Single(e => e.Type == EntityType.BroodingMawlek);
            var bossFsm = boss.Object.Host.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Mawlek Control");
            var pickup = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.gameObject.scene.name == scene &&
                f.name == "Heart Piece" && f.FsmName == "Heart Container Control");
            Dump(pickup);
            var hero = HeroController.instance;
            Mark("MAWLEK PREFLIGHT boss=" + boss.Object.Host.transform.position + " pickup=" + pickup.transform.position +
                " active=" + pickup.gameObject.activeInHierarchy + " state=" + pickup.ActiveStateName +
                " authority=" + manager.IsSceneHost);
            hero.transform.position = boss.Object.Host.transform.position + new Vector3(5, 1, 0);
            hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
            hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return new WaitForSecondsRealtime(2);
            if (manager.IsSceneHost) {
                Dump(bossFsm);
                yield return Until(() => bossFsm.ActiveStateName != "Dormant" && !bossFsm.ActiveStateName.StartsWith("Wake") &&
                    bossFsm.ActiveStateName != "Title", "Mawlek original introduction finishes before combat damage", 15);
                Flag("mawlek.combat");
            }
            yield return Until(() => Has("mawlek.combat") && hero.acceptingInput && !hero.controlReqlinquished,
                "each knight regains control after the full native Mawlek introduction", 15);
            yield return ResourceBarrier("mawlek-combat");
            if (manager.IsSceneHost) {
                var health = boss.Object.Host.GetComponent<HealthManager>();
                ResourceCheck(health != null && health.gameObject.activeInHierarchy && !health.isDead,
                    "native Mawlek boss active before real lethal damage");
                health.ApplyExtraDamage(health.hp);
            }
            yield return Until(() => pickup.gameObject.activeInHierarchy && pickup.ActiveStateName == "Idle",
                "Mawlek native death releases an idle mask shard", 20);
            yield return ResourceBarrier("mawlek-reward-ready");
            if (role == collector || collector == "both") {
                hero.transform.position = pickup.transform.position;
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
                Physics2D.SyncTransforms();
                if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_RESOURCE_INTERRUPT") == "1") {
                    yield return Until(() => LocalResourceClaims.Contains(id) && pd.GetInt("heartPieces") == 4,
                        "fourth Mawlek shard reaches native counter before conversion", 8);
                    Mark("INTERRUPTION FIXTURE native scene transition during fourth-shard UI; no conversion writes");
                    Enter("Town");
                    yield return Until(() => AliveScene("Town"), "interrupted native pickup enters Town", 20);
                }
            }
            yield return Until(() => LocalResourceClaims.Contains(id) &&
                pd.GetInt("heartPieces") == expectedPieces && pd.GetInt("maxHealthBase") == expectedHealth,
                "Mawlek collision gives each player exactly one shard", 20);
            yield return ResourceIdle("Mawlek reward", 20);
            if (linked) yield return Until(() => pd.GetInt("maxHealth") == sharedMax + (initial == 3 ? 1 : 0) &&
                pd.GetInt("health") == sharedHealth + (initial == 3 ? 1 : 0),
                "Mawlek shared bar gains exactly one mask on the fourth fragment without an extra refill", 10);
            CheckFragmentNativeCleanup();
            yield return ResourceBarrier("mawlek-received");
            ResourceSend(id); ResourceSend(id);
            yield return new WaitForSecondsRealtime(1);
            ResourceCheck(pd.GetInt("heartPieces") == expectedPieces && pd.GetInt("maxHealthBase") == expectedHealth,
                "Mawlek replay cannot award another shard or convert another mask");
            Mark("FINAL PASS Mawlek native death and mask reward collector=" + collector + " initial=" + initial);
        } finally { On.HutongGames.PlayMaker.Fsm.EnterState -= trace; }
    }
}
