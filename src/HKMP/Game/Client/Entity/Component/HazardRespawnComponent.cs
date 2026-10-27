using System.Collections.Generic;
using System.Linq;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using Modding;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the hazard respawn changes within certain bossfights. Currently only Radiance and Absolute
/// Radiance.
internal class HazardRespawnComponent : EntityComponent {
    /// <summary>
    /// The offset of the climb hazard respawn indices.
    /// </summary>
    private const int ClimbRespawnOffset = 1;
    
    /// <summary>
    /// The Control FSM of the host entity.
    /// </summary>
    private readonly PlayMakerFSM _hostControlFsm;
    /// <summary>
    /// The game object that holds the Ascend Respawn objects.
    /// </summary>
    private readonly GameObject _ascendRespawnsObject;
    /// <summary>
    /// List of hazard respawn trigger behaviours.
    /// </summary>
    private readonly List<HazardRespawnTrigger> _hazardRespawnTriggers;

    /// <summary>
    /// The last state of 'active' of the Ascend Respawn object.
    /// </summary>
    private bool _lastActiveAscendsRespawns;
    /// <summary>
    /// The index of the highest respawn that has been triggered locally or received from the server.
    /// </summary>
    private int _highestRespawn = -1;
    
    public HazardRespawnComponent(
        NetClient netClient,
        ulong entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        _hazardRespawnTriggers = new List<HazardRespawnTrigger>();
        var host = gameObject.Host;
        if (host == null) {
            Logger.Warn("Hazard respawn component has no host object");
            return;
        }
        _hostControlFsm = host.LocateMyFSM("Control");
        if (!_hostControlFsm) {
            Logger.Error("Could not find 'Control' FSM on Radiance host object");
            return;
        }
        
        var climbState = _hostControlFsm.FsmStates.FirstOrDefault(s => s.Name == "Climb Plats1");
        if (climbState != null) {
            try {
                _hostControlFsm.InsertMethod("Climb Plats1", 7, () => {
                    if (IsControlled) return;
                    Logger.Debug("Climb Plats1 state reached, sending hazard respawn data");

                    _highestRespawn = 0;

                    var data = new EntityNetworkData {
                        Type = EntityComponentType.HazardRespawn
                    };

                    data.Packet.Write((byte) 0);
                    data.Packet.Write(_lastActiveAscendsRespawns);

                    SendData(data);
                });
            } catch (System.Exception exception) {
                Logger.Warn($"Could not hook Radiance climb hazard state: {exception.GetType().Name}");
            }
        } else {
            Logger.Warn("Radiance Control FSM has no Climb Plats1 state; hazard sync will use triggers only");
        }

        // Find the Ascend Respawns objects and add all HazardRespawnTrigger behaviours to the list
        var hostParent = host.transform.parent;
        if (hostParent) {
            _ascendRespawnsObject = hostParent.gameObject.FindGameObjectInChildren("Ascend Respawns");
            if (_ascendRespawnsObject) {
                foreach (var hazardRespawnTrigger in _ascendRespawnsObject.GetComponentsInChildren<HazardRespawnTrigger>(true)) {
                    if (hazardRespawnTrigger != null) {
                        _hazardRespawnTriggers.Add(hazardRespawnTrigger);
                        
                        Logger.Debug($"Added '{hazardRespawnTrigger.gameObject.name}' to list of hazard respawn triggers");
                    }
                }
                _lastActiveAscendsRespawns = _ascendRespawnsObject.activeSelf;
            }
        }
        
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
        On.HazardRespawnTrigger.OnTriggerEnter2D += HazardRespawnTriggerOnTriggerEnter2D;
        On.HeroController.FinishedEnteringScene += FinishedEnteringScene;
    }

    /// <summary>
    /// Hook for the "OnTriggerEnter2D" method of HazardRespawnTrigger to network that a certain hazard respawn trigger
    /// has been reached.
    /// </summary>
    private void HazardRespawnTriggerOnTriggerEnter2D(
        On.HazardRespawnTrigger.orig_OnTriggerEnter2D orig, 
        HazardRespawnTrigger self, 
        Collider2D otherCollider
    ) {
        if (self == null || otherCollider == null) return;
        var inactive = ReflectionHelper.GetField<HazardRespawnTrigger, bool>(self, "inactive"); 
        if (inactive || otherCollider == null || otherCollider.gameObject == null ||
            otherCollider.gameObject.layer != 9) {
            orig(self, otherCollider);
            return;
        }
        
        orig(self, otherCollider);

        // The hook is global, but each component owns only one boss's trigger
        // list. Without this membership check a trigger from another Radiance
        // instance (or another boss that happens to use the same native hook)
        // can advance this component's respawn index.
        if (IsControlled || !_hazardRespawnTriggers.Contains(self)) return;
        
        var name = self.gameObject.name;
        
        Logger.Debug($"Player triggered hazard respawn: {name}");

        var numRespawn = _hazardRespawnTriggers.IndexOf(self) + ClimbRespawnOffset;
        if (numRespawn < ClimbRespawnOffset) return;
        if (name.Contains("(") && name.Contains(")")) {
            if (int.TryParse(name.Split('(')[1].Split(')')[0], out var result)) {
                var namedRespawn = ClimbRespawnOffset + result;
                if (namedRespawn >= ClimbRespawnOffset && namedRespawn <= _hazardRespawnTriggers.Count) numRespawn = namedRespawn;
            }
        }
        
        Logger.Debug($"Num respawn: {numRespawn}");

        if (numRespawn > _highestRespawn) {
            _highestRespawn = numRespawn;
            
            var data = new EntityNetworkData {
                Type = EntityComponentType.HazardRespawn
            };
            
            data.Packet.Write((byte) numRespawn);
            data.Packet.Write(_lastActiveAscendsRespawns);

            SendData(data);
            
            Logger.Debug("Num respawn exceeds highest respawn, sending to server");
        } else {
            Logger.Debug("Num respawn is less than or equal to highest respawn");
        }
    }
    
    /// <summary>
    /// Update hook to check for changes in the active state of the Ascend Respawns object and network them.
    /// </summary>
    private void OnUpdate() {
        if (IsControlled || _ascendRespawnsObject == null) {
            return;
        }
        
        var active = _ascendRespawnsObject.activeSelf;
        if (active != _lastActiveAscendsRespawns) {
            _lastActiveAscendsRespawns = active;
            
            Logger.Debug($"Ascends Respawns object changed active: {active}, sending to server");
            
            var data = new EntityNetworkData {
                Type = EntityComponentType.HazardRespawn
            };
            
            // A state change can occur before the first climb trigger. The
            // wire format has no negative sentinel; zero is the stable base
            // platform respawn and is safe to replay on a late client.
            data.Packet.Write((byte) Mathf.Clamp(_highestRespawn, 0, byte.MaxValue));
            data.Packet.Write(active);

            SendData(data);
        }
    }

    /// <inheritdoc />
    public override void InitializeHost() {
    }

    private void FinishedEnteringScene(On.HeroController.orig_FinishedEnteringScene orig, HeroController hero,
        bool setHazardRespawn, bool preventRunBob) {
        orig(hero, setHazardRespawn, preventRunBob);
        // A scene snapshot may arrive before the native entry finishes.
        // FinishedEnteringScene then writes the entrance's old floor marker
        // over the phase checkpoint. Reapply once after that native write;
        // later local climb triggers remain free to advance their own marker.
        if (IsControlled && hero == HeroController.instance && _highestRespawn >= 0)
            ApplyRespawn(_highestRespawn);
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var numRespawn = data.Packet.ReadByte();
        var ascendsRespawnsActive = data.Packet.ReadBool();

        if (IsControlled && _ascendRespawnsObject != null) {
            _ascendRespawnsObject.SetActive(ascendsRespawnsActive);
        }

        if (numRespawn <= _highestRespawn) {
            Logger.Debug("Num respawn received is less than or equal to already registered highest respawn");
            return;
        }

        if (ApplyRespawn(numRespawn)) _highestRespawn = numRespawn;
    }

    private bool ApplyRespawn(int numRespawn) {
        if (numRespawn == 0) {
            if (_hostControlFsm == null || HeroController.instance == null) return false;
            var p2AHazardVar = _hostControlFsm.FsmVariables.GetFsmGameObject("P2A Hazard");
            if (p2AHazardVar == null) {
                Logger.Error("Could not find P2A Hazard variable in host FSM");
                return false;
            }

            var p2AHazard = p2AHazardVar.Value;
            if (!p2AHazard) {
                Logger.Error("P2A Hazard variable value is null in host FSM");
                return false;
            }
            
            Logger.Debug("Setting hazard respawn to plats hazard respawn");
            HeroController.instance.SetHazardRespawn(p2AHazard.transform.position, true);
        } else {
            if (numRespawn > _hazardRespawnTriggers.Count) {
                Logger.Error($"Received numRespawn = {numRespawn}, but there is no matching hazard respawn trigger");
                return false;
            }

            var hazardRespawnTrigger = _hazardRespawnTriggers[numRespawn - 1];
            if (hazardRespawnTrigger == null || hazardRespawnTrigger.respawnMarker == null) {
                Logger.Error($"Respawn trigger {numRespawn} has no valid respawn marker");
                return false;
            }
            // Validate the destination before changing earlier trigger state.
            for (var i = numRespawn; i > 0; i--) {
                var earlier = _hazardRespawnTriggers[i - 1];
                if (earlier != null) ReflectionHelper.SetField(earlier, "inactive", true);
            }
            if (PlayerData.instance == null) return false;
            PlayerData.instance.SetHazardRespawn(hazardRespawnTrigger.respawnMarker);
            
            Logger.Debug($"Setting hazard respawn to climb phase respawn: {hazardRespawnTrigger.gameObject.name}");
        }
        
        return true;
    }

    /// <inheritdoc />
    public override void Destroy() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        On.HazardRespawnTrigger.OnTriggerEnter2D -= HazardRespawnTriggerOnTriggerEnter2D;
        On.HeroController.FinishedEnteringScene -= FinishedEnteringScene;
    }
}
