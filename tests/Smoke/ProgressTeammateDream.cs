using System;
using System.Collections;
using System.Linq;
using HollowConnect;
using HollowConnect.Core;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator TeammateDream() {
        var shared = Environment.GetEnvironmentVariable("HOLLOWCONNECT_CHARM_SHARE") != "0";
        yield return Room("Crossroads_47");
        if (Has(role + ".failed")) yield break;
        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        ResourceCheck(JsonConvert.DeserializeObject<Settings>("{\"ShareProgress\":true}").ShareCharms,
            "old settings JSON retains charm default");
        ResourceCheck(runtime.Shared && runtime.ShareCharms == shared, "host charm rule overrides opposite guest config");
        CoopMod.Settings.ShareCharms = !shared;
        pd.SetBool(host ? "gotCharm_2" : "gotCharm_3", true);
        yield return ResourceBarrier("charm-local");
        yield return new WaitForSecondsRealtime(1.2f);
        ResourceCheck(pd.GetBool(host ? "gotCharm_3" : "gotCharm_2") == shared,
            "room charm rule frozen while local config changes");
        pd.SetBool(host ? "hasWalljump" : "hasSuperDash", true);
        yield return Until(() => pd.GetBool("hasWalljump") && pd.GetBool("hasSuperDash"), "other permanent upgrades still share", 5);
        var send = typeof(CoopRuntime).GetMethod("Send", ResourcePrivate);
        if (host) send.Invoke(runtime, new object[] { new Message { Kind = Kind.ProgressOffer, Key = "gotCharm_4", Value = 1 } });
        yield return new WaitForSecondsRealtime(.6f);
        ResourceCheck(pd.GetBool("gotCharm_4") == shared, "server rejects disabled charm offer even if client sends it");
        if (!shared) {
            typeof(CoopRuntime).GetMethod("Receive", ResourcePrivate).Invoke(runtime, new object[] {
                new Message { Kind = Kind.ProgressGrant, Key = "gotCharm_5", Value = 1, Session = runtime.Session, Round = Hkmp.CoopContext.Epoch } });
            ResourceCheck(!pd.GetBool("gotCharm_5"), "disabled client ignores delayed charm grant");
        }
        var manager = Field<Hkmp.Game.Client.Save.SaveManager>(engine.ClientManager, "_saveManager");
        var mapping = Hkmp.Game.Client.Save.SaveDataMapping.Instance;
        var incoming = manager.GetType().GetMethod("UpdateSaveWithData", ResourcePrivate, null,
            new[] { typeof(ushort), typeof(byte[]) }, null);
        var hosting = manager.IsHostingServer; manager.IsHostingServer = false;
        try {
            incoming.Invoke(manager, new object[] { mapping.PlayerDataIndices["gotCharm_6"], Hkmp.Util.EncodeUtil.EncodeSaveDataValue(true) });
            var count = pd.GetInt("charmsOwned");
            incoming.Invoke(manager, new object[] { mapping.PlayerDataIndices["charmsOwned"], Hkmp.Util.EncodeUtil.EncodeSaveDataValue(count + 10) });
            ResourceCheck(!pd.GetBool("gotCharm_6") && pd.GetInt("charmsOwned") == count, "full-save replay cannot bypass charm ledger");
        } finally { manager.IsHostingServer = hosting; }
        yield return ResourceBarrier("charm-checked");
        var slots = pd.GetInt("charmSlots");
        if (host) ResourceSend("notch:salubraNotch1");
        yield return Until(() => pd.GetInt("charmSlots") == slots + 1 && LocalResourceClaims.Contains("notch:salubraNotch1"),
            "charm slots still share with the independent charm rule", 8);
        yield return ResourceIdle("independent charm notch");
        yield return ResourceBarrier("notch-checked");
        if (!host) pd.SetBoolInternal("gotCharm_4", false);
        yield return ResourceReconnectSameProfile();
        yield return Until(() => runtime.ShareCharms == shared && (pd.GetBool("gotCharm_4") == shared),
            "real reconnect snapshot obeys frozen charm rule", 8);
        ResourceCheck(pd.GetBool(host ? "gotCharm_2" : "gotCharm_3"), "reconnect does not remove personal charms");
        yield return ResourceBarrier("charm-reconnect-checked");
        pd.SetBool("hasDreamNail", true); pd.SetBool("equippedCharm_30", false);
        pd.SetInt("MPCharge", 0); pd.SetInt("MPReserve", 0);
        var anchor = GameObject.Find("Station Bell").transform.position + new Vector3(-4, 3, 0);
        hero.transform.position = anchor + Vector3.right * (host ? -1 : 1);
        if (host) hero.FaceRight(); else hero.FaceLeft();
        hero.AffectedByGravity(true); hero.GetComponent<Rigidbody2D>().velocity = Vector2.zero;
        Physics2D.SyncTransforms();
        yield return Until(() => hero.CanDreamNail(), "native grounded dream nail available", 8);
        yield return ResourceBarrier("dream-positioned");
        yield return new WaitForSecondsRealtime(.4f);
        var targetId = runtime.Peers.Keys.Single();
        ResourceCheck(runtime.CanDreamNail(targetId) && !runtime.CanDreamNail(runtime.LocalId), "only actual live teammate is eligible");
        var peer = runtime.Peers.Values.Single();
        var life = peer.Life; peer.Life = Life.Down;
        ResourceCheck(!runtime.CanDreamNail(targetId), "downed teammate cannot grant dream soul"); peer.Life = life;
        var seen = peer.Seen; peer.Seen = CoopRuntime.Now - 3;
        ResourceCheck(!runtime.CanDreamNail(targetId), "stale teammate cannot grant dream soul"); peer.Seen = seen;
        var scene = peer.State.Scene; peer.State.Scene = "Town";
        ResourceCheck(!runtime.CanDreamNail(targetId), "other-scene teammate cannot grant dream soul"); peer.State.Scene = scene;
        yield return DreamPair("host");
        yield return DreamPair("guest");
        yield return ResourceBarrier("dream-complete");
    }
    private IEnumerator DreamPair(string caster) {
        var pd = PlayerData.instance;
        if (role == caster) {
            pd.SetInt("MPCharge", 0);
            if (host) HeroController.instance.FaceLeft(); else HeroController.instance.FaceRight();
            yield return DreamSwing();
            ResourceCheck(pd.GetInt("MPCharge") == 0, "dream swing facing away grants no soul");
            if (host) HeroController.instance.FaceRight(); else HeroController.instance.FaceLeft();
            yield return DreamSwing();
            Mark("DREAM SOUL first=" + pd.GetInt("MPCharge"));
            ResourceCheck(pd.GetInt("MPCharge") == 16, "one real dream swing grants exactly 16 soul");
            pd.SetBool("equippedCharm_30", true);
            yield return DreamSwing();
            ResourceCheck(pd.GetInt("MPCharge") == 33, "second real dream swing totals 33 even with Dream Wielder");
            ResourceCheck(pd.GetInt("health") == 5, "dream nail does not harm teammate");
            pd.SetInt("health", 4);
            var focus = InputHandler.Instance.inputActions.cast;
            var frame = Time.frameCount + 1;
            using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
                new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, focus) ? Time.frameCount == frame : orig(self))))
            using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
                new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, focus) ? Time.frameCount >= frame : orig(self))))
                yield return Until(() => pd.GetInt("health") == 5, "two dream hits fund one real native focus heal", 5);
            ResourceCheck(pd.GetInt("MPCharge") == 0, "native focus consumes exactly the 33 soul awarded");
            yield return Until(() => HeroController.instance.CanDreamNail(), "native focus releases control", 5);
            pd.SetInt("MPCharge", pd.GetInt("maxMP"));
            yield return DreamSwing();
            ResourceCheck(pd.GetInt("MPCharge") == pd.GetInt("maxMP") && pd.GetInt("MPReserve") == 0,
                "dream soul follows native full soul cap");
            Flag(caster + ".dream-pair");
        } else {
            var before = pd.GetInt("MPCharge");
            yield return Until(() => Has(caster + ".dream-pair"), "other player completes real dream and focus checks", 18);
            ResourceCheck(pd.GetInt("MPCharge") == before && pd.GetInt("health") == 5,
                "remote dream animation gives recipient no soul or damage");
        }
        yield return ResourceBarrier("dream-pair-" + caster);
    }
    private IEnumerator DreamSwing() {
        var hero = HeroController.instance;
        var fsm = hero.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Nail");
        var box = hero.transform.Find("Dream Effects/Hitbox").gameObject;
        yield return Until(() => fsm.ActiveStateName == "Inactive" && hero.CanDreamNail(), "native dream ready", 5);
        var action = InputHandler.Instance.inputActions.dreamNail;
        var frame = Time.frameCount + 1;
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("WasPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount == frame : orig(self))))
        using (new Hook(typeof(InControl.OneAxisInputControl).GetProperty("IsPressed").GetGetMethod(),
            new Func<Func<InControl.OneAxisInputControl, bool>, InControl.OneAxisInputControl, bool>((orig, self) => ReferenceEquals(self, action) ? Time.frameCount >= frame : orig(self)))) {
            yield return Until(() => box.activeInHierarchy, "native input charges and activates dream hitbox", 5);
            var target = runtime.PlayerObject(runtime.Peers.Keys.Single());
            Mark("DREAM BOUNDS hero=" + hero.transform.position + " box=" + box.GetComponent<Collider2D>().bounds +
                " peer=" + target.transform.position + " targetBounds=" + target.GetComponent<Collider2D>().bounds +
                " layersIgnored=" + Physics2D.GetIgnoreLayerCollision(17, 9));
            yield return new WaitForSecondsRealtime(.1f);
        }
        yield return Until(() => !box.activeInHierarchy && fsm.ActiveStateName == "Inactive", "native dream swing fully completes", 5);
        if (Has(role + ".failed")) throw new Exception("Native dream swing failed state=" + fsm.ActiveStateName);
    }
}
