using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator StoryAcidRescueChecks() {
        var hero = HeroController.instance; var pd = PlayerData.instance; var body = hero.GetComponent<Rigidbody2D>();
        using var placementTrace = new MonoMod.RuntimeDetour.Hook(typeof(CoopRuntime).GetMethod("FindStoryRescuePosition", BindingFlags.Instance | BindingFlags.NonPublic),
            new Func<Func<CoopRuntime, HeroController, Vector3?>, CoopRuntime, HeroController, Vector3?>((orig, self, h) => {
                var origin = h.transform.position;
                var result = orig(self, h);
                if (self == runtime && !result.HasValue) {
                    var trail = Field<System.Collections.IEnumerable>(runtime, "rescueTrail").Cast<object>().Reverse().Take(8);
                    Log("ACID NO LANDING origin=" + origin + " down=" + runtime.Down + " hazard=" + Field<bool>(runtime, "rescueHazardDeath") +
                        " trail=" + string.Join(";", trail.Select(f => Field<Vector2>(f, "Position") + ":" + Field<Collider2D>(f, "Support")?.name)));
                }
                return result;
            }));
        var authority = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_RESCUE_AUTHORITY") ?? "host");
        if (!authority) yield return Until(() => Has("story.acid-first"), "first acid entrant");
        yield return RuleEnter("Fungus1_02");
        if (authority) Flag("story.acid-first");
        yield return StoryTogether("acid-entered");
        var bounds = hero.GetComponent<Collider2D>().bounds;
        var size = (Vector2)bounds.size; var offset = (Vector2)(bounds.center - hero.transform.position);
        var options = new List<(Vector3 Stand, int Direction, Collider2D Acid)>();
        foreach (var acid in Object.FindObjectsOfType<DamageHero>().Where(d => d.hazardType == 3 && d.enabled &&
            StoryFixtureScene(d.gameObject, "Fungus1_02")).SelectMany(d => d.GetComponents<Collider2D>()).Where(c => c.enabled && c.gameObject.activeInHierarchy)) {
            foreach (var direction in new[] { 1, -1 }) {
                var x = direction == 1 ? acid.bounds.min.x - 1.5f : acid.bounds.max.x + 1.5f;
                var support = Physics2D.Raycast(new Vector2(x, acid.bounds.max.y + 6), Vector2.down, 12, 1 << 8);
                if (support.collider == null || support.collider.isTrigger || support.normal.y < .9f ||
                    Math.Abs(support.point.y - acid.bounds.max.y) > 3) continue;
                var stand = new Vector3(x, support.point.y + size.y / 2 - offset.y + .07f, 0);
                var safe = true;
                foreach (var dx in new[] { 0f, -direction * 3f }) {
                    var point = stand + Vector3.right * dx;
                    var floor = Physics2D.Raycast(point, Vector2.down, 2, 1 << 8);
                    if (floor.collider == null || floor.collider.isTrigger || Math.Abs(floor.point.y - support.point.y) > .2f ||
                        Physics2D.OverlapBoxAll((Vector2)point + offset, size * .95f, 0).Any(c => !c.transform.IsChildOf(hero.transform) &&
                            (c.GetComponentInParent<DamageHero>() != null || !c.isTrigger && c.gameObject.layer == 8))) { safe = false; break; }
                }
                if (safe) options.Add((stand, direction, acid));
            }
        }
        Check(options.Count > 0, "native acid has an adjacent supported shore for a real walking collision");
        var fixture = options.OrderBy(o => Vector2.Distance(o.Stand, hero.transform.position)).First();
        Log("STORY NATIVE ACID " + fixture.Acid.name + " bounds=" + fixture.Acid.bounds + " shore=" + fixture.Stand + " direction=" + fixture.Direction);
        Modding.ModHooks.SoulGainHook += _ => 0;
        var rounds = Environment.GetEnvironmentVariable("HOLLOWCONNECT_ACID_TRACE") == "1" ? 2 : 1;
        for (var round = 0; round < rounds; round++) foreach (var victimRole in new[] { "guest", "host" }) {
            var victim = role == victimRole; var step = "acid." + victimRole + "." + round;
            pd.SetInt("health", 5); pd.SetInt("healthBlue", 0); pd.SetBool("isInvincible", false);
            pd.SetInt("geo", 321); pd.SetInt("geoPool", 67); pd.SetString("shadeScene", "Town"); pd.SetInt("MPCharge", 0);
            hero.takeNoDamage = true; hero.cState.invulnerable = hero.cState.recoiling = false;
            hero.transform.position = fixture.Stand + Vector3.left * (victim ? 0 : fixture.Direction * 3);
            body.velocity = Vector2.zero; hero.AffectedByGravity(true); Physics2D.SyncTransforms();
            yield return new WaitForFixedUpdate();
            yield return Until(() => hero.cState.onGround && hero.acceptingInput, "native acid shore grounded " + step);
            yield return new WaitForSecondsRealtime(.5f);
            yield return StoryTogether(step + ".ready");
            var serial = runtime.DeathSerial; var quota = runtime.RescuesUsed;
            if (victim) {
                hero.takeNoDamage = false; pd.SetInt("health", 1);
                using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
                    new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = fixture.Direction; })))
                    yield return Until(() => runtime.Down, "native walking into acid causes a lethal collision " + step, 15);
                Check(Field<bool>(runtime, "rescueHazardDeath"), "native hazard type reaches story rescue policy " + step);
                Log("STORY ACID ANCHOR " + runtime.DeathPosition);
                Log("ACID DEATH ROUTE giveUpRequested=" + Field<bool>(runtime, "giveUpRequested") + " returning=" + runtime.Returning +
                    " nativeOrigin=" + Field<Vector3>(Field<object>(runtime, "personalDeath"), "Position"));
                Log("ACID BODY fixture=" + size + "/" + offset + " product=" + Field<Vector2>(runtime, "rescueBodySize") + "/" + Field<Vector2>(runtime, "rescueBodyOffset") +
                    " heroScale=" + hero.transform.lossyScale + " actual=" + hero.GetComponent<Collider2D>().bounds);
                foreach (var c in Physics2D.OverlapBoxAll((Vector2)runtime.DeathPosition + offset, size * .9f, 0)
                    .Where(c => c.GetComponentInParent<DamageHero>() != null)) {
                    var d = c.GetComponentInParent<DamageHero>();
                    Log("ACID ANCHOR OVERLAP " + c.name + " damage=" + d.name + " hazard=" + d.hazardType + " enabled=" + d.enabled +
                        " owner=" + Hkmp.Game.Client.Entity.EntityReferences.Owner(c.gameObject)?.Type + " bounds=" + c.bounds);
                }
                Check(!Physics2D.OverlapBoxAll((Vector2)runtime.DeathPosition + offset, size * .9f, 0)
                    .Any(c => c.GetComponentInParent<DamageHero>() != null), "acid rescue anchor leaves damaging volume " + step);
                yield return Until(() => !runtime.Down && !runtime.Returning && pd.health == 1 && hero.cState.onGround && hero.acceptingInput,
                    "acid rescue restores supported controllable knight " + step, 25);
                Check(runtime.DeathSerial == serial + 1 && runtime.RescuesUsed == quota + 1 && pd.GetInt("geo") == 321 && pd.GetInt("geoPool") == 67,
                    "acid rescue retains one death, one quota charge and personal geo " + step);
            } else {
                yield return Until(() => runtime.Peers.Values.Single().Life == Life.Down, "acid teammate down received " + step);
                var peer = runtime.Peers.Single(); var service = Field<ShadeService>(runtime, "shades");
                yield return Until(() => service.Get(peer.Key) != null, "acid shade visible " + step);
                var anchor = service.Get(peer.Key).transform.position; var start = hero.transform.position;
                yield return StoryWalk(anchor.x - fixture.Direction * 1.5f, "acid rescuer " + step);
                Check(Vector2.Distance(start, hero.transform.position) > .4f, "acid rescuer uses physical route after death " + step);
                if (fixture.Direction == 1) hero.FaceRight(); else hero.FaceLeft();
                hero.Attack(GlobalEnums.AttackDirection.normal);
                yield return Until(() => peer.Value.Life == Life.Alive, "real nail rescues acid victim " + step, 8);
                Check(pd.GetInt("MPCharge") == RescueFeedback.SoulReward, "acid rescue pays one soul reward " + step);
            }
            yield return StoryTogether(step + ".rescued"); yield return new WaitForSecondsRealtime(2);
        }
    }
}
