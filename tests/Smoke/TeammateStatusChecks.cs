using System;
using System.Collections;
using System.IO;
using System.Linq;
using HollowConnect.Core;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class RescueProbe {
    private static string StatusPlayerName(bool roomHost) => roomHost ? "小骑士" : "旅人";

    private IEnumerator TeammateStatusChecks() {
        Canvas FindCanvas(string name) => Object.FindObjectsOfType<Canvas>(true).SingleOrDefault(c => c.name == name);
        Canvas PartyHud() => FindCanvas("HollowConnect Teammate Status");
        Canvas DamageHud() => FindCanvas("HollowConnect Damage Ranking");
        bool Visible(Canvas canvas) => canvas != null && canvas.gameObject.activeInHierarchy;
        var menu = CoopMod.Instance.GetMenuData(null).Single(e => e.Name == CoopMod.TeammateStatusOptionName);
        var linked = Environment.GetEnvironmentVariable("HOLLOWCONNECT_OBSERVATION_LINKED") == "1";
        yield return Until(() => runtime.Peers.Values.Any(p => p.State != null), "roster received in normal gameplay");
        Check(global::Language.Language.CurrentLanguage() == global::Language.LanguageCode.ZH, "client language is Simplified Chinese");
        Check(!new Settings().TeammateStatus && !PartyHud(), "teammate HUD is off by default");
        var id = runtime.Peers.Keys.Single();
        // The board receives a representative snapshot so this layout check
        // does not require entering and fighting through a Pantheon.
        var snapshot = new DamageRankingSnapshot { Board = 1, Revision = 1, Sequence = "Boss Sequence Tier 4",
            Players = new[] { new DamageStanding { Id = host ? runtime.LocalId : id, Name = StatusPlayerName(true), Damage = 40 },
                new DamageStanding { Id = host ? id : runtime.LocalId, Name = StatusPlayerName(false), Damage = 30 } } };
        if (linked) {
            Check(runtime.LinkedHealthRuleEnabled && CoopMod.TeammateStatusLocked, "host shared health locks local status setting on both endpoints");
            menu.Saver(1);
            Check(menu.Loader() == 0 && !CoopMod.Settings.TeammateStatus, "menu refuses to enable status during shared health");
            CoopMod.Settings.TeammateStatus = true;
            yield return Until(() => runtime.damageRankingEnabled, "host damage ranking rule is enabled in shared health");
            runtime.damageRanking = snapshot;
            yield return Until(() => Visible(DamageHud()), "shared health can display damage ranking");
            yield return new WaitForSecondsRealtime(.4f);
            Check(!PartyHud(), "runtime blocks status even when a saved preference was enabled");
            yield break;
        }

        var rendered = Environment.GetEnvironmentVariable("HOLLOWCONNECT_RENDERED_TEST") == "1";
        if (rendered) {
            Screen.SetResolution(1280, 720, false);
            yield return Until(() => Screen.width == 1280 && Screen.height == 720, "1280x720 demo window");
        }
        yield return RuleEnter("Crossroads_01");
        var white = host ? 4 : 2;
        var blue = host ? 0 : 2;
        var nativeHud = NativeMaskGraphic.FindHudAnimator();
        Check(nativeHud != null, "native health animator loaded");
        var whiteMask = NativeMaskGraphic.ReadFrame(nativeHud, "Health Idle");
        var blueMask = NativeMaskGraphic.ReadFrame(nativeHud, "Blue Idle");
        Check(whiteMask?.material?.mainTexture != null && blueMask?.material?.mainTexture != null && whiteMask != blueMask,
            "distinct native white and Lifeblood atlas frames: " + whiteMask?.name + ", " + blueMask?.name);

        var player = PlayerData.instance;
        player.health = white;
        var healthHud = nativeHud.transform.parent.parent;
        foreach (var fsm in healthHud.GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f.FsmName == "health_display"))
            fsm.SendEvent("HERO DAMAGED");
        var nativeBlue = healthHud.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Blue Health Control");
        for (var i = 0; i < blue; i++) {
            yield return Until(() => nativeBlue.ActiveStateName == "Idle", "native blue HUD ready");
            nativeBlue.SendEvent("ADD BLUE HEALTH");
            var expected = i + 1;
            yield return Until(() => player.healthBlue == expected, "native HUD grants a blue mask");
        }
        Flag(role + ".hud-ready");
        yield return Until(() => Has((host ? "guest" : "host") + ".hud-ready"), "both set different white and blue health");
        menu.Saver(1);
        Check(menu.Loader() == 1, "settings entry enables teammate HUD");
        yield return Until(() => Visible(PartyHud()) && PartyHud().transform.Find("Party").childCount == 2, "real roster creates status canvas");
        Check(!runtime.damageRankingEnabled && !DamageHud(), "status works with the room damage rule disabled");
        var canvas = PartyHud();
        var panel = (RectTransform)canvas.transform.Find("Party");
        var own = (RectTransform)panel.GetChild(0);
        var remote = (RectTransform)panel.GetChild(1);
        int Masks(Transform row, tk2dSpriteDefinition frame) => row.GetComponentsInChildren<NativeMaskGraphic>()
            .Count(i => i.Frame == frame && i.mainTexture == frame.material.mainTexture);
        yield return Until(() => Masks(own, whiteMask) == white && Masks(own, blueMask) == blue &&
            Masks(remote, whiteMask) == (host ? 2 : 4) && Masks(remote, blueMask) == (host ? 2 : 0) &&
            remote.Find("Ping").GetComponent<Text>().text.EndsWith(" ms", StringComparison.Ordinal),
            "actual white and blue frames match live health and measured RTT over UDP");
        var pingBlue = new Color32(103, 141, 184, 255);
        Check(((Color32)own.Find("Ping").GetComponent<Text>().color).Equals(pingBlue) &&
            ((Color32)remote.Find("Ping").GetComponent<Text>().color).Equals(pingBlue),
            "whole latency label uses muted dark blue on both rows");
        Check(remote.Find("Name").GetComponent<Text>().text == StatusPlayerName(!host), "Chinese peer name rendered");
        var chineseFont = Modding.Menu.MenuResources.NotoSerifCJKSCRegular;
        Check(chineseFont != null && canvas.GetComponentsInChildren<Text>().All(t => t.font == chineseFont),
            "all teammate text uses the game's Chinese font " + chineseFont?.name);
        Check(own.Find("Local player marker").gameObject.activeSelf && !remote.Find("Local player marker").gameObject.activeSelf,
            "only own row has the blue marker");
        Check(canvas.GetComponentsInChildren<Graphic>().All(g => !g.raycastTarget), "status HUD does not intercept input");
        if (rendered) {
            // Let the original masks finish animating and join notifications fade
            // before capturing both copies' normal gameplay HUDs.
            yield return Until(() => healthHud.GetComponentsInChildren<PlayMakerFSM>(true).Count(f =>
                f.FsmName == "health_display" && f.ActiveStateName == "Idle" &&
                f.transform.Find("Idle").GetComponent<Renderer>().enabled) == white, "native HUD shows the same white mask count");
            yield return new WaitForSecondsRealtime(5);
            yield return CaptureStatusHud("仅队友状态");
        }

        runtime.damageRankingEnabled = true;
        runtime.damageRanking = snapshot;
        yield return Until(() => Visible(DamageHud()), "damage board visible alongside teammate status");
        var damage = (RectTransform)DamageHud().transform.Find("Leaderboard");
        Check(damage.Find("Title").GetComponent<Text>().text == "伤害占比" && damage.Find("Title").GetComponent<Text>().font == chineseFont,
            "damage board title uses Simplified Chinese and native font");
        var boardPosition = damage.anchoredPosition;
        var rowPosition = own.anchoredPosition;
        if (rendered) yield return CaptureStatusHud("队友状态和伤害占比");
        yield return ObservationKey(KeyCode.F1);
        yield return Until(() => !Visible(DamageHud()), "F1 hides the damage board");
        Check(Visible(canvas) && own.anchoredPosition == rowPosition, "hiding the board leaves the status view in place");
        yield return ObservationKey(KeyCode.F1);
        menu.Saver(0);
        yield return Until(() => Visible(DamageHud()) && !Visible(canvas), "only damage ranking is displayed when status is turned off");
        Check(damage.anchoredPosition == boardPosition, "switching status off leaves the board in place");
        if (rendered) yield return CaptureStatusHud("仅伤害占比");
        menu.Saver(1);
        yield return Until(() => Visible(canvas), "status can be switched back on");

        yield return TeammateStatusDeathCheck(host ? own : remote, whiteMask, chineseFont, id, rendered);

        Flag(role + ".hud-captured");
        yield return Until(() => Has((host ? "guest" : "host") + ".hud-captured"), "both verified the HUDs");
        if (host) {
            yield return Until(() => !runtime.Peers.ContainsKey(id) && remote.GetComponent<CanvasGroup>().alpha < .5f &&
                remote.Find("Ping").GetComponent<Text>().text == "—", "departed remote dims and loses its latency");
            yield return Until(() => !Visible(canvas), "departed row expires when alone");
            Check(Visible(DamageHud()) && damage.anchoredPosition == boardPosition, "departing teammate does not move the board");
        } else {
            engine.ClientManager.Disconnect();
            yield return Until(() => !runtime.Connected && !PartyHud(), "disconnect disposes local teammate HUD");
        }
    }

    private IEnumerator TeammateStatusDeathCheck(RectTransform victim, tk2dSpriteDefinition whiteMask, Font font,
        ushort peerId, bool rendered) {
        IEnumerator BothObserved(string step) {
            Flag(role + "." + step);
            return Until(() => Has((host ? "guest" : "host") + "." + step), step);
        }
        bool OneWhiteMask() {
            var masks = victim.GetComponentsInChildren<NativeMaskGraphic>();
            return masks.Length == 1 && masks[0].Frame == whiteMask;
        }
        var badge = victim.Find("Down status");
        var label = badge.Find("Label").GetComponent<Text>();
        Check(!badge.gameObject.activeSelf, "living player has no down badge");
        var position = victim.anchoredPosition;
        var hero = HeroController.instance;
        Freeze();
        yield return BothObserved("status.death-ready");
        if (host) {
            PlayerData.instance.health = 1;
            hero.takeNoDamage = false;
            hero.TakeDamage(null, GlobalEnums.CollisionSide.left, 1, 1);
        }
        yield return Until(() => (host ? runtime.Down : runtime.Peers[peerId].Life == Life.Down) &&
            badge.gameObject.activeInHierarchy, "native lethal hit shows the down badge locally and over UDP");
        Check(label.text == "暗影" && label.font == font && label.preferredWidth <= label.rectTransform.rect.width &&
            victim.anchoredPosition == position && victim.GetComponentsInChildren<NativeMaskGraphic>().Length == 0,
            "Chinese down label fits in the victim's health slot and replaces masks");
        var shades = Field<ShadeService>(runtime, "shades");
        var victimId = host ? runtime.LocalId : peerId;
        yield return Until(() => shades.Get(victimId) != null, "native Hollow Shade visual appears in the room");
        var animator = shades.Get(victimId).GetComponentInChildren<tk2dSpriteAnimator>();
        var shadeFrame = NativeMaskGraphic.ReadFrame(animator, "Idle");
        Check(animator.CurrentClip.name == "Idle" && shadeFrame?.material?.mainTexture != null,
            "world shade uses native Idle frame " + shadeFrame?.name + " in atlas " + shadeFrame?.material?.mainTexture?.name);
        if (rendered) {
            yield return new WaitForSecondsRealtime(3);
            yield return CaptureStatusHud("队友倒地");
        }
        yield return BothObserved("status.death-observed");
        if (!host) {
            var peer = runtime.Peers[peerId];
            Check(runtime.RequestShadeHit(peerId, peer.DownSerial), "send shade rescue over UDP");
        }
        yield return Until(() => (host ? !runtime.Down : runtime.Peers[peerId].Life == Life.Alive) &&
            !badge.gameObject.activeSelf && OneWhiteMask(), "accepted rescue clears badge and restores one native mask");
        Check(victim.anchoredPosition == position, "rescue leaves status row in place");
        if (rendered) yield return CaptureStatusHud("队友复活");
    }

    private IEnumerator CaptureStatusHud(string label) {
        yield return new WaitForEndOfFrame();
        var pixels = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        try {
            pixels.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
            pixels.Apply();
            File.WriteAllBytes(Path.Combine(root, role + "-" + label + ".png"), pixels.EncodeToPNG());
        } finally { Destroy(pixels); }
    }
}
