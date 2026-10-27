using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Hkmp.Animation;
using Hkmp.Game.Client;
using Hkmp.Util;
using HollowConnect;
using HollowConnect.Core;
using Modding;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class BossProbe {
    private bool BalanceCheck => Environment.GetEnvironmentVariable("HOLLOWCONNECT_BOSS_BALANCE_CHECK") == "1";
    private IEnumerator BalanceVisualChecks() {
        yield return Until(() => runtime.Peers.Count == 1, "balance roster ready");
        var players = Field<PlayerManager>(engine.ClientManager, "_playerManager");
        var peerId = runtime.Peers.Single().Key;
        yield return Until(() => players.GetPlayerObject(peerId) != null, "remote body available");
        var remote = players.GetPlayerObject(peerId);
        yield return new WaitForSecondsRealtime(.1f);
        var alpha = host ? .45f : .7f;
        var observation = remote.AddComponent<OpacityObservation>();
        yield return new WaitForSecondsRealtime(.1f);
        Check(Math.Abs(observation.Alpha - alpha) < .001f, "remote body opacity from local config");
        var heroSprite = HeroController.instance.GetComponent<tk2dBaseSprite>();
        Check(Math.Abs(heroSprite.color.a - 1) < .001f, "own body stays opaque");
        var text = remote.transform.parent.GetComponentInChildren<TextMeshPro>(true);
        Check(text != null && Vector4.Distance(text.color, MapService.PeerColor(peerId)) < .001f, "remote name matches direction/map colour");
        var ownName = HeroController.instance.GetComponentInChildren<TextMeshPro>(true);
        Check(ownName != null && ownName.color == Color.white, "own name remains white");
        new Hkmp.Animation.Effects.MonarchWings().Play(remote, null);
        var shadeSoul = new Hkmp.Animation.Effects.ShadeSoul();
        HeroController.instance.spellControl.GetFirstAction<HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool>("Fireball 2")
            .gameObject.Value.LocateMyFSM("Fireball Cast").Fsm.InitData();
        shadeSoul.SetServerSettings(new Hkmp.Game.Settings.ServerSettings());
        shadeSoul.Play(remote, new[] { false, false, false });
        yield return new WaitForSecondsRealtime(.15f);
        var fireball = Object.FindObjectOfType<Hkmp.Animation.Effects.Fireball>();
        var spellObservation = fireball == null ? null : fireball.gameObject.AddComponent<OpacityObservation>();
        yield return null;
        Check(fireball != null && fireball.GetComponent<RemoteVisualOpacity>() != null && spellObservation != null &&
            Math.Abs(spellObservation.Alpha - alpha) < .001f,
            "detached remote Shade Soul follows opacity");
        var feather = remote.GetComponentsInChildren<ParticleSystem>(true).FirstOrDefault(p => p.name.Contains("Feather"));
        if (feather != null) Check(feather.GetComponent<RemoteVisualOpacity>() != null, "remote feather particles carry opacity");
        yield return new WaitForSecondsRealtime(.2f);
        Check(Math.Abs(observation.Alpha - alpha) < .001f, "opacity remains stable across frames");
        var menu = Field<Hkmp.Menu.ModMenu>(ModHooks.GetMod("HKMP"), "_modMenu");
        var option = Field<MenuOptionHorizontal>(menu, "_skinHorizontalOption");
        if (option == null) {
            menu.CreateMenu(null);
            option = Field<MenuOptionHorizontal>(menu, "_skinHorizontalOption");
        }
        typeof(Hkmp.Menu.ModMenu).GetMethod("ModifySkinHorizontalOption", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(menu, new object[] { true });
        // Use real pointer handling on the actual constructed selector; no
        // frame/click wait, so this check does not send a skin-change packet.
        option.SetOptionTo(2);
        var menuScreen = Field<MenuScreen>(menu, "_clientSettingsMenu");
        menuScreen.gameObject.SetActive(true);
        menuScreen.transform.localScale = Vector3.one;
        var rect = (RectTransform)option.transform;
        var canvas = option.GetComponentInParent<Canvas>();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        void Click(float x) {
            var point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(new Vector3(x, rect.rect.center.y)));
            Mark("SKIN pointer=" + point + " rect=" + rect.rect + " scale=" + rect.lossyScale + " active=" + option.gameObject.activeInHierarchy + " interact=" + option.interactable + " before=" + option.selectedOptionIndex);
            option.OnPointerClick(new PointerEventData(EventSystem.current) { position = point,
                pointerPressRaycast = new RaycastResult { module = canvas == null ? null : canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() } });
            Mark("SKIN after=" + option.selectedOptionIndex);
        }
        Click(rect.rect.xMin + 5);
        Check(option.selectedOptionIndex == 1, "skin left mouse click decrements 2 to 1");
        Click(rect.rect.xMin + 5);
        Check(option.selectedOptionIndex == 0, "skin left mouse click returns to default 0");
        Click(rect.rect.xMax - 5);
        Check(option.selectedOptionIndex == 1, "skin right mouse click increments");
        option.SetOptionTo(0);
        menuScreen.gameObject.SetActive(false);
        Flag(role + ".balance-visuals");
        yield return Until(() => Has((host ? "guest" : "host") + ".balance-visuals"), "both local presentation checks complete");
    }

    private IEnumerator BalanceDamageChecks(HealthManager health, double multiplier) {
        Check(Math.Abs(runtime.CurrentBossHealthMultiplier() - multiplier) < .00001, "host difficulty config overrides guest config");
        var takeDamage = typeof(HealthManager).GetMethod("TakeDamage", BindingFlags.Instance | BindingFlags.NonPublic);
        var before = health.hp;
        void Hit(AttackTypes type, int damage, float mod = 1) => takeDamage.Invoke(health, new object[] {
            new HitInstance { Source = HeroController.instance.gameObject, AttackType = type, DamageDealt = damage, Multiplier = mod, IgnoreInvulnerable = true }
        });
        // Exercise the accepted native hit path after its own multipliers.
        Hit(AttackTypes.Nail, 16);
        Check(health.hp == before - (multiplier > 1 ? 10 : 16), "native nail damage scaled exactly once");
        before = health.hp;
        Hit(AttackTypes.Spell, 8, 2);
        Check(health.hp == before - (multiplier > 1 ? 10 : 16), "native spell multiplier precedes boss scaling");
        var extra = health.GetComponent<ExtraDamageable>();
        Check(extra != null, "native boss has extra-damage receiver");
        before = health.hp;
        var applyExtra = typeof(ExtraDamageable).GetMethod("ApplyExtraDamageToHealthManager", BindingFlags.Instance | BindingFlags.NonPublic);
        applyExtra.Invoke(extra, new object[] { 8 });
        Check(health.hp == before - (multiplier > 1 ? 5 : 8), "native player extra damage shares boss scaling");
        before = health.hp; health.ApplyExtraDamage(1);
        Check(health.hp == before - 1, "direct scripted extra damage remains native");
        if (multiplier > 1) {
            var other = runtime.Peers.Single();
            var previousScene = other.Value.State.Scene;
            other.Value.State.Scene = "Town";
            Check(runtime.CurrentBossHealthMultiplier() == 1 && health.hp == before - 1, "leaving room restores solo difficulty without healing");
            other.Value.State.Scene = previousScene;
            var oldLife = other.Value.Life; other.Value.Life = Life.Down;
            Check(Math.Abs(runtime.CurrentBossHealthMultiplier() - 1.6) < .00001, "downed teammate still counts in encounter");
            other.Value.Life = Life.Loading;
            Check(Math.Abs(runtime.CurrentBossHealthMultiplier() - 1.6) < .00001, "native cutscene and transition readiness cannot lower boss difficulty");
            other.Value.Life = oldLife;
            runtime.Peers[65000] = new Peer { State = new Message { Scene = Hkmp.CoopContext.LocalScene }, Seen = CoopRuntime.Now, Life = Life.Alive };
            Check(Math.Abs(runtime.CurrentBossHealthMultiplier() - 1.96) < .00001, "third participant roster gives 196 percent");
            runtime.Peers.Remove(65000);
        }
        yield return null;
    }
}

[DefaultExecutionOrder(10000)]
internal sealed class OpacityObservation : MonoBehaviour {
    internal float Alpha;
    private void LateUpdate() => Alpha = GetComponent<tk2dBaseSprite>().color.a;
}
