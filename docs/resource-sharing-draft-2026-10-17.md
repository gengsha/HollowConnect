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
13 selected model tests passed. Earlier isolated scenarios observed the actual
Crossroads_13 world shard trigger, source-offer deduplication and full-save
resource isolation. Runs `r1` through `r7` retain their native shop fixture
failures, and `r8` remains incomplete. None is retrospectively a clean pass.

The fixture now waits for the complete native shop/list input state before
cancelling through the actual `ListenForMenuActions` listener. It requires the
original close animation and original input/control restoration; it does not
call `RegainControl` or inject a fallback `CLOSE` event. Run `r9` observed this
close sequence and the original purchase, but its stale-confirmation fixture
incorrectly entered an inactive, uninitialized Confirm FSM directly and failed.
Strict acceptance was rejected. Its later post-stop hash capture also mismatched
after shared binaries changed and cannot establish an accepted loaded payload.

Run `r10` replaced that shortcut with two real native confirmations opened
before the host buys. The host paid the original 120 Geo once. The guest kept
its confirmation open until the source arrived, then confirmed through the
native cost entry; the queued-source guard prevented another charge/grant.
Both original close animations released control. Final Geo was 29880/30000
and slots were 4/4; reopening the shop removed the source without a scene
reload, and repeated offers changed neither Geo nor slots. These shop scenarios
passed, but the run later failed a conversion assertion because the faster peer
had already started the next fragment family. The overall strict gate therefore
failed; its post-stop hashes were 12/12 matches. Family completion barriers
were added to the fixture.

Run `r11` failed because skipping shop also skipped the scene load that
populates original reward prefabs. Strict acceptance was rejected and its
post-stop hashes matched 12/12. Run `r12` fixes that prerequisite and explicitly
continues **after** shop, with one initial shard and four initial slots supplied
by the fixture. It excludes save/world/shop. Both clients passed native tagged
mask 3-to-4 conversion (health 6, shards 0), vessel 2-to-3 conversion (reserve
33, fragments 0), three distinct shared ore offers, and each player's two real
Nailsmith upgrades. Starting at upgrade level 1 with three ore each, the native
level-2 and level-3 purchases consume one and two ore respectively, restore
original control, set only that player's nail damage and leave the peer's level
and ore unchanged. Both actual network disconnect/reconnect sequences preserve
the loaded profile's source ledger; replay does not refund spent ore or change
the personal nail. The receipt JSON roundtrip also passed, but this was not a
process restart or an on-disk save/profile reload.

Run `r12` has both `FINAL PASS` markers, clean strict runtime logs, both normal
offline markers and the server-stopped marker. The exact isolated processes
were stopped, all four DLLs matched the recorded build in all three checked
locations (12/12), and isolated Smoke DLLs were then disabled. This is a passing
after-shop continuation, not one clean end-to-end run joining the earlier shop
and world scenarios under one final payload. Seer/all story variants remain
unverified. Tagged native instants and UDP source offers remain fixtures, not
proof of every physical/story source. Both confirmations can still charge if
both players confirm before either source packet arrives; there is no server
reservation or refund mechanism. See
`docs/evidence/resource-sharing-2026-10-17.json` for the per-run evidence.
