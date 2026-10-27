using System;
using System.Linq;
using HollowConnect.Core;

internal static class DebugDiagnosticsTests {
    internal static void Register(Action<string, Action> test) {
        test("debug event ring is bounded and keeps chronological tail", () => {
            var ring = new DebugEventRing(3);
            for (var i = 0; i < 5; i++) ring.Add(i, DebugEventSeverity.Info, "event-" + i);
            var events = ring.Snapshot();
            if (ring.Count != 3 || events.Count != 3 || events[0].Code != "event-2" || events[2].Code != "event-4")
                throw new Exception("ring did not retain its bounded chronological tail");
        });
        test("save safety risk model catches a stale transient lock", () => {
            var snapshot = new SaveSafetySnapshot {
                SceneName = "Dream_04_White_Defender", HeroControlRelinquished = true,
                HeroAcceptingInput = false, HeroBoxInactive = true, BodySimulated = false
            };
            var risks = snapshot.RiskCodes();
            if (!risks.Contains("hero-control-lock") || !risks.Contains("hero-box-left-inactive") ||
                !risks.Contains("hero-rigidbody-disabled")) throw new Exception("stale lock was not diagnosed");
        });
        test("offline pending boss context is treated as a save safety risk", () => {
            var snapshot = new SaveSafetySnapshot { Connected = false, HasBossContextGuard = true };
            if (!snapshot.RiskCodes().Contains("boss-context-pending-offline") || !snapshot.HasTransientLockRisk)
                throw new Exception("offline boss context guard was not diagnosed");
        });
        test("finished native load cannot hide a hero still exiting without input", () => {
            var snapshot = new SaveSafetySnapshot { HasFinishedEnteringScene = true, HeroTransitioning = true,
                TransitionState = "EXITING_SCENE", BodySimulated = true };
            if (!snapshot.HasTransientLockRisk || !snapshot.RiskCodes().Contains("hero-exit-without-native-load"))
                throw new Exception("reported ending-return stall was not diagnosed");
            snapshot.IsInSceneTransition = true;
            if (snapshot.RiskCodes().Contains("hero-exit-without-native-load")) throw new Exception("normal loading was diagnosed as a stale exit");
            snapshot.IsInSceneTransition = false; snapshot.HeroHazardRespawning = true;
            if (snapshot.RiskCodes().Contains("hero-exit-without-native-load")) throw new Exception("native hazard recovery was diagnosed as a stale exit");
        });
        test("diagnostic export is a state summary without identity or save dump", () => {
            var snapshot = new SaveSafetySnapshot { SceneName = "Boss", RespawnScene = "Town", RespawnMarkerName = "Bench" };
            var ring = new DebugEventRing();
            ring.Add(1, DebugEventSeverity.Warning, "disconnect", "Boss", "player=Alice ip=192.168.1.42 token=123456");
            var json = SafeDiagnosticsFormatter.Format(snapshot, ring.Snapshot(), "2026-10-17T00:00:00Z");
            if (!json.Contains("\"scene\":\"Boss\"") || json.Contains("Alice") || json.Contains("192.168.1.42") ||
                json.Contains("PlayerData") || json.Contains("token=123456")) throw new Exception("diagnostic leaked private data");
        });
    }
}
