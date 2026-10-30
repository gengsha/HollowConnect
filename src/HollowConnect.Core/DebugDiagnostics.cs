using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace HollowConnect.Core;

public enum DebugEventSeverity {
    Info,
    Warning,
    Critical
}

public sealed class DebugEvent {
    public long Sequence { get; internal set; }
    public double Time { get; internal set; }
    public DebugEventSeverity Severity { get; internal set; }
    public string Code { get; internal set; }
    public string Scene { get; internal set; }
    public string Detail { get; internal set; }
}

// A bounded, allocation-light history for events that are useful after a
// scene lock or disconnect. It intentionally stores only diagnostic labels,
// never packets, player names, addresses, or PlayerData contents.
public sealed class DebugEventRing {
    private readonly DebugEvent[] events;
    private int next;
    private int count;
    private long sequence;

    public DebugEventRing(int capacity = 128) {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        events = new DebugEvent[capacity];
    }

    public int Count => count;
    public int Capacity => events.Length;

    public void Add(double time, DebugEventSeverity severity, string code, string scene = "", string detail = "") {
        if (string.IsNullOrEmpty(code)) return;
        events[next] = new DebugEvent {
            Sequence = ++sequence,
            Time = time,
            Severity = severity,
            Code = Limit(code, 96),
            Scene = Limit(scene, 96),
            Detail = Limit(detail, 240)
        };
        next = (next + 1) % events.Length;
        if (count < events.Length) count++;
    }

    public IReadOnlyList<DebugEvent> Snapshot() {
        var result = new List<DebugEvent>(count);
        var first = (next - count + events.Length) % events.Length;
        for (var i = 0; i < count; i++) result.Add(events[(first + i) % events.Length]);
        return result;
    }

    private static string Limit(string value, int max) {
        if (value == null) return "";
        return value.Length <= max ? value : value.Substring(0, max);
    }
}

// This is a deliberately narrow view of runtime state. Keep additions here
// scalar and reviewable: the report must never become a save-file dump.
public sealed class SaveSafetySnapshot {
    public string SceneName = "";
    public string GameManagerScene = "";
    public string EntryGateName = "";
    public string TransitionState = "";
    public string CameraMode = "";
    public string WarpScene = "";
    public string WarpEntry = "";
    public string RetryScene = "";
    public string RespawnScene = "";
    public string RespawnMarkerName = "";
    public string BossReturnEntryGate = "";
    public string BossSequenceName = "";
    public int RespawnType;
    public int PeerCount;
    public int ResourceReceiptCount;
    public int SharedProgressCount;
    public bool Connected;
    public bool SessionPresent;
    public bool IsInSceneTransition;
    public bool HasFinishedEnteringScene;
    public bool HeroInPosition;
    public bool HeroAcceptingInput;
    public bool HeroControlRelinquished;
    public bool HeroDead;
    public bool HeroHazardDeath;
    public bool HeroHazardRespawning;
    public bool HeroTransitioning;
    public bool HeroBoxInactive;
    public bool BodySimulated;
    public bool CameraFrozen;
    public bool InBossSequence;
    public bool ChangingScene;
    public bool Down;
    public bool Resetting;
    public bool Returning;
    public bool VoluntaryObservation;
    public bool HasNativeDeath;
    public bool HasBossContextGuard;
    public bool HasCheckpointReload;
    public bool HasTeammateWarp;

    public bool HasTransientLockRisk =>
        !IsInSceneTransition && (ChangingScene || HasCheckpointReload || HasTeammateWarp) ||
        HasUnexpectedControlLock ||
        HasBossContextGuard && !Connected || HasUnfinishedHeroExit;

    private bool HasUnfinishedHeroExit => !IsInSceneTransition && HasFinishedEnteringScene &&
        !HeroDead && !HeroHazardDeath && !HeroHazardRespawning && !Down && !Resetting && !Returning &&
        !HeroInPosition && !HeroAcceptingInput && HeroTransitioning && TransitionState == "EXITING_SCENE";

    private bool HasUnexpectedControlLock => !IsInSceneTransition && !HeroDead &&
        HeroControlRelinquished && !HeroAcceptingInput && !(Connected && VoluntaryObservation);

    public List<string> RiskCodes() {
        var result = new List<string>();
        if (!IsInSceneTransition && (ChangingScene || HasCheckpointReload || HasTeammateWarp))
            result.Add("transient-transition-without-native-load");
        if (HasUnexpectedControlLock)
            result.Add("hero-control-lock");
        if (HeroBoxInactive && !(Connected && VoluntaryObservation) && !Down && !Resetting && !Returning && !IsInSceneTransition)
            result.Add("hero-box-left-inactive");
        if (BodySimulated == false && !(Connected && VoluntaryObservation) && !Down && !Resetting && !Returning && !IsInSceneTransition)
            result.Add("hero-rigidbody-disabled");
        if (HasBossContextGuard && !Connected) result.Add("boss-context-pending-offline");
        if (HasUnfinishedHeroExit) result.Add("hero-exit-without-native-load");
        return result;
    }
}

public static class SafeDiagnosticsFormatter {
    public static string Format(SaveSafetySnapshot snapshot, IReadOnlyList<DebugEvent> events, string generatedUtc) {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var b = new StringBuilder(4096);
        b.Append("{\"generatedUtc\":").Append(StringValue(generatedUtc));
        Field(b, "scene", snapshot.SceneName); Field(b, "gameManagerScene", snapshot.GameManagerScene);
        Field(b, "entryGate", snapshot.EntryGateName); Field(b, "transitionState", snapshot.TransitionState);
        Field(b, "cameraMode", snapshot.CameraMode); Field(b, "warpScene", snapshot.WarpScene);
        Field(b, "warpEntry", snapshot.WarpEntry); Field(b, "retryScene", snapshot.RetryScene);
        Field(b, "respawnScene", snapshot.RespawnScene); Field(b, "respawnMarker", snapshot.RespawnMarkerName);
        Field(b, "bossReturnEntryGate", snapshot.BossReturnEntryGate); Field(b, "bossSequence", snapshot.BossSequenceName);
        Number(b, "respawnType", snapshot.RespawnType); Number(b, "peerCount", snapshot.PeerCount);
        Number(b, "resourceReceiptCount", snapshot.ResourceReceiptCount); Number(b, "sharedProgressCount", snapshot.SharedProgressCount);
        Bool(b, "connected", snapshot.Connected); Bool(b, "sessionPresent", snapshot.SessionPresent);
        Bool(b, "voluntaryObservation", snapshot.VoluntaryObservation);
        Bool(b, "isInSceneTransition", snapshot.IsInSceneTransition); Bool(b, "hasFinishedEnteringScene", snapshot.HasFinishedEnteringScene);
        Bool(b, "heroInPosition", snapshot.HeroInPosition); Bool(b, "heroAcceptingInput", snapshot.HeroAcceptingInput);
        Bool(b, "heroControlRelinquished", snapshot.HeroControlRelinquished); Bool(b, "heroDead", snapshot.HeroDead);
        Bool(b, "heroHazardDeath", snapshot.HeroHazardDeath); Bool(b, "heroHazardRespawning", snapshot.HeroHazardRespawning);
        Bool(b, "heroTransitioning", snapshot.HeroTransitioning); Bool(b, "heroBoxInactive", snapshot.HeroBoxInactive);
        Bool(b, "bodySimulated", snapshot.BodySimulated); Bool(b, "cameraFrozen", snapshot.CameraFrozen);
        Bool(b, "inBossSequence", snapshot.InBossSequence); Bool(b, "changingScene", snapshot.ChangingScene);
        Bool(b, "down", snapshot.Down); Bool(b, "resetting", snapshot.Resetting); Bool(b, "returning", snapshot.Returning);
        Bool(b, "hasNativeDeath", snapshot.HasNativeDeath); Bool(b, "hasCheckpointReload", snapshot.HasCheckpointReload);
        Bool(b, "hasBossContextGuard", snapshot.HasBossContextGuard); Bool(b, "hasTeammateWarp", snapshot.HasTeammateWarp); Bool(b, "hasTransientLockRisk", snapshot.HasTransientLockRisk);
        b.Append(",\"risks\":[");
        var risks = snapshot.RiskCodes();
        for (var i = 0; i < risks.Count; i++) { if (i > 0) b.Append(','); b.Append(StringValue(risks[i])); }
        b.Append("],\"events\":[");
        if (events != null) for (var i = 0; i < events.Count; i++) {
            if (i > 0) b.Append(',');
            var e = events[i];
            b.Append("{\"sequence\":").Append(e.Sequence).Append(",\"time\":").Append(e.Time.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Field(b, "severity", e.Severity.ToString()); Field(b, "code", e.Code); Field(b, "scene", e.Scene); Field(b, "detail", Redact(e.Detail));
            b.Append('}');
        }
        b.Append("]}");
        return b.ToString();
    }

    private static void Field(StringBuilder b, string name, string value) => b.Append(",\"").Append(name).Append("\":").Append(StringValue(value));
    private static void Number(StringBuilder b, string name, int value) => b.Append(",\"").Append(name).Append("\":").Append(value);
    private static void Bool(StringBuilder b, string name, bool value) => b.Append(",\"").Append(name).Append("\":").Append(value ? "true" : "false");
    private static string StringValue(string value) {
        if (value == null) value = "";
        var b = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value) {
            switch (c) { case '\\': b.Append("\\\\"); break; case '"': b.Append("\\\""); break; case '\r': b.Append("\\r"); break; case '\n': b.Append("\\n"); break; case '\t': b.Append("\\t"); break; default: b.Append(c); break; }
        }
        return b.Append('"').ToString();
    }
    private static string Redact(string value) {
        if (string.IsNullOrEmpty(value)) return "";
        var result = Regex.Replace(value, @"(?i)(player|name|ip|token)\s*=\s*[^,\s;]+", "$1=<redacted>");
        result = Regex.Replace(result, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "<redacted>");
        return result;
    }
}
