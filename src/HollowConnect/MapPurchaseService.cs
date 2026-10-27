using System;
using System.Linq;
using HollowConnect.Core;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace HollowConnect;

// Both the shared yes/no dialogue and Iselda's shop can keep an old purchase
// prompt open while ownership arrives. Scope a free payment to that native
// purchase, leaving its dialogue, receipt and input cleanup intact.
internal sealed class MapPurchaseService : IDisposable {
    private readonly Func<string, bool> owned;
    private bool freePayment;
    internal MapPurchaseService(Func<string, bool> owned) { this.owned = owned; }
    internal void Register() {
        On.HutongGames.PlayMaker.Fsm.EnterState += EnterState;
        On.HeroController.TakeGeo += TakeGeo;
    }
    private void EnterState(On.HutongGames.PlayMaker.Fsm.orig_EnterState orig, Fsm fsm, FsmState state) {
        var previous = freePayment;
        // These native purchase actions execute synchronously on entry. Keep
        // the exemption scoped to that purchase, including nested calls only.
        freePayment = false;
        if (fsm.GameObject != null && fsm.GameObject.scene.IsValid()) {
            var key = fsm.Name == "Confirm Control" && state.Name == "Deduct Geo and set PD"
                ? fsm.Variables.FindFsmString("PD Bool Name")?.Value
                : fsm.Name == "Dialogue Page Control" && fsm.GameObject.name == "Text YN" && state.Name == "Take Geo"
                    ? fsm.Variables.FindFsmGameObject("Requester")?.Value?.GetComponents<PlayMakerFSM>()
                        .FirstOrDefault(f => f.FsmName == "Conversation Control")?.FsmVariables.FindFsmString("Got Map Bool")?.Value
                    : null;
            freePayment = ProgressCatalog.IsMapPurchase(key) && owned(key);
        }
        try { orig(fsm, state); } finally { freePayment = previous; }
    }
    private void TakeGeo(On.HeroController.orig_TakeGeo orig, HeroController hero, int amount) {
        if (!freePayment) orig(hero, amount);
    }
    public void Dispose() {
        On.HutongGames.PlayMaker.Fsm.EnterState -= EnterState;
        On.HeroController.TakeGeo -= TakeGeo;
    }
}
