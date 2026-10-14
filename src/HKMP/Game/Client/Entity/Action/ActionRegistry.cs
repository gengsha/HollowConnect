using System.Collections.Generic;
using System.Linq;
using Hkmp.Util;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity.Action; 

/// <summary>
/// Static class that manages loading and storing of action data. Specifically, which actions execute every frame.
/// </summary>
internal static class ActionRegistry {
    /// <summary>
    /// The file path of the embedded resource file for the action registry.
    /// </summary>
    private const string ActionRegistryFilePath = "Hkmp.Resource.action-registry.json";
    
    /// <summary>
    /// List of all entity registry entries that are loaded from the embedded file.
    /// </summary>
    private static List<ActionRegistryEntry> Entries { get; }

    static ActionRegistry() {
        Entries = FileUtil.LoadObjectFromEmbeddedJson<List<ActionRegistryEntry>>(ActionRegistryFilePath);
        if (Entries == null) {
            Logger.Warn("Could not load action registry");
        }
    }

    /// <summary>
    /// Checks whether the given FSM action is an action that executes continuously. This is the case for actions
    /// that have the "everyFrame" field and the value for this field is true or have unfinished native work
    /// (such as collision checks, fixed-update deceleration and absolute tweens with completion callbacks).
    /// </summary>
    /// <param name="action">The FSM action to check.</param>
    /// <returns>true if the action executes continuously; otherwise false.</returns>
    public static bool IsActionContinuous(FsmStateAction action) {
        var entry = Entries.FirstOrDefault(entry => entry.Type == action.GetType().Name);
        if (entry != null && entry.UpdateField == null) {
            return true;
        }

        var type = action.GetType();
        // Native/custom actions such as GetCanSeeHero and BoolAllTrue are
        // absent from the historical registry, but their everyFrame flag still
        // requires them to resume when authority changes in a waiting state.
        var fieldName = entry?.UpdateField ?? "everyFrame";
        var fieldInfo = type.GetField(fieldName);
        if (fieldInfo == null) {
            if (entry != null) Logger.Warn($"Could not find field on FSM state action class: {type}, {fieldName}");
            return false;
        }

        var value = fieldInfo.GetValue(action);
        if (value is bool boolValue) {
            return boolValue;
        }

        if (value is FsmInt fsmIntValue) {
            return fsmIntValue.Value > 0;
        }
        if (value is FsmFloat fsmFloatValue) return fsmFloatValue.Value > 0;
        if (value is FsmBool fsmBoolValue) return fsmBoolValue.Value;

        Logger.Warn($"Could not find type of the field on FSM state action class: {type}, {entry.UpdateField}");
        return false;
    }
}

/// <summary>
/// Class representing a single entry in the action registry that contains the relevant data for an action.
/// </summary>
internal class ActionRegistryEntry {
    /// <summary>
    /// The type of the action.
    /// </summary>
    [JsonProperty("type")]
    public string Type { get; set; }
    
    /// <summary>
    /// The name of the field that controls whether the actions is checked every frame.
    /// </summary>
    [JsonProperty("update_field")]
    public string UpdateField { get; set; }
}
