using System;
using System.Collections;
using System.Linq;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private bool DreamDialogueCase => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_DIALOGUE_CASE"));

    private static string DreamPath(Transform t) => t.parent == null ? t.name : DreamPath(t.parent) + "/" + t.name;

    private IEnumerator DreamDialogueChecks() {
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_DIALOGUE_CASE") is "grub" or "grub-hit") {
            yield return DreamGrubSample();
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_DIALOGUE_CASE") == "enemy") {
            yield return DreamEnemySample();
            yield break;
        }
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_DIALOGUE_CASE") != "inspect") {
            yield return DreamDialogueSamples();
            yield break;
        }
        foreach (var scene in new[] { "Town", "Deepnest_41", "Crossroads_01" }) {
            yield return Room(scene);
            var dream = HeroController.instance.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Nail");
            Mark("DREAM SCHEMA HERO state=" + dream.ActiveStateName); Dump(dream);
            foreach (var f in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f =>
                f.name == "Text Dream" || f.gameObject.scene.name == scene && f.FsmName.IndexOf("dream", StringComparison.OrdinalIgnoreCase) >= 0)) {
                Mark("DREAM SCHEMA " + DreamPath(f.transform) + "/" + f.FsmName + " state=" + f.ActiveStateName +
                    " active=" + f.gameObject.activeInHierarchy + " enabled=" + f.enabled + " position=" + f.transform.position);
                Dump(f);
                foreach (var c in f.GetComponents<Collider2D>()) Mark("DREAM COLLIDER " + DreamPath(c.transform) +
                    " bounds=" + c.bounds + " enabled=" + c.enabled + " layer=" + c.gameObject.layer + " tag=" + c.tag);
            }
            foreach (var e in Object.FindObjectsOfType<EnemyDreamnailReaction>())
                Mark("DREAM ENEMY " + DreamPath(e.transform) + " position=" + e.transform.position);
            Flag(role + ".dream-schema." + scene);
            yield return Until(() => Has((host ? "guest" : "host") + ".dream-schema." + scene), "both captured dream schema " + scene);
        }
    }

    private string DreamTextSnapshot() {
        var box = Object.FindObjectsOfType<DialogueBox>(true).Single(d => d.name == "Text");
        var mesh = box.GetComponents<Component>().Single(c => c.GetType().Name == "TextMeshPro");
        var text = mesh.GetType().GetProperty("text").GetValue(mesh, null) as string;
        var visible = (int)mesh.GetType().GetProperty("maxVisibleCharacters").GetValue(mesh, null);
        Mark("DREAM TEXT key=" + box.currentConversation + " hidden=" + Field<bool>(box, "hidden") + " visible=" + visible +
            " active=" + box.gameObject.activeInHierarchy + " renderer=" + box.GetComponent<Renderer>().enabled +
            " text=" + text?.Replace("\n", "\\n"));
        ResourceCheck(!string.IsNullOrEmpty(box.currentConversation) && !string.IsNullOrEmpty(text) && text != "#!##!#" &&
            !Field<bool>(box, "hidden") && visible > 0 && box.gameObject.activeInHierarchy && box.GetComponent<Renderer>().enabled,
            "dream dialogue shows nonempty visible native text");
        return box.currentConversation;
    }

    private IEnumerator DreamDialogueSwing(Action follow = null) {
        var hero = HeroController.instance;
        var fsm = hero.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Nail");
        var box = hero.transform.Find("Dream Effects/Hitbox").gameObject;
        yield return Until(() => fsm.ActiveStateName == "Inactive" && hero.CanDreamNail(), "native NPC dream input ready", 8);
        var action = InputHandler.Instance.inputActions.dreamNail;
        var frame = Time.frameCount + 1;
        var inputDeadline = Time.realtimeSinceStartup + 1;
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            // PlayMaker can poll in FixedUpdate, so retain the press until the
            // native FSM acknowledges it instead of pulsing one render frame.
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ?
                Time.frameCount >= frame && Time.realtimeSinceStartup < inputDeadline && fsm.ActiveStateName == "Inactive" : orig(self))))
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount >= frame : orig(self)))) {
            var end = Time.realtimeSinceStartup + 5;
            while (!box.activeInHierarchy && Time.realtimeSinceStartup < end) {
                // Preserve native grounded entry before following a moving
                // enemy. Moving first can make CanDreamNail reject the input.
                if (fsm.ActiveStateName is "Charge" or "Slash Antic") follow?.Invoke();
                yield return null;
            }
            ResourceCheck(box.activeInHierarchy, "native dream charge activated real hitbox; state=" + fsm.ActiveStateName +
                " grounded=" + hero.cState.onGround + " position=" + hero.transform.position);
            Mark("DREAM HITBOX " + box.GetComponent<Collider2D>().bounds);
            yield return new WaitForSecondsRealtime(.1f);
        }
    }

    private IEnumerator DreamDialogueSamples() {
        var hitCase = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_DIALOGUE_CASE") == "hit";
        var bothCase = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_DIALOGUE_CASE") == "simultaneous";
        var samples = hitCase ? new[] { new[] { "Deepnest_41", "Happy Spider NPC/Dream Dialogue" } } : new[] {
            new[] { "Town", "_NPCs/Elderbug/Dream Dialogue" },
            new[] { "Deepnest_41", "Dream Dialogue Corpse" },
            new[] { "Deepnest_41", "Happy Spider NPC/Dream Dialogue" }
        };
        for (var sample = 0; sample < samples.Length; sample++) {
            yield return Room(samples[sample][0]);
            var target = Object.FindObjectsOfType<PlayMakerFSM>(true).Single(f => f.FsmName == "npc_dream_dialogue" &&
                f.gameObject.activeInHierarchy && DreamPath(f.transform) == samples[sample][1]);
            ResourceCheck(target.enabled && target.ActiveStateName == "Idle", "native dream requester ready " + samples[sample][1]);
            Dump(target);
            foreach (var text in Object.FindObjectsOfType<PlayMakerFSM>(true).Where(f => f.name == "Text")) Dump(text);
            var hero = HeroController.instance;
            var hit = 0;
            var remoteHits = 0;
            On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
                if (fsm == target.Fsm && state.Name == "Take Control") hit++;
                if (fsm == target.Fsm || fsm.Name == "Dream Nail" || fsm.GameObject?.name == "Text")
                    Mark("DREAM STATE " + fsm.GameObject?.name + "/" + fsm.Name + " -> " + state.Name);
                orig(fsm, state);
            };
            On.HutongGames.PlayMaker.Fsm.hook_ProcessEvent contact = (orig, fsm, ev, data) => {
                if (hitCase && fsm.GameObject == target.transform.parent.gameObject && fsm.Name == "tink_effect" && ev?.Name == "TAKE DAMAGE") {
                    var source = data?.SentByGameObject ?? data?.SentByFsm?.GameObject;
                    var remote = source != null && source.GetComponentsInParent<Component>(true).Any(c => c.GetType().Name == "RemoteVisualOpacity");
                    if (remote) remoteHits++;
                    Mark("DREAM NAIL CONTACT source=" + source?.name + " remote=" + remote + " requester=" + target.ActiveStateName);
                }
                orig(fsm, ev, data);
            };
            On.HutongGames.PlayMaker.Fsm.EnterState += trace;
            On.HutongGames.PlayMaker.Fsm.ProcessEvent += contact;
            try {
                foreach (var caster in hitCase ? new[] { Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_DIALOGUE_CASTER") ?? "host" } :
                    bothCase ? new[] { "both" } : new[] { "host", "guest" }) {
                    var stage = sample + "." + caster;
                    var casting = role == caster || bothCase;
                    hero.transform.position = target.transform.position + new Vector3(casting ? -1.5f : -7, 1, 0);
                    hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
                    hero.FaceRight(); hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                    yield return new WaitForSecondsRealtime(.4f);
                    yield return Until(() => hero.cState.onGround && hero.CanDreamNail(), "native grounded NPC dream setup " + stage, 8);
                    Flag(role + ".dream-ready." + stage);
                    yield return Until(() => Has((host ? "guest" : "host") + ".dream-ready." + stage), "both dream positions " + stage);
                    if (casting) {
                        var before = hit;
                        yield return DreamDialogueSwing();
                        yield return Until(() => target.ActiveStateName == "Convo", "native dream collision enters Convo " + stage, 8);
                        yield return new WaitForSecondsRealtime(.6f);
                        ResourceCheck(hit > before && !hero.acceptingInput && PlayerData.instance.GetBool("disablePause"), "dream requester owns native input lock");
                        DreamTextSnapshot();
                        if (bothCase) {
                            Flag(role + ".dream-open." + stage);
                            yield return Until(() => Has((host ? "guest" : "host") + ".dream-open." + stage), "both independently read native dream text " + stage);
                        } else {
                            Flag("dream-open." + stage);
                            yield return Until(() => Has("dream-peer-observed." + stage), "peer checked dream side effects " + stage);
                        }
                        if (hitCase) {
                            Mark("DREAM AFTER PEER HIT requester=" + target.ActiveStateName + " active=" + target.gameObject.activeInHierarchy +
                                " input=" + hero.acceptingInput + " pause=" + PlayerData.instance.GetBool("disablePause"));
                            ResourceCheck(remoteHits > 0, "real UDP peer nail contacts Midwife during native dream dialogue");
                            ResourceCheck(target.gameObject.activeInHierarchy && target.ActiveStateName == "Convo" &&
                                !hero.acceptingInput && PlayerData.instance.GetBool("disablePause"), "peer nail preserves native dream requester and input ownership");
                            DreamTextSnapshot();
                        }
                        var end = Time.realtimeSinceStartup + 15;
                        while (!hero.acceptingInput && Time.realtimeSinceStartup < end) {
                            yield return ResourcePress(InputHandler.Instance.inputActions.jump);
                            yield return new WaitForSecondsRealtime(.25f);
                        }
                        ResourceCheck(hero.acceptingInput && !hero.controlReqlinquished && !PlayerData.instance.GetBool("disablePause") &&
                            target.ActiveStateName == "Idle", "dream closes through native input and restores control/pause " + stage);
                        if (hitCase) {
                            var midwife = target.transform.parent.gameObject;
                            var behavior = midwife.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Control");
                            hero.transform.position = midwife.transform.position + new Vector3(-1.8f, 0, 0);
                            hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                            yield return Until(() => behavior.ActiveStateName == "Retreated", "post-dream hit retains native Midwife retreat", 8);
                            ResourceCheck(hero.acceptingInput && !PlayerData.instance.GetBool("disablePause"), "post-dream hit keeps control/pause restored");
                        }
                        Flag("dream-closed." + stage);
                    } else {
                        yield return Until(() => Has("dream-open." + stage), "caster opens native NPC dream " + stage, 15);
                        Mark("DREAM PEER requester=" + target.ActiveStateName + " input=" + hero.acceptingInput + " pause=" + PlayerData.instance.GetBool("disablePause"));
                        ResourceCheck(target.ActiveStateName == "Idle" && hero.acceptingInput && !PlayerData.instance.GetBool("disablePause"),
                            "peer dream swing does not open or lock local dream UI " + stage);
                        if (hitCase) {
                            var midwife = target.transform.parent.gameObject;
                            var behavior = midwife.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Control");
                            hero.transform.position = midwife.transform.position + new Vector3(-1.8f, 0, 0);
                            hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                            Mark("DREAM ATTACK ENTRY Midwife=" + behavior.ActiveStateName + " position=" + hero.transform.position);
                            yield return Until(() => behavior.ActiveStateName == "Idle" && hero.cState.onGround,
                                "attacker enters native Midwife appearance range before swinging", 8);
                            hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                            yield return Until(() => behavior.ActiveStateName == "Retreated", "actual nail hit retreats attacker's idle Midwife", 8);
                            yield return new WaitForSecondsRealtime(.3f);
                        }
                        Flag("dream-peer-observed." + stage);
                        yield return Until(() => Has("dream-closed." + stage), "caster closes native NPC dream " + stage, 20);
                    }
                    Flag(role + ".dream-finished." + stage);
                    yield return Until(() => Has((host ? "guest" : "host") + ".dream-finished." + stage), "both completed dream sample " + stage);
                }
            } finally {
                On.HutongGames.PlayMaker.Fsm.EnterState -= trace;
                On.HutongGames.PlayMaker.Fsm.ProcessEvent -= contact;
            }
        }
    }

    private IEnumerator DreamEnemySample() {
        yield return Room("Crossroads_01");
        var hero = HeroController.instance;
        foreach (var caster in new[] { "host", "guest" }) {
            var enemy = Object.FindObjectsOfType<EnemyDreamnailReaction>().First(e => e.gameObject.activeInHierarchy);
            ResourceCheck(enemy != null, "native ordinary enemy dream receiver exists");
            var health = enemy.GetComponent<HealthManager>();
            var hits = 0;
            On.EnemyDreamnailReaction.hook_RecieveDreamImpact trace = (orig, self) => {
                if (self == enemy) { hits++; Mark("DREAM ENEMY IMPACT " + self.name); }
                orig(self);
            };
            On.EnemyDreamnailReaction.RecieveDreamImpact += trace;
            try {
                void Position() {
                    hero.transform.position = enemy.transform.position + new Vector3(-1.5f, .2f, 0);
                    hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
                    hero.FaceRight(); Physics2D.SyncTransforms();
                }
                if (role == caster) { Position(); hero.AffectedByGravity(true); }
                else { hero.transform.position = enemy.transform.position + Vector3.left * 7; Freeze(); }
                // Native enemy motion stays active; follow only the test hero
                // during charge so the real dream polygon can contact it.
                yield return new WaitForSecondsRealtime(.3f);
                if (role == caster) yield return Until(() => hero.cState.onGround && hero.CanDreamNail(), "enemy sample native floor " + caster, 8);
                Flag(role + ".enemy-ready." + caster);
                yield return Until(() => Has((host ? "guest" : "host") + ".enemy-ready." + caster), "both enemy dream positions " + caster);
                if (role == caster) {
                    PlayerData.instance.SetInt("MPCharge", 0);
                    var hp = health.hp;
                    yield return DreamDialogueSwing(Position);
                    yield return Until(() => hits > 0, "real dream polygon contacts ordinary enemy " + caster, 5);
                    yield return new WaitForSecondsRealtime(.2f);
                    foreach (var component in Object.FindObjectsOfType<Component>().Where(c => c != null && c.GetType().Name == "TextMeshPro" &&
                        c.gameObject.activeInHierarchy && c.GetComponent<Renderer>()?.enabled == true)) {
                        var value = component.GetType().GetProperty("text").GetValue(component, null) as string;
                        if (!string.IsNullOrEmpty(value)) Mark("DREAM ENEMY TEXT " + DreamPath(component.transform) + "=" + value.Replace("\n", "\\n"));
                    }
                    yield return Until(() => hero.acceptingInput && !hero.controlReqlinquished && !PlayerData.instance.GetBool("disablePause") &&
                        hero.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Nail").ActiveStateName == "Inactive" &&
                        !hero.transform.Find("Dream Effects/Hitbox").gameObject.activeInHierarchy,
                        "ordinary enemy dream swing returns native input " + caster, 8);
                    ResourceCheck(health != null && health.hp == hp && PlayerData.instance.GetInt("MPCharge") > 0,
                        "enemy dream grants soul without damage");
                    Flag("enemy-complete." + caster);
                } else {
                    yield return Until(() => Has("enemy-complete." + caster), "peer dreams ordinary enemy " + caster, 15);
                    Mark("DREAM ENEMY PEER input=" + hero.acceptingInput + " relinquished=" + hero.controlReqlinquished +
                        " dreamFSM=" + hero.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Nail").ActiveStateName +
                        " scene=" + GameManager.instance.sceneName + " position=" + hero.transform.position +
                        " pause=" + PlayerData.instance.GetBool("disablePause"));
                    ResourceCheck(hero.acceptingInput && !hero.controlReqlinquished && !PlayerData.instance.GetBool("disablePause"),
                        "ordinary enemy dream does not lock peer");
                }
                Flag(role + ".enemy-finished." + caster);
                yield return Until(() => Has((host ? "guest" : "host") + ".enemy-finished." + caster), "both enemy checks complete " + caster);
            } finally { On.EnemyDreamnailReaction.RecieveDreamImpact -= trace; }
        }
    }

    private IEnumerator DreamGrubSample() {
        var hitCase = Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_DIALOGUE_CASE") == "grub-hit";
        yield return Room("Crossroads_03");
        var hero = HeroController.instance;
        var bottle = Object.FindObjectsOfType<PlayMakerFSM>().Single(f => f.FsmName == "Bottle Control");
        var jar = bottle.GetComponent<Collider2D>();
        var target = bottle.transform.Find("Bottle Physical/Dream Dialogue").GetComponent<PlayMakerFSM>();
        ResourceCheck(bottle.ActiveStateName == "Idle" && jar != null && jar.enabled, "native unbroken grub bottle ready");
        foreach (var f in bottle.GetComponentsInChildren<PlayMakerFSM>(true)) {
            Mark("DREAM GRUB SCHEMA " + DreamPath(f.transform) + "/" + f.FsmName + " state=" + f.ActiveStateName +
                " active=" + f.gameObject.activeInHierarchy + " position=" + f.transform.position);
            Dump(f);
        }
        foreach (var c in bottle.GetComponentsInChildren<Collider2D>(true))
            Mark("DREAM GRUB COLLIDER " + DreamPath(c.transform) + " bounds=" + c.bounds + " enabled=" + c.enabled +
                " layer=" + c.gameObject.layer + " tag=" + c.tag);
        var broken = false;
        var impacts = 0;
        var remoteHits = 0;
        On.HutongGames.PlayMaker.Fsm.hook_EnterState trace = (orig, fsm, state) => {
            if (fsm == bottle.Fsm) {
                Mark("DREAM GRUB STATE " + state.Name);
                if (state.Name != "Idle") broken = true;
            }
            if (fsm == target.Fsm) Mark("DREAM GRUB REQUESTER " + state.Name);
            orig(fsm, state);
        };
        On.HutongGames.PlayMaker.Fsm.hook_ProcessEvent contact = (orig, fsm, ev, data) => {
            if (fsm.GameObject == bottle.gameObject) {
                var source = data?.SentByGameObject ?? data?.SentByFsm?.GameObject;
                var remote = source != null && source.GetComponentsInParent<Component>(true).Any(c => c.GetType().Name == "RemoteVisualOpacity");
                if (ev?.Name == "DREAM IMPACT" && source?.name == "Hitbox") impacts++;
                Mark("DREAM GRUB EVENT " + fsm.Name + " " + ev?.Name + " source=" + source?.name + " remote=" + remote);
            }
            orig(fsm, ev, data);
        };
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.hook_DoTriggerEnter2D nailContact = (orig, self, collider) => {
            if (self.Fsm == bottle.Fsm && collider != null && collider.CompareTag("Nail Attack") &&
                collider.GetComponentsInParent<Component>(true).Any(c => c.GetType().Name == "RemoteVisualOpacity")) {
                remoteHits++;
                Mark("DREAM GRUB REMOTE NAIL source=" + collider.name + " requester=" + target.ActiveStateName);
            }
            orig(self, collider);
        };
        On.HutongGames.PlayMaker.Fsm.EnterState += trace;
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += contact;
        On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerEnter2D += nailContact;
        try {
            foreach (var caster in hitCase ? new[] { Environment.GetEnvironmentVariable("HOLLOWCONNECT_DREAM_DIALOGUE_CASTER") ?? "host" } : new[] { "host", "guest" }) {
                var casting = role == caster;
                var initialCount = PlayerData.instance.GetInt("grubsCollected");
                hero.transform.position = bottle.transform.position + new Vector3(casting ? -1.5f : -7, .2f, 0);
                hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
                hero.FaceRight(); hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                yield return Until(() => hero.cState.onGround && hero.CanDreamNail(), "native grub floor " + caster, 8);
                Mark("DREAM GRUB ENTRY caster=" + caster + " casting=" + casting + " hero=" + hero.transform.position +
                    " jar=" + jar.bounds + " state=" + bottle.ActiveStateName);
                Flag(role + ".grub-ready." + caster);
                yield return Until(() => Has((host ? "guest" : "host") + ".grub-ready." + caster), "both grub dream positions " + caster);
                if (casting) {
                    var before = impacts;
                    yield return DreamDialogueSwing();
                    yield return Until(() => target.ActiveStateName == "Convo", "native grub dream enters Convo " + caster, 8);
                    yield return new WaitForSecondsRealtime(.6f);
                    ResourceCheck(impacts > before && !broken && jar.enabled && bottle.ActiveStateName == "Idle",
                        "real dream impact opens grub thought without shattering jar " + caster);
                    ResourceCheck(DreamTextSnapshot() == "GRUB_BOTTLE_DREAM", "native grub dream conversation key");
                    Flag("grub-cast." + caster);
                    yield return Until(() => Has("grub-peer-observed." + caster), "peer checked grub dream " + caster, 15);
                    Mark("DREAM GRUB AFTER PEER requester=" + target.ActiveStateName + " active=" + target.gameObject.activeInHierarchy +
                        " state=" + bottle.ActiveStateName + " collider=" + jar.enabled + " changed=" + broken +
                        " collected=" + PlayerData.instance.GetInt("grubsCollected") + " remoteHits=" + remoteHits +
                        " input=" + hero.acceptingInput + " relinquished=" + hero.controlReqlinquished + " pause=" + PlayerData.instance.GetBool("disablePause"));
                    if (hitCase) ResourceCheck(remoteHits > 0, "real UDP peer nail contacts grub jar during dream");
                    ResourceCheck(!broken && jar.enabled && target.gameObject.activeInHierarchy && target.ActiveStateName == "Convo",
                        "grub dream requester survives peer observation or nail hit");
                    DreamTextSnapshot();
                    var end = Time.realtimeSinceStartup + 15;
                    while (!hero.acceptingInput && Time.realtimeSinceStartup < end) {
                        yield return ResourcePress(InputHandler.Instance.inputActions.jump);
                        yield return new WaitForSecondsRealtime(.25f);
                    }
                    ResourceCheck(hero.acceptingInput && !hero.controlReqlinquished && !PlayerData.instance.GetBool("disablePause") &&
                        target.ActiveStateName == "Idle", "grub dream closes natively and restores input/pause " + caster);
                    if (hitCase) {
                        yield return Until(() => PlayerData.instance.GetInt("grubsCollected") == initialCount + 1,
                            "peer broken jar shares grub once while reader finishes dream", 8);
                        hero.transform.position = bottle.transform.position + new Vector3(-1.8f, 0, 0);
                        hero.FaceRight(); hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                        yield return Until(() => broken && !jar.enabled, "post-dream native nail shatters local grub jar", 8);
                        ResourceCheck(PlayerData.instance.GetInt("grubsCollected") == initialCount + 1,
                            "post-dream local grub collection is not counted twice");
                    } else ResourceCheck(PlayerData.instance.GetInt("grubsCollected") == initialCount, "dream reading does not collect grub");
                    Flag("grub-closed." + caster);
                } else {
                    yield return Until(() => Has("grub-cast." + caster), "peer casts real dream at grub " + caster, 15);
                    ResourceCheck(!broken && target.ActiveStateName == "Idle" && hero.acceptingInput && !PlayerData.instance.GetBool("disablePause"),
                        "peer grub dream leaves local jar and input independent");
                    if (hitCase) {
                        hero.transform.position = bottle.transform.position + new Vector3(-1.8f, 0, 0);
                        hero.FaceRight(); hero.AffectedByGravity(true); Physics2D.SyncTransforms();
                        yield return Until(() => bottle.FsmVariables.FindFsmBool("Hero Range").Value && hero.cState.onGround,
                            "native bottle range ready for teammate real nail", 8);
                        hero.Attack(GlobalEnums.AttackDirection.normal); Physics2D.SyncTransforms();
                        yield return Until(() => broken && !jar.enabled, "teammate real nail shatters native grub jar", 8);
                        yield return new WaitForSecondsRealtime(.4f);
                    }
                    Flag("grub-peer-observed." + caster);
                    yield return Until(() => Has("grub-closed." + caster), "caster closes native grub dream " + caster, 20);
                    ResourceCheck(hero.acceptingInput && !hero.controlReqlinquished && !PlayerData.instance.GetBool("disablePause"), "grub dream keeps peer input restored");
                }
                Flag(role + ".grub-finished." + caster);
                yield return Until(() => Has((host ? "guest" : "host") + ".grub-finished." + caster), "both grub dream checks complete " + caster);
            }
        } finally {
            On.HutongGames.PlayMaker.Fsm.EnterState -= trace;
            On.HutongGames.PlayMaker.Fsm.ProcessEvent -= contact;
            On.HutongGames.PlayMaker.Actions.Trigger2dEvent.DoTriggerEnter2D -= nailContact;
        }
    }
}
