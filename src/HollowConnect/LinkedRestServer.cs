using HollowConnect.Core;
using System.Linq;

namespace HollowConnect;

public sealed partial class CoopServerAddon {
    private void TickLinkedRest() {
        if (!linkedHealth) return;
        var now = Now;
        // A native death/wipe never turns a stale resting report into a heal.
        foreach (var member in party.Members.Values)
            if (party.Resetting || member.Life != Life.Alive && member.Life != Life.Loading)
                linkedRest.Interrupt(member.Id, 0, now);
        var count = linkedRest.Count(now);
        var missing = string.Join("、", party.Members.Values.OrderBy(m => m.Id)
            .Where(m => linkedRest.Kind(m.Id, now) == LinkedRestKind.None)
            .Select(m => m.Name + (!destinations.TryGetValue(m.Id, out var presence) || string.IsNullOrEmpty(m.Scene) ? "（加载中）" :
                now - m.Seen >= 2 ? "（等待响应）" : (presence.Aux & 8) != 0 ? "（加载中）" : "（未就位）")));
        var changed = !count.Equals(linkedRestCount) || missing != linkedRestStatus;
        linkedRestStatus = missing;
        if (changed) linkedPool.RestChanged();
        linkedRestCount = count;
        var heal = linkedRest.Healing(now);
        if (!party.Resetting && heal > 0 && linkedPool.Heal(heal)) changed = true;
        if (changed) BroadcastLinkedHealth();
    }
}
