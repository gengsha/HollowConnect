using System;
using System.Collections;
using System.Linq;
using Hkmp.Animation;
using Hkmp.Animation.Effects;
using Hkmp.Game.Client;
using Hkmp.Util;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HollowConnect.Smoke;

public sealed partial class ProgressProbe {
    private IEnumerator RemoteFeedbackChecks() {
        yield return Until(() => runtime.Peers.Count == 1 && VisiblePeer(), "remote feedback peer visible");
        var remote = Field<PlayerManager>(engine.ClientManager, "_playerManager").GetPlayerObject(runtime.Peers.Single().Key);
        var hero = HeroController.instance;
        var vibrationCalls = 0;
        var recoilCalls = 0;
        On.VibrationManager.hook_PlayVibrationClipOneShot vibration = (orig, data, target, looping, tag) => {
            vibrationCalls++; return null;
        };
        On.VibrationManager.PlayVibrationClipOneShot += vibration;
        On.HeroController.hook_RecoilLeft recoilLeft = (orig, self) => { recoilCalls++; };
        On.HeroController.hook_RecoilRight recoilRight = (orig, self) => { recoilCalls++; };
        On.HeroController.RecoilLeft += recoilLeft;
        On.HeroController.RecoilRight += recoilRight;
        var source = new GameObject("Feedback fixture"); source.SetActive(false);
        var clone = (GameObject)null;
        var contact = new GameObject("Feedback collision fixture") { layer = 11 };
        var nativeSlash = (GameObject)null;
        try {
            source.AddComponent<VibrationEffect>();
            source.AddComponent<VibrationPlayer>().PlayAutomatically = true;
            source.SetActive(true);
            ResourceCheck(vibrationCalls == 2, "native local component vibrations still run");
            vibrationCalls = 0;
            clone = RemotePlayerVisuals.Instantiate(source);
            ResourceCheck(vibrationCalls == 0, "active remote clone cannot vibrate before marker assignment");
            clone.SetActive(false); clone.SetActive(true);
            clone.GetComponent<VibrationPlayer>().Play();
            ResourceCheck(vibrationCalls == 0, "remote reactivation and explicit vibration stay isolated");
            source.GetComponent<VibrationPlayer>().Play();
            ResourceCheck(vibrationCalls == 1, "local vibration remains enabled after remote creation");

            var slashEffect = new Slash(); slashEffect.SetServerSettings(new Hkmp.Game.Settings.ServerSettings());
            slashEffect.Play(remote, new bool[6]);
            var slash = remote.GetComponentsInChildren<Collider2D>(true).First(c => c.CompareTag("Nail Attack") && c.gameObject.LocateMyFSM("damages_enemy") != null);
            // Keep a native NailSlash alive through this same-frame contact:
            // SlashBase's deferred Destroy has not completed at this point.
            nativeSlash = RemotePlayerVisuals.Instantiate(hero.slashPrefab, remote.transform.Find("Attacks"));
            nativeSlash.SetActive(true);
            var collider = contact.AddComponent<BoxCollider2D>();
            nativeSlash.SendMessage("OnTriggerEnter2D", collider, SendMessageOptions.RequireReceiver);
            ResourceCheck(recoilCalls == 0, "same-frame remote native nail contact cannot recoil local hero");
            hero.Attack(GlobalEnums.AttackDirection.normal);
            hero.slashPrefab.SendMessage("OnTriggerEnter2D", collider, SendMessageOptions.RequireReceiver);
            ResourceCheck(recoilCalls > 0, "native local nail contact still applies recoil");

            // Use a real native TinkEffect callback, including the existing IL
            // isolation, rather than calling the feedback predicate directly.
            var tink = contact.AddComponent<TinkEffect>();
            tink.blockEffect = Hkmp.HkmpMod.PreloadedObjects["GG_Sly"]["Battle Scene/Sly Boss/Cyclone Tink"].GetComponent<TinkEffect>().blockEffect;
            yield return null; // Native Start caches the camera.
            recoilCalls = 0;
            contact.SendMessage("OnTriggerEnter2D", nativeSlash.GetComponent<Collider2D>(), SendMessageOptions.RequireReceiver);
            ResourceCheck(recoilCalls == 0, "remote wall tink does not move local hero");
            yield return new WaitForSecondsRealtime(.3f);
            contact.SendMessage("OnTriggerEnter2D", hero.slashPrefab.GetComponent<Collider2D>(), SendMessageOptions.RequireReceiver);
            ResourceCheck(recoilCalls > 0, "local wall tink keeps native recoil");
            yield return null;
            ResourceCheck(slash == null || slash.GetComponent<NailSlash>() == null, "remote slash removes native local-player recoil component");
        } finally {
            On.VibrationManager.PlayVibrationClipOneShot -= vibration;
            On.HeroController.RecoilLeft -= recoilLeft;
            On.HeroController.RecoilRight -= recoilRight;
            if (nativeSlash != null) Object.Destroy(nativeSlash);
            Object.Destroy(contact);
            if (clone != null) Object.Destroy(clone);
            Object.Destroy(source);
        }
    }
}
