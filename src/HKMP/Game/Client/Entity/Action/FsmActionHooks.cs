using System;
using System.Collections.Generic;
using Hkmp.Logging;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using UnityEngine.SceneManagement;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity.Action; 

/// <summary>
/// Static class for registering callbacks on the "OnEnter" method of an <see cref="FsmStateAction"/> class.
/// </summary>
internal static class FsmActionHooks {
    internal static FsmStateAction CurrentAction;
    /// <summary>
    /// Dictionary mapping types (subtypes of <see cref="FsmStateAction"/>) to an hook class.
    /// </summary>
    private static readonly Dictionary<Type, FsmActionHook> TypeEvents;

    /// <summary>
    /// List of all registered hooks. Used to loop over and remove all.
    /// </summary>
    // ReSharper disable once CollectionNeverQueried.Local
    private static readonly List<Hook> Hooks;

    static FsmActionHooks() {
        TypeEvents = new Dictionary<Type, FsmActionHook>();
        Hooks = new List<Hook>();
    }

    /// <summary>
    /// Register hooks for the FSM actions.
    /// </summary>
    public static void RegisterHooks() {
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnSceneChanged;
    }

    /// <summary>
    /// Deregister hooks for the FSM actions. This will also clear the registered hooks for the type events.
    /// </summary>
    public static void DeregisterHooks() {
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnSceneChanged;
        
        ResetHooks();
    }

    /// <summary>
    /// Register an action as callback on the "OnEnter" method of an <see cref="FsmStateAction"/> class.
    /// </summary>
    /// <param name="type">The subtype of <see cref="FsmStateAction"/> to register the callback for.</param>
    /// <param name="action">The action that will be called when the "OnEnter" method executes with the instance
    /// as the parameter to the action.</param>
    public static void RegisterFsmStateActionType(Type type, Action<FsmStateAction> action) {
        if (!TypeEvents.TryGetValue(type, out var fsmActionHook)) {
            fsmActionHook = new FsmActionHook();

            var onEnterMethodInfo = type.GetMethod("OnEnter");

            Hooks.Add(new Hook(
                onEnterMethodInfo,
                OnActionEntered
            ));

            TypeEvents.Add(type, fsmActionHook);
        }

        fsmActionHook.HookEvent += action;
    }
    internal static void RemoveObserver(Type type, Action<FsmStateAction> action) {
        if (TypeEvents.TryGetValue(type, out var hook)) hook.Remove(action);
    }
    internal static void RegisterFsmStateActionType(Type type, GameObject owner, Action<FsmStateAction> action) {
        if (!TypeEvents.ContainsKey(type)) {
            RegisterFsmStateActionType(type, action);
            RemoveObserver(type, action);
        }
        TypeEvents[type].Owners[owner.GetInstanceID()] = action;
    }
    internal static void RemoveObserver(Type type, GameObject owner, Action<FsmStateAction> action) {
        if (!ReferenceEquals(owner, null) && TypeEvents.TryGetValue(type, out var hook)) hook.Owners.Remove(owner.GetInstanceID());
    }

    /// <summary>
    /// Callback method on the "OnEnter" method for a specific <see cref="FsmStateAction"/> class.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The instance on which it was called.</param>
    private static void OnActionEntered(Action<FsmStateAction> orig, FsmStateAction self) {
        var previous=CurrentAction;
        CurrentAction=self;
        try { orig(self); } finally { CurrentAction=previous; }
        
        if (!TypeEvents.TryGetValue(self.GetType(), out var fsmActionHook)) {
            Logger.Warn("Hook was fired but no associated hook class was found");
            return;
        }

        fsmActionHook.InvokeEvent(self);
    }

    /// <summary>
    /// Callback method for when the scene changes, used to reset all hooks.
    /// </summary>
    private static void OnSceneChanged(Scene oldScene, Scene newScene) {
        ResetHooks();
    }

    /// <summary>
    /// Reset all hooks for the type events.
    /// </summary>
    private static void ResetHooks() {
        foreach (var actionHook in TypeEvents.Values) {
            actionHook.Clear();
        }
    }

    /// <summary>
    /// A wrapper class containing an event for all callbacks of a hook.
    /// </summary>
    private class FsmActionHook {
        /// <summary>
        /// Event for the callbacks on the hook.
        /// </summary>
        public event Action<FsmStateAction> HookEvent;
        internal readonly Dictionary<int, Action<FsmStateAction>> Owners = new();
        public void Remove(Action<FsmStateAction> action) => HookEvent -= action;

        /// <summary>
        /// Invokes all (if any) callbacks to this hook.
        /// </summary>
        /// <param name="fsmStateAction">The instance on which the hook triggered.</param>
        public void InvokeEvent(FsmStateAction fsmStateAction) {
            // A network observer must never interrupt the native FSM/death
            // chain or prevent other entities' observers from running. orig
            // remains outside this boundary so native errors still surface.
            var callbacks = HookEvent;
            var owner = fsmStateAction.Fsm?.GameObject;
            if (owner != null && Owners.TryGetValue(owner.GetInstanceID(), out var handler)) {
                try { handler(fsmStateAction); }
                catch (Exception e) { Logger.Error($"FSM network observer failed ({fsmStateAction.GetType().Name}): {e}"); }
            }
            if (callbacks == null) return;
            foreach (Action<FsmStateAction> callback in callbacks.GetInvocationList()) {
                try { callback(fsmStateAction); }
                catch (Exception e) { Logger.Error($"FSM network observer failed ({fsmStateAction.GetType().Name}): {e}"); }
            }
        }

        /// <summary>
        /// Clear all subscriptions to the hook event.
        /// </summary>
        public void Clear() {
            HookEvent = null;
            Owners.Clear();
        }
    }
}
