using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Game.Client.Entity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private IEnumerator MantisStoryGateChecks() {
        const string room = "Fungus2_15";
        var authority = role == Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_GATE_AUTHORITY");
        var mode = Environment.GetEnvironmentVariable("HOLLOWCONNECT_STORY_GATE_CHECK");
        var challenger = role == (Environment.GetEnvironmentVariable("HOLLOWCONNECT_MANTIS_CHALLENGER") ?? "host");
        var peer = host ? "guest" : "host";
        using var noContact = IgnoreExternalCombatDamage();
        if (!authority) yield return Until(() => Has("mantis.first"), "first Mantis arena entrant");
        TransitionPoint.lastEntered = "top1";
        yield return RuleEnter(room, "top1");
        if (authority) Flag("mantis.first");
        var fsms = Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => (f.gameObject.scene.name ?? "").StartsWith(room))
            .Concat(EntityReferences.Entities.Values.SelectMany(e => new[] { e.Object.Host, e.Object.Client })
                .Where(o => o != null).SelectMany(o => o.GetComponentsInChildren<PlayMakerFSM>(true))).Distinct().ToArray();
        Log("MANTIS ENTRIES " + string.Join(",", Object.FindObjectsOfType<TransitionPoint>(true)
            .Where(p => p.gameObject.scene.name == room).Select(p => p.name + "@" + p.transform.position)));
        Log("MANTIS ENTITIES " + string.Join(",", EntityReferences.Entities.Values.Select(e => e.Type + ":" + e.Object.Host?.name + ":scene=" + e.SourceScene + ":authority=" + e.IsNativeAuthority)));
        if (mode == "mantis-schema") foreach (var fsm in fsms.Where(f => f.name.Contains("Mantis") || f.name.StartsWith("Battle ") ||
            f.name.Contains("Challenge") || f.name.Contains("mantis_cage") || f.name.Contains("mantis_lord_opening"))) EncounterSchema(fsm);
        Check(fsms.Any(f => f.FsmName == "Mantis Throne Main"), "native story Mantis throne exists");
        var lords = EntityReferences.Entities.Values.Where(e => e.Type == EntityType.MantisLord).ToArray();
        Check(lords.Length == 3 && lords.All(e => e.SourceScene == room + "_boss" && e.IsNativeAuthority == authority),
            "three native additive lords have expected first-entrant authority");
        var prompt = fsms.Single(f => f.FsmName == "Challenge Start" && EntityReferences.Owner(f.gameObject) == null);
        var cage = fsms.Single(f => f.name == "mantis_cage_down" && EntityReferences.Owner(f.gameObject) == null);
        var localControl = fsms.Where(f => f.name == "Mantis Battle").ToArray();
        foreach (var control in localControl) EncounterSchema(control);
        var hero = HeroController.instance;
        var body = hero.GetComponent<Rigidbody2D>();
        var startX = prompt.transform.position.x + (challenger ? .5f : 2.5f);
        var floor = Physics2D.RaycastAll(new Vector2(startX, prompt.transform.position.y + 10), Vector2.down, 30, 1 << 8)
            .Where(h => !h.collider.isTrigger && h.normal.y > .95f).OrderBy(h => Math.Abs(h.point.y - prompt.transform.position.y)).First();
        var heroBounds = hero.GetComponent<Collider2D>().bounds;
        hero.transform.position = new Vector3(startX, floor.point.y + heroBounds.extents.y - (heroBounds.center.y - hero.transform.position.y) + .1f, 0);
        hero.AffectedByGravity(true); body.velocity = Vector2.zero; Physics2D.SyncTransforms();
        Log("MANTIS PROMPT PRECONDITIONS hero=" + hero.transform.position + " floor=" + floor.collider.name + ":" + floor.point +
            " prompt=" + prompt.enabled + ":" + string.Join(",", prompt.GetComponentsInChildren<Collider2D>(true)
                .Select(c => c.name + ":" + c.enabled + ":" + c.isTrigger + ":" + c.bounds)));
        Flag(role + ".mantis.ready");
        yield return Until(() => Has(peer + ".mantis.ready"), "both native Mantis participants ready");
        if (mode == "mantis-schema") yield break;
        var guardRequests = Environment.GetEnvironmentVariable("HOLLOWCONNECT_MANTIS_REQUEST_GUARDS") == "1";
        var main = EntityReferences.Entities.Values.Single(e => e.Type == EntityType.MantisLordBattle && e.Object.Host.name == "Battle Main");
        var transport = Field<Hkmp.Networking.Client.NetClient>(engine.ClientManager, "_netClient");
        void SendRequest(bool malformed) {
            var request = new Hkmp.Networking.Packet.Data.EntityNetworkData { Type = Hkmp.Game.Client.Entity.Component.EntityComponentType.ChallengePrompt };
            if (malformed) request.Packet.Write((byte)0); else request.Packet.Write(0);
            transport.UpdateManager.AddEntityData(main.Id, request);
        }
        if (guardRequests) {
            if (!authority) { SendRequest(true); Flag("mantis.invalid.sent"); }
            yield return Until(() => Has("mantis.invalid.sent"), "malformed peer challenge sent through real transport");
            yield return new WaitForSecondsRealtime(.6f);
            Check(lords.All(e => !e.Object.Host.activeInHierarchy) && hero.acceptingInput && !hero.controlReqlinquished,
                "malformed challenge cannot start combat or acquire local control");
            Flag(role + ".mantis.guarded");
            yield return Until(() => Has(peer + ".mantis.guarded"), "both endpoints reject malformed request before real input");
        }
        if (challenger) {
            for (var i = 0; i < 20 && !(prompt.ActiveStateName.StartsWith("In Range") && hero.cState.onGround); i++) {
                Log("MANTIS PROMPT WAIT state=" + prompt.ActiveStateName + " hero=" + hero.transform.position + " ground=" + hero.cState.onGround);
                yield return new WaitForSecondsRealtime(.5f);
            }
            Check(prompt.ActiveStateName.StartsWith("In Range") && hero.cState.onGround,
                "local hero naturally enters native Mantis challenge prompt");
            yield return RulePress(InputHandler.Instance.inputActions.up);
            Flag("mantis.challenged");
        }
        else yield return Until(() => Has("mantis.challenged"), "teammate actually presses native challenge input");
        var previous = "";
        var duplicated = false;
        var end = Time.realtimeSinceStartup + 40;
        while (Time.realtimeSinceStartup < end) {
            var sample = "prompt=" + (prompt == null ? "destroyed" : prompt.ActiveStateName) + " cage=" + cage.ActiveStateName +
                " local=" + string.Join(",", localControl.Select(f => f.FsmName + ":" + f.ActiveStateName)) +
                " lords=" + string.Join(",", lords.Select(e => e.Object.Host == null ? "native body destroyed" : e.Object.Host.name + ":" + e.Object.Host.activeInHierarchy + ":" +
                    e.Object.Host.GetComponent<HealthManager>().hp + ":" + e.Object.Host.GetComponent<PlayMakerFSM>().ActiveStateName)) +
                " throne=" + string.Join(",", EntityReferences.Entities.Values.Where(e => e.Type == EntityType.MantisThrone)
                    .Select(e => e.Object.Host.name + ":" + e.Object.Host.GetComponents<PlayMakerFSM>().First(f => f.FsmName.StartsWith("Mantis Throne")).ActiveStateName)) +
                " input=" + hero.acceptingInput + ":" + hero.controlReqlinquished + " flag=" + PlayerData.instance.GetBool("defeatedMantisLords");
            if (sample != previous) { Log("MANTIS FLOW " + sample); previous = sample; }
            if (guardRequests && !authority && !duplicated && lords.Any(e => e.Object.Client != null && e.Object.Client.activeInHierarchy)) {
                duplicated = true;
                for (var i = 0; i < 5; i++) SendRequest(false);
                Log("MANTIS DUPLICATE REQUESTS during native combat");
            }
            if (PlayerData.instance.GetBool("defeatedMantisLords") && localControl.All(f => f.ActiveStateName == "Floor + Cage") &&
                !cage.GetComponentsInChildren<Collider2D>(true).Any(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy)) break;
            if (authority) foreach (var lord in lords) {
                if (lord.Object.Host == null) continue;
                var hm = lord.Object.Host.GetComponent<HealthManager>();
                if (hm.gameObject.activeInHierarchy && !hm.IsInvincible && hm.hp > 0) {
                    Log("MANTIS NATIVE DAMAGE " + hm.name + " hp=" + hm.hp);
                    hm.ApplyExtraDamage(hm.hp);
                }
            }
            yield return new WaitForSecondsRealtime(.25f);
        }
        Check(PlayerData.instance.GetBool("defeatedMantisLords") &&
            !cage.GetComponentsInChildren<Collider2D>(true).Any(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy) &&
            hero.acceptingInput && !hero.controlReqlinquished && !PlayerData.instance.GetBool("disablePause"),
            "native two-phase Mantis completion releases local cage and player control");
        if (guardRequests && !authority) Check(duplicated, "duplicate challenge guard exercised during native combat");
        var originalX = hero.transform.position.x;
        using (new MonoMod.RuntimeDetour.Hook(typeof(HeroController).GetMethod("LookForInput", BindingFlags.Instance | BindingFlags.NonPublic),
            new Action<Action<HeroController>, HeroController>((orig, self) => { orig(self); if (self.acceptingInput) self.move_input = 1; })))
            yield return Until(() => hero.transform.position.x - originalX > 1, "native movement works after Mantis challenge and completion", 5);
        hero.move_input = 0;
        Flag(role + ".mantis.checked");
        yield return Until(() => Has(peer + ".mantis.checked"), "both native Mantis endings complete");
    }
}
