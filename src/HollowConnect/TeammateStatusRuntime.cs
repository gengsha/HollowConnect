using System.Collections.Generic;
using System.Linq;
using Hkmp;
using Hkmp.Networking.Client;
using HollowConnect.Core;
using UnityEngine;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private TeammateStatusView teammateStatusView;
    private readonly List<TeammateStatusRow> teammateRows = new();
    private readonly Dictionary<ushort, (TeammateStatusRow Row, double Expires)> departedStatus = new();
    private float nextTeammateStatus;

    private void DisposeTeammateStatus() {
        teammateStatusView?.Dispose();
        teammateStatusView = null;
        nextTeammateStatus = 0;
        departedStatus.Clear();
        teammateRows.Clear();
    }

    private void RememberDepartedTeammate(ushort id) {
        if (teammateStatusView == null || !Peers.TryGetValue(id, out var peer) || peer.State == null) return;
        departedStatus[id] = (RemoteStatus(peer, true), Now + 4);
    }

    private static TeammateStatusRow RemoteStatus(Peer peer, bool departed = false) {
        var state = peer.State;
        TeammateStatusData.Read(state.BossReturnScene, out var white, out var blue, out var rtt, out var fresh);
        // Legacy servers don't publish a freshness flag. Their rows can still
        // show names and the absence of a measurement.
        var offline = departed || (state.BossReturnScene.Length > 0 && !fresh) || Now - peer.Seen >= 2;
        return new TeammateStatusRow(state.Name, white, blue, rtt, false, peer.Life is Life.Down or Life.Spectating, offline);
    }

    private void TickTeammateStatus() {
        var visible = CoopMod.Settings.TeammateStatus && Connected && !LinkedHealthRuleEnabled && Playable && !changingScene &&
            !GameManager.instance.isPaused && !Hkmp.Util.SceneUtil.IsNonGameplayScene(CoopContext.LocalScene);
        if (!visible) {
            teammateStatusView?.Hide();
            nextTeammateStatus = 0;
            return;
        }
        if (Time.unscaledTime < nextTeammateStatus) return;
        nextTeammateStatus = Time.unscaledTime + .1f;
        teammateRows.Clear();
        var pd = PlayerData.instance;
        TeammateStatusData.Unpack(TeammateStatusData.Pack(pd.health, pd.healthBlue), out var localWhite, out var localBlue);
        var update = (api.NetClient as NetClient)?.UpdateManager;
        int? rtt = update?.ReceivedAgeMilliseconds >= 0 ? update.AverageRtt : (int?)null;
        teammateRows.Add(new TeammateStatusRow(localName, localWhite, localBlue, rtt, true, Down, false));
        foreach (var pair in Peers.OrderBy(p => p.Key)) {
            if (pair.Value.State == null) continue;
            teammateRows.Add(RemoteStatus(pair.Value));
            if (teammateRows.Count >= TeammateStatusView.MaxRows) break;
        }
        var now = Now;
        foreach (var departed in departedStatus.ToArray()) {
            var row = departed.Value.Row;
            if (departed.Value.Expires <= now || Peers.ContainsKey(departed.Key) || teammateRows.Any(p => p.Name == row.Name))
                departedStatus.Remove(departed.Key);
            else if (teammateRows.Count < TeammateStatusView.MaxRows) teammateRows.Add(row);
        }
        if (teammateRows.Count <= 1) {
            teammateStatusView?.Hide();
            return;
        }
        teammateStatusView ??= new TeammateStatusView(transform);
        teammateStatusView.Show(teammateRows);
    }
}
