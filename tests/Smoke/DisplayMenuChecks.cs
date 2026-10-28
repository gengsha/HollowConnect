using System;
using System.Collections;
using System.Linq;
using Hkmp.Game.Client;
using Hkmp.Util;
using HollowConnect;
using Modding;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HollowConnect.Smoke;

public sealed partial class BossProbe {
    private bool DisplayMenuCheck => Environment.GetEnvironmentVariable("HOLLOWCONNECT_DISPLAY_MENU_CHECK") == "1";
    private IMenuMod.MenuEntry DisplayOption(string name) => CoopMod.Instance.GetMenuData(null).Single(e => e.Name == name);

    private void ConfigureDisplayMenuChecks() {
        CoopMod.Settings = new Settings { ShareProgress = false };
        var defaults = JsonConvert.DeserializeObject<Settings>("{}");
        Check(defaults.OtherPlayerOpacity == 1 && defaults.ColoredPlayerNames && !defaults.BossHealthScaling,
            "missing JSON settings default to opaque, colored names and no boss scaling");
        CoopMod.Settings.OtherPlayerOpacity = .45f;
        var custom = DisplayOption("队友透明度（本机）");
        Check(custom.Values[custom.Loader()].Contains("45") && CoopMod.Settings.OtherPlayerOpacity == .45f,
            "opening menu preserves and accurately displays custom JSON opacity");
        custom.Saver(0);
        var opacity = DisplayOption("队友透明度（本机）");
        var names = DisplayOption("队友名字配色（本机）");
        var boss = DisplayOption("动态 Boss 血量（房主）");
        Check(opacity.Loader() == 0 && names.Loader() == 1 && boss.Loader() == 0,
            "all three visible controls expose requested defaults");
        Check(BossHealthParameterRow.TrySave("80") && Math.Abs(CoopMod.Settings.BossExtraHealthPerPlayer - .8f) < .00001,
            "typed 80 percent saves one health parameter");
        foreach (var invalid in new[] { "-1", "401", "NaN", "Infinity", "abc" })
            Check(!BossHealthParameterRow.TrySave(invalid) && Math.Abs(CoopMod.Settings.BossExtraHealthPerPlayer - .8f) < .00001,
                "invalid health input preserves current value " + invalid);
        Check(BossHealthParameterRow.TrySave("0") && BossHealthParameterRow.TrySave("400"), "health parameter input supports both limits");
        BossHealthParameterRow.TrySave(host ? "80" : "200");
        var roundtrip = JsonConvert.DeserializeObject<Settings>(JsonConvert.SerializeObject(CoopMod.Instance.OnSaveGlobal()));
        Check(Math.Abs(roundtrip.BossExtraHealthPerPlayer - (host ? .8f : 2)) < .00001, "single health parameter survives JSON roundtrip");
        boss.Saver(host ? 1 : 0);
    }

    private IEnumerator DisplayMenuChecks() {
        yield return Until(() => runtime.Peers.Count == 1, "display menu roster ready");
        var peerId = runtime.Peers.Single().Key;
        var players = Field<PlayerManager>(engine.ClientManager, "_playerManager");
        yield return Until(() => players.GetPlayerObject(peerId) != null, "opaque remote player created before menu change");
        var remote = players.GetPlayerObject(peerId);
        var observation = remote.AddComponent<OpacityObservation>();
        var text = remote.transform.parent.GetComponentInChildren<TextMeshPro>(true);
        var ownName = HeroController.instance.GetComponentInChildren<TextMeshPro>(true);
        var heroSprite = HeroController.instance.GetComponent<tk2dBaseSprite>();
        yield return new WaitForSecondsRealtime(.1f);
        bool Colored() => text != null && Vector4.Distance(text.color, MapService.PeerColor(peerId)) < .001f;
        Check(Math.Abs(observation.Alpha - 1) < .001f && Colored(), "default opaque body and colored remote name");
        Check(!remote.GetComponent<Hkmp.Animation.RemoteVisualOpacity>().enabled,
            "disabled opacity has no per-frame tint updates");
        yield return Until(() => runtime.CurrentBossHealthMultiplier() > 1.5, "host enabled boss rule reaches both clients");
        Check(Math.Abs(runtime.CurrentBossHealthMultiplier() - 1.8) < .00001,
            "host menu rule overrides guest disabled setting");
        var parameterMenu = CoopMod.Instance.GetMenuScreen(null, null);
        parameterMenu.gameObject.SetActive(true);
        yield return null;
        var input = parameterMenu.GetComponentInChildren<BossHealthParameterRow>(true).GetComponentInChildren<InputField>(true);
        Check(input.readOnly && input.text == "80", "connected menu displays host parameter read-only on both clients");
        var savedParameter = CoopMod.Settings.BossExtraHealthPerPlayer;
        input.onEndEdit.Invoke("25");
        Check(input.text == "80" && CoopMod.Settings.BossExtraHealthPerPlayer == savedParameter,
            "connected input callback cannot edit local or server health parameter");
        UnityEngine.Object.Destroy(parameterMenu.gameObject);
        Flag(role + ".display-initial");
        yield return Until(() => Has((host ? "guest" : "host") + ".display-initial"), "both display defaults observed");
        var opacity = DisplayOption("队友透明度（本机）");
        var names = DisplayOption("队友名字配色（本机）");
        if (host) {
            opacity.Saver(2); names.Saver(0);
            Flag("host.display-changed");
            yield return Until(() => Has("guest.display-independent"), "guest verifies independent local settings");
        } else {
            yield return Until(() => Has("host.display-changed"), "host changed only local display");
            yield return new WaitForSecondsRealtime(.1f);
            Check(Math.Abs(observation.Alpha - 1) < .001f && Colored() && CoopMod.Settings.OtherPlayerOpacity == 1,
                "host transparency and name switches do not change guest display");
            Flag("guest.display-independent");
            opacity.Saver(3); names.Saver(0);
        }
        // Room difficulty stays the captured host rule until the next room/server session.
        DisplayOption("动态 Boss 血量（房主）").Saver(host ? 0 : 1);
        yield return new WaitForSecondsRealtime(.15f);
        Check(Math.Abs(observation.Alpha - (host ? .5f : .25f)) < .001f && text.color == Color.white,
            "live menu changes existing remote body and name locally");
        Check(Math.Abs(runtime.CurrentBossHealthMultiplier() - 1.8) < .00001,
            "menu edits cannot silently change active room difficulty");
        Check(Math.Abs(heroSprite.color.a - 1) < .001f && ownName != null && ownName.color == Color.white,
            "local hero and own white name stay unchanged");

        var spell = new Hkmp.Animation.Effects.ShadeSoul();
        HeroController.instance.spellControl.GetFirstAction<HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool>("Fireball 2")
            .gameObject.Value.LocateMyFSM("Fireball Cast").Fsm.InitData();
        spell.SetServerSettings(new Hkmp.Game.Settings.ServerSettings());
        spell.Play(remote, new[] { false, false, false });
        yield return new WaitForSecondsRealtime(.1f);
        var fireball = UnityEngine.Object.FindObjectOfType<Hkmp.Animation.Effects.Fireball>();
        var spellObservation = fireball == null ? null : fireball.gameObject.AddComponent<OpacityObservation>();
        opacity.Saver(4);
        yield return new WaitForSecondsRealtime(.1f);
        Check(Math.Abs(observation.Alpha) < .001f && spellObservation != null && Math.Abs(spellObservation.Alpha) < .001f,
            "fully transparent setting includes existing detached spell");
        opacity.Saver(0); names.Saver(1);
        yield return new WaitForSecondsRealtime(.1f);
        Check(Math.Abs(observation.Alpha - 1) < .001f && Colored() &&
            spellObservation != null && Math.Abs(spellObservation.Alpha - 1) < .001f,
            "turning opacity off restores existing body and detached spell and re-enables name colors");
        Check(!remote.GetComponent<Hkmp.Animation.RemoteVisualOpacity>().enabled,
            "restoring opaque disables tint updates again");
        var roundtrip = JsonConvert.DeserializeObject<Settings>(JsonConvert.SerializeObject(CoopMod.Instance.OnSaveGlobal()));
        Check(roundtrip.OtherPlayerOpacity == 1 && roundtrip.ColoredPlayerNames && roundtrip.BossHealthScaling == host,
            "menu choices survive settings serialization without writing personal configuration");
        Flag(role + ".display-complete");
        yield return Until(() => Has((host ? "guest" : "host") + ".display-complete"), "both local display checks complete");
    }
}
