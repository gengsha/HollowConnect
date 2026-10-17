# Audio pool and fifth Pantheon evidence review, 2026-10-17

This review changed no production code, installed mods, game assets, or saves. It used the Hollow Knight boss net audit skill for read-only evidence assessment.

## Audio pool conclusion

The available evidence does not establish an Audio Player leak or show that audio pooling caused the reported frame drops. Disabling all `AudioPlayerOneShot` and `AudioPlayerOneShotSingle` network events would remove valid remote attack sounds: replica combat FSMs are disabled in `Entity`/`EntityInitializer`, so they do not independently run the same native audio actions. Preserve the existing sound behavior until a measured failure supports a narrower change.

Read-only UnityPy inspection of `resources.assets` found:

| GameObject | path ID | Components relevant to audio lifetime |
| --- | ---: | --- |
| Audio Player Actor 2D | 4104 | AudioSource 13828; PlayAudioAndRecycle 24255 |
| Audio Player Actor | 4105 | AudioSource 13829; PlayAudioAndRecycle 24842 |
| Audio Player UI | 4106 | AudioSource 13833; PlayAudioAndRecycle 24263 |
| Audio Player NoDestroy | 4107 | AudioSource 13834; no PlayAudioAndRecycle |

`PlayAudioAndRecycle` in the inspected `Assembly-CSharp.dll` calls `audioSource.Play()` in `OnEnable` and calls `gameObject.Recycle()` in each `Update` when `!audioSource.isPlaying`. An Actor/UI instance with no playing clip should therefore recycle on the next update, provided that component is enabled and correctly bound. This is a static lifecycle finding; this review did not measure repeated remote playback or pool counts in Unity.

`ObjectPool.orig_Spawn` warns and instantiates another object when its available pool is empty. `ObjectPool.Recycle` removes a tracked instance from `spawnedObjects`, adds it to `pooledObjects`, reparents it to the pool, and disables it (or sends the ActiveRecycler event). An expansion warning proves that concurrent demand exceeded the available pool at that instant. It does not prove that previous instances failed to return. The warning can correspond to a small allocation spike; its effect on frame time remains unmeasured.

The generic `AutoRecycleSelf` also supports AUDIO_CLIP_END, checked every 20 frames, but that is **not** the component on the three Actor/UI prefabs above. Do not add a second production recycler. Do not automatically recycle `Audio Player NoDestroy`; its native lifetime is deliberately different.

`CombatSpawns.Queue` considers registered combat prefabs or prefabs with `DamageHero`, so these audio-only prefabs do not become DynamicHazard entities through that path. The normal replica script-disabling code therefore does not, by itself, demonstrate that these freshly spawned sound prefabs lose their recycler.

The current receivers have defensive gaps worth considering only in a separate scoped fix: select and validate the clip before spawning; reject a missing/destroyed prefab or spawn point; validate weighted-array bounds; guard optional `storePlayer`; handle a missing AudioSource without leaving a newly tracked instance. These are confirmed code paths, not evidence that malformed actions occurred in the reported rooms. Replacing valid audio with `return false` is not a supported performance fix.

An eventual audio-specific runtime probe should use a real native prefab/clip and the production receive path, check that a remote sound starts, wait for its completion and return to the pool, then repeat. A null-clip/missing-prefab case must not increase active spawned instances. Record per-frame time and spawned/pooled counts under the same attack sequence before attributing a slowdown to audio. Existing muted batch probes do not establish perceived audio correctness or 144 FPS performance.

## FSM warning deduplication

Deduplicate invalid host-state warnings by `(fsmIndex, stateIndex)` per entity. Deduplicating only the state index hides a distinct mismatch in another FSM. Include entity/type/scene, FSM index/name, received state and local state count. The currently observed tuple-based change in `Entity.UpdateHostFsmData` addresses that diagnostic issue; it does not repair a native FSM layout mismatch.

## Fifth Pantheon boundary

The following existing two-client local probes reached `GG_Uumuu_V`, index 32, with `inSequence=True` after Watcher Knights, and contain both client completion records:

- `.test-results/p5-watchers-018-baseline-20261016`
- `.test-results/p5-motion-persistent-rescue-20261016`
- `.test-results/p5-motion-persistent-final-20261016`

These exercise the selected Watcher Knights segment, with preceding rescue/reconnect scenarios where configured. They do not cover a complete native fifth-Pantheon journey from its entrance, all prior room history, or real two-machine transport. The user-reported exit after Watcher Knights remains **not reproduced**, not disproved or confirmed fixed. An older `user-bugs-018` log returned to Workshop but recorded a statue boss scene without the sequence continuation marker; that run does not establish the reported fifth-Pantheon failure.

## Inspected installation

Game data directory: `E:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data`.

| File relative to data directory | Bytes | SHA-256 |
| --- | ---: | --- |
| Managed/Assembly-CSharp.dll | 4064768 | 5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D |
| Managed/PlayMaker.dll | 212480 | 0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A |
| resources.assets | 30209728 | F2A9851F0424D94C0231B2689AEB51327FC7054F284BD77DB2E3293D80B9386C |

Native methods were inspected with the repository's `.tools/ilspycmd.exe`; no native methods were executed by the static inspection.
