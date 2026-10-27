using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HollowConnect;
using HollowConnect.Core;
using Hkmp.Game.Client;
using Modding;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Scenes = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private bool MenuResourceCase => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESOURCE_MENU"));

    private IEnumerator MenuResourceEntry() {
        yield return Until(() => Has("host.audit.first-ready"), "full host ready before guest menu Connect");
        if (Has(role + ".failed")) yield break;
        if (Environment.GetEnvironmentVariable("HOLLOWCONNECT_RESOURCE_MENU") == "stale") {
            // A previous slot can still own every source while the profile menu
            // is open. The newly selected slot must receive its own awards.
            CoopMod.Statistics = new DeathStatistics();
            foreach (var source in ResourceCatalog.Sources) CoopMod.Statistics.Resources.TryClaim(source.Id, out _);
        }
        var received = new HashSet<string>(StringComparer.Ordinal);
        using (var trace = new Hook(typeof(CoopRuntime).GetMethod("Receive", ResourcePrivate),
            (Action<Action<CoopRuntime, Message>, CoopRuntime, Message>)((orig, target, message) => {
                orig(target, message);
                if (message.Kind == HollowConnect.Kind.ResourceGrant && message.Session == target.Session &&
                    ResourceCatalog.TryGet(message.Key, out _)) received.Add(message.Key);
            }))) {
            typeof(ClientManager).GetField("_autoConnect", ResourcePrivate).SetValue(engine.ClientManager, false);
            typeof(ClientManager).GetMethod("Connect", ResourcePrivate).Invoke(engine.ClientManager,
                new object[] { "127.0.0.1", 29650, "progress" + role });
            yield return Until(() => runtime.Connected && received.Count == 39, "guest receives all 39 resource grants before selecting a save");
            if (Has(role + ".failed")) yield break;
            ResourceCheck(Scenes.GetActiveScene().name == "Menu_Title" && HeroController.instance == null,
                "resource grants arrived while native save-selection menu is still open");
            Flag("guest.menu.all-grants");
            remoteResourceUiStarts = 0;
            On.HutongGames.PlayMaker.Fsm.EnterState += ObserveRemoteResourceUi;
            GameManager.instance.profileID = 99;
            GameManager.instance.StartNewGame(false, false);
            var joinUi = Field<Hkmp.Ui.UiManager>(engine.ClientManager, "_uiManager");
            yield return Until(() => Field<MenuScreen>(joinUi, "_joinSaveMenu") != null &&
                !Field<bool>(joinUi, "_joinMenuBusy"), "shared-progress menu entry waits for confirmation");
            if (Has(role + ".failed")) yield break;
            var joinMenu = Field<MenuScreen>(joinUi, "_joinSaveMenu");
            var confirm = joinMenu.GetComponentsInChildren<UnityEngine.UI.MenuButton>(true).Single(b => b.name == "Confirm join");
            UnityEngine.EventSystems.ExecuteEvents.Execute(confirm.gameObject,
                new UnityEngine.EventSystems.BaseEventData(UnityEngine.EventSystems.EventSystem.current),
                UnityEngine.EventSystems.ExecuteEvents.submitHandler);
            yield return Until(() => Scenes.GetActiveScene().name == "Opening_Sequence", "native new slot reaches opening sequence");
            if (Has(role + ".failed")) yield break;
            StartCoroutine(SkipMenuResourceOpening());
            yield return Until(() => Scenes.GetActiveScene().name == "Tutorial_01" && HeroController.instance != null &&
                HeroController.instance.isHeroInPosition && HeroController.instance.acceptingInput &&
                !HeroController.instance.controlReqlinquished && !GameManager.instance.IsInSceneTransition,
                "native new slot reaches playable Tutorial", 60);
            if (Has(role + ".failed")) yield break;
            Freeze();
            var pd = PlayerData.instance;
            Mark("MENU RESOURCE loaded totals receipts=" + LocalResourceClaims.Claimed.Count +
                " slots=" + pd.GetInt("charmSlots") + " masks=" + pd.GetInt("maxHealthBase") +
                " reserve=" + pd.GetInt("MPReserveMax") + " ore=" + pd.GetInt("ore"));
        }
    }
    private IEnumerator SkipMenuResourceOpening() {
        while (Scenes.GetActiveScene().name == "Opening_Sequence") {
            GameManager.instance.SkipCutscene();
            yield return new WaitForSecondsRealtime(3);
        }
    }
}
