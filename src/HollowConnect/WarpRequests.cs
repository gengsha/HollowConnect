using Hkmp;
using Hkmp.Util;
using HollowConnect.Core;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowConnect;

// Warp replies already carry Value. Zero remains a normal grant. Denials
// have no scene, so earlier clients ignore them without changing the packet.
internal enum WarpResult { Granted, Disabled, RequesterBusy, TargetMissing, TargetBusy, CoolingDown, TryAgain }

public sealed partial class CoopRuntime {
    private sealed class WarpRequest {
        internal ushort Target;
        internal uint Serial, Round, Visit;
        internal int SceneHandle;
        internal float Deadline, RetryAt;
        internal bool RetryScheduled;
    }

    private const float WarpRequestWindow = 4;
    private const float WarpRetryDelay = .25f;
    private WarpRequest pendingWarp;
    private string warpNotice = "";
    private float warpNoticeUntil;

    private void ShowWarpNotice(string text) {
        Notice(text);
        warpNotice = text;
        warpNoticeUntil = Time.realtimeSinceStartup + 3;
    }

    private bool BeginWarpRequest(ushort target) {
        if (pendingWarp?.Target == target && IsCurrentWarp(pendingWarp) &&
            Time.realtimeSinceStartup < pendingWarp.Deadline) return false;
        pendingWarp = new WarpRequest {
            Target = target, Round = CoopContext.Epoch, Visit = CoopContext.LocalVisit,
            SceneHandle = UnitySceneManager.GetActiveScene().handle,
            Deadline = Time.realtimeSinceStartup + WarpRequestWindow
        };
        SendWarpRequest(pendingWarp);
        return true;
    }

    private void SendWarpRequest(WarpRequest request) {
        request.Serial = ++warpSerial;
        request.RetryScheduled = false;
        DebugEvent("warp-request", detail: "serial=" + request.Serial + " target=" + request.Target);
        Send(new Message { Kind = Kind.WarpRequest, Target = request.Target, Serial = request.Serial });
    }

    private bool IsCurrentWarp(WarpRequest request) => Connected && CanStartWarp &&
        request.Round == CoopContext.Epoch && request.Visit == CoopContext.LocalVisit &&
        request.SceneHandle == UnitySceneManager.GetActiveScene().handle;

    private void TickWarpRequest() {
        var request = pendingWarp;
        if (request == null) return;
        if (!IsCurrentWarp(request)) {
            pendingWarp = null;
            warpNotice = "";
            DebugEvent("warp-request-cancelled");
            return;
        }
        if (!Peers.ContainsKey(request.Target)) {
            pendingWarp = null;
            ShowWarpNotice("所选队友已不在房间");
            return;
        }
        var now = Time.realtimeSinceStartup;
        if (now >= request.Deadline) {
            pendingWarp = null;
            DebugEvent("warp-request-expired", DebugEventSeverity.Warning);
            ShowWarpNotice(request.RetryAt > 0 ? "暂时无法传送，请稍后重试 /tp" : "传送没有收到回复，请重试");
        } else if (request.RetryScheduled && now >= request.RetryAt) SendWarpRequest(request);
    }

    private void ReceiveWarpReply(Message reply) {
        var request = pendingWarp;
        if (request == null || reply.Serial != request.Serial || reply.Target != request.Target ||
            request.RetryScheduled || !IsCurrentWarp(request) || Time.realtimeSinceStartup >= request.Deadline) return;
        var result = (WarpResult)reply.Value;
        DebugEvent("warp-reply", detail: "serial=" + reply.Serial + " result=" + result);
        if (result == WarpResult.TryAgain) {
            if (request.RetryAt == 0) ShowWarpNotice("正在等待传送条件恢复…");
            request.RetryAt = Time.realtimeSinceStartup + WarpRetryDelay;
            request.RetryScheduled = true;
            return;
        }
        pendingWarp = null;
        switch (result) {
            case WarpResult.Granted:
                if (reply.Serial <= handledWarp || string.IsNullOrEmpty(reply.Scene) || SceneUtil.IsNonGameplayScene(reply.Scene)) {
                    ShowWarpNotice("当前传送目的地不可用");
                    return;
                }
                warpNotice = "";
                StartTeammateWarp(reply);
                break;
            case WarpResult.Disabled: ShowWarpNotice("房主未开启队友传送"); break;
            case WarpResult.RequesterBusy: ShowWarpNotice("当前无法传送，请等待操作或复位完成"); break;
            case WarpResult.TargetMissing: ShowWarpNotice("所选队友已不在房间"); break;
            case WarpResult.CoolingDown: ShowWarpNotice("传送冷却中，请稍后重试"); break;
            default: ShowWarpNotice("当前传送目的地不可用"); break;
        }
    }

    private void StartTeammateWarp(Message message) {
        handledWarp = message.Serial;
        var travel = StartCoroutine(WarpTo(message));
        teammateWarp = changingScene ? travel : null;
    }
}
