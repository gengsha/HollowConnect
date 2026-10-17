# Resource sharing integration and verification (2026-10-17)

The resource receipt integration is now active in `ResourceShareService.cs` and
the model/Smoke scenarios are wired into the build. This note records the
implementation boundary and the evidence status; it is no longer a draft
instruction to rename or activate source files.

## Dependencies and integration points

- Add direct references in `src/HollowConnect/HollowConnect.csproj` to
  `../../vendor/references/MonoMod.RuntimeDetour.dll` and
  `../../vendor/references/MMHOOK_PlayMaker.dll`, both `Private=false`.
  `tests/Smoke/HollowConnect.Smoke.csproj` already uses these exact references.
- Add `ResourceClaims` to the existing slot-local `DeathStatistics`; sanitize
  it through `Copy()`. Use the existing `ILocalSettings<DeathStatistics>` path,
  not a second local settings implementation. Source receipts are separate
  from the server room union and never represent a resource balance.
- Construct/register the service for a loaded playable slot even while offline
  or sharing is off. `enabled` means connected AND permanent sharing enabled;
  it gates network sends/receives only. `ready` also excludes co-op death,
  reset, return and warp flows. Tick it before runtime's disconnected return.
- Import native receipts once at slot load / first native idle, before the
  initial room claim exchange. Never import again on reconnect, opening a
  reward dialogue, or during a trial. Source flags can precede actual grants.
  `ImportNativeReceipts()` returns false while unsafe; retry until true, then
  mark this slot imported. Its native-idle/inFlight guard is not permission to
  call it later during an active session.
- Call `ResetSlot()` on an actual save-slot change. `ResetConnection()` clears
  network queues while preserving an ongoing native pickup. Dispose on unload.
- Send initial validated local source IDs on joining/enabling sharing. Server
  keeps an independent `ResourceClaims` union, replays it to a joining member,
  and publishes each newly claimed source. Validate packet session, sender,
  bounded strings and `ResourceCatalog.TryGet` before calling `Receive`.
- Remove `nailSmithUpgrades` from shared progress and the remote `nailDamage`
  rewrite. Each player upgrades and spends ore independently. Do not infer or
  refund earlier mistaken upgrades from present balances.
- Register `ResourceClaimsTests.Register(Test)` in the existing pure test
  entry point when this step becomes active.

## Native lifecycle

Actual positive acquisition writes are intercepted at the native action, with
identity from the original persistent object / shop flag / Seer reward state.
Early bool changes are not live grants. Native fragment resets and ore spending
remain untouched. A synchronous creation context and a tag on each spawned
instant preserve identity when the actual grant happens several frames later.

The numeric grant records a local receipt immediately, but does not release its
`inFlight` marker or deactivate any object. Original Heart Container UI / Vessel
Fragment UI objects are tracked through destruction; the inspected native UI
calls RegainControl in its final Destroy Self state. Control must also be idle
for two successive updates after activity. There is no timeout that assumes
an unfinished original flow succeeded. Native scene transition clears flow
references without canceling the original game flow.

All remote sources wait behind any native resource flow, including a different
fragment source, so cached fragment counts cannot collide with native rollover.
Persistent receipt flags are committed after the original flow completes and
again when applying a remote grant. Physical pickup retirement runs only after
that point. Shop refresh is limited to changed shop sources. A queued known
shop receipt blocks the native cost state before it can deduct Geo.

## Conservative old-save handling and limits

Source receipts live beside the original game save; spending ore never erases
them. Native receipt import records historical pickups without granting again.
For an older slot whose arena completion flag is already true but whose shiny
receipt is missing, treat the source as already received. That state cannot
prove whether the old reward was received and consumed or missed; automatic
compensation is intentionally unavailable. Do not guess from charmSlots, ore,
or nail level. New live arena completion flags are never imported mid-trial;
the actual reward endpoint is observed instead.

The source union prevents duplicate resources for each local save. Two users
can each pay for the same shop source if both confirm before either packet is
received; the draft does not implement atomic server purchase reservations or
automatic fee refunds. Once a remote source is queued, its local cost preflight
blocks further purchase. Remote resource application may wait for the shop or
reward UI to close to protect the original control-release sequence.

Before declaring verified, check the hooked native delegates in a game copy
and verify: simultaneous same-source acquisition, local fragment conversion
with a different queued remote source, Seer / shop delayed instant creation,
offline consumed ore then reconnect, late arena reward and a repeated packet,
and legacy completed-but-unproven migration. Use isolated test saves only.

## SaveManager interaction audited without a game run

`SaveManager.UpdateSaveWithData(ushort, byte[])` directly sets PlayerData
internals and, for persistent bools, sets the active FSM `Activated`, saves
SceneData, and runs SaveChanges. These writes bypass ModHooks and cannot be
treated as an observed native acquisition. Setting only
`persistentBoolData.activated` in our adapter is insufficient: native SaveState
reads the FSM variable and could overwrite the receipt on disable/unload. The
active service writes both before retiring the original pickup.

The current normal mod-server UI starts with `fullSynchronisation=false`.
ClientManager.OnSaveUpdate and ServerManager.OnSaveUpdate return before these
save messages when it is false, and initial SetSaveWithData is also gated by
that mode and by non-auto-connect. The current sharing addon uses its own
ledger and does not turn this mode on. In that usual mode there is no competing
HKMP save-update route that prematurely hides a resource.

If full save synchronisation is enabled separately, most catalog resources and
all their counters are `SyncType.Player` and are stored by auth key rather than
broadcast to other players. One catalog exception exists:
`Deepnest_38 / Vessel Fragment` is `SyncType.Server`, so it can broadcast its
Activated marker before the recipient has received a resource. Its initial
snapshot can also seed a false native historical receipt. When supporting full
sync with this addon, route/skip that catalog persistent source before
SaveManager applies it (or make this mapping player-specific); do not infer a
resource grant from the prematurely delivered marker. The mapping and service
guard are active and covered by the model and save-isolation checks.

## Unity verification status

`tests/Smoke/ProgressResources.cs` is wired into the progress probe. The
isolated runs covered the actual Crossroads_13 world shard trigger, native
Salubra selection/payment (120 Geo), guest remote notch award, repeated source
offers, full-save resource protection, and native UI lifecycle observations.
The 13 selected model tests passed. The run reached the post-purchase shop
close fixture, but batch-mode input left the original Salubra controller in a
paused/controlled state; runs `r1` through `r7` preserve that failure. Run
`r8` ended during startup without a completion marker. Therefore the resource
receipt and award paths are locally verified, while the final Salubra control
release and later Nailsmith/reconnect stages remain unverified in this fixture.
The tagged instant and UDP source cases remain explicitly labeled fixtures,
not proof of every story source. See
`docs/evidence/resource-sharing-2026-10-17.json`.
