using System.Collections.Generic;
using System.Linq;
using Modding;

namespace HollowConnect;

public sealed partial class CoopRuntime {
    private static readonly int[] LinkedBannedCharms = { 8, 9, 23, 27, 29 };
    private PlayerData linkedCharmData;
    private List<int> linkedOriginalCharms, linkedAllowedCharms;
    private bool[] linkedOriginalCharmFlags;
    private int linkedOriginalNotches;
    private int linkedOriginalJoni;
    private bool linkedOriginalOvercharmed;
    private double linkedCharmNotice;
    private bool linkedCharmDirty;
    private int linkedHealBlockScope;
    private static bool LinkedBannedCharm(int id) => id == 8 || id == 9 || id == 23 || id == 27 || id == 29;
    private static bool LinkedBannedCharmKey(string key) => key == "equippedCharm_8" || key == "equippedCharm_9" ||
        key == "equippedCharm_23" || key == "equippedCharm_27" || key == "equippedCharm_29";
    private bool ReadLinkedCharm(string key, bool value) => LinkedHealthRuleEnabled && LinkedBannedCharmKey(key) ? false : value;
    private void LinkedEquipCharm(On.PlayerData.orig_EquipCharm orig, PlayerData pd, int id) {
        if (!LinkedHealthRuleEnabled || !LinkedBannedCharm(id)) { orig(pd, id); return; }
        LinkedCharmDenied();
    }
    private void LinkedUnequipCharm(On.PlayerData.orig_UnequipCharm orig, PlayerData pd, int id) {
        // Unequipping is always permitted. An explicit removal also retires
        // the suspended copy so save/exit cannot silently equip it again.
        if (LinkedHealthRuleEnabled && pd == linkedCharmData) ForgetLinkedCharm(id);
        orig(pd, id);
    }
    private void ForgetLinkedCharm(string key) {
        var id = int.Parse(key.Substring("equippedCharm_".Length));
        ForgetLinkedCharm(id);
    }
    private void ForgetLinkedCharm(int id) {
        if (linkedCharmData == null || linkedOriginalCharms == null || !LinkedBannedCharm(id)) return;
        linkedOriginalCharms.RemoveAll(value => value == id);
        for (var i = 0; i < LinkedBannedCharms.Length; i++)
            if (LinkedBannedCharms[i] == id) linkedOriginalCharmFlags[i] = false;
        linkedOriginalNotches = linkedOriginalCharms.Distinct().Sum(value => linkedCharmData.GetIntInternal("charmCost_" + value));
        linkedOriginalOvercharmed = linkedOriginalNotches > linkedCharmData.charmSlots;
    }
    private void LinkedCharmDenied() {
        linkedCharmDirty = true;
        if (Now < linkedCharmNotice) return;
        linkedCharmNotice = Now + 3;
        Notice("共享血量：禁用生命血护符、心脏、乔尼的祝福和蜂巢之血");
    }
    private void SanitizeLinkedCharms(PlayerData pd) {
        if (!LinkedHealthRuleEnabled || pd == null) return;
        var firstSuppression = linkedCharmData != pd;
        if (firstSuppression) {
            RestoreLinkedCharms();
            linkedCharmData = pd;
            linkedOriginalCharms = new List<int>(pd.equippedCharms);
            linkedAllowedCharms = pd.equippedCharms.Where(id => !LinkedBannedCharm(id)).ToList();
            linkedOriginalCharmFlags = LinkedBannedCharms.Select(id => pd.GetBoolInternal("equippedCharm_" + id)).ToArray();
            linkedOriginalNotches = pd.charmSlotsFilled; linkedOriginalOvercharmed = pd.overcharmed;
            linkedOriginalJoni = pd.joniHealthBlue;
        }
        // Only the initial native loadout could have supplied charm blue.
        // Later forbidden flag injection never creates valid charm masks.
        var charmBlue = firstSuppression ? (pd.GetBoolInternal("equippedCharm_8") ? 2 : 0) + (pd.GetBoolInternal("equippedCharm_9") ? 4 : 0) : 0;
        var worldBlue = System.Math.Max(0, pd.healthBlue - charmBlue);
        var changed = pd.equippedCharms.RemoveAll(LinkedBannedCharm) > 0;
        foreach (var id in LinkedBannedCharms) {
            var key = "equippedCharm_" + id;
            if (pd.GetBoolInternal(key)) { changed = true; pd.SetBoolInternal(key, false); }
        }
        if (!changed && !linkedCharmDirty) return;
        linkedCharmDirty = false;
        pd.CalculateNotchesUsed();
        pd.overcharmed = pd.charmSlotsFilled > pd.charmSlots;
        // The second defence also clears native effects which were already
        // running when the player joined, including Joni's converted masks.
        linkedHealBlockScope++;
        try { HeroController.instance?.CharmUpdate(); }
        finally { linkedHealBlockScope--; pd.healthBlue = worldBlue; }
        if (changed && Now >= linkedCharmNotice) {
            linkedCharmNotice = Now + 3;
            Notice("共享血量：已暂时卸下禁用护符");
        }
    }
    private void LinkedCharmUpdate(On.HeroController.orig_CharmUpdate orig, HeroController hero) {
        if (!LinkedHealthRuleEnabled) { orig(hero); return; }
        var pd = PlayerData.instance; var health = pd.health; var previous = pd.prevHealth;
        var suppressing = linkedCharmDirty || LinkedBannedCharms.Any(id => pd.GetBoolInternal("equippedCharm_" + id) || pd.equippedCharms.Contains(id));
        var charmBlue = linkedCharmData != pd ? (pd.equippedCharm_8 ? 2 : 0) + (pd.equippedCharm_9 ? 4 : 0) : 0;
        var worldBlue = System.Math.Max(0, pd.healthBlue - charmBlue);
        var initializing = !linkedHealthStateReceived;
        SanitizeLinkedCharms(PlayerData.instance);
        linkedHealBlockScope++;
        try { orig(hero); } finally {
            linkedHealBlockScope--;
            if (suppressing) pd.healthBlue = worldBlue;
            if (initializing) { pd.health = health; pd.prevHealth = previous; }
        }
    }
    private bool CanRestoreLinkedCharms => linkedCharmData != null && linkedOriginalCharms != null &&
        linkedCharmData.equippedCharms.SequenceEqual(linkedAllowedCharms);
    private int LinkedPersonalCapacity(PlayerData pd) => CanRestoreLinkedCharms ?
        linkedOriginalCharmFlags[3] ? 1 : pd.maxHealthBase + (linkedOriginalCharmFlags[2] && !pd.brokenCharm_23 ? 2 : 0) : pd.maxHealth;
    private void RestoreLinkedCharmSnapshot(PlayerData target) {
        if (!CanRestoreLinkedCharms) return;
        target.equippedCharms = new List<int>(linkedOriginalCharms);
        for (var i = 0; i < LinkedBannedCharms.Length; i++)
            target.SetBoolInternal("equippedCharm_" + LinkedBannedCharms[i], linkedOriginalCharmFlags[i]);
        target.charmSlotsFilled = linkedOriginalNotches; target.overcharmed = linkedOriginalOvercharmed;
        target.maxHealth = target.equippedCharm_27 ? 1 : target.maxHealthBase +
            (target.equippedCharm_23 && !target.brokenCharm_23 ? 2 : 0);
        target.joniHealthBlue = target.equippedCharm_27 ? linkedOriginalJoni : 0;
    }
    private void RestoreLinkedCharms() {
        if (linkedCharmData != null) RestoreLinkedCharmSnapshot(linkedCharmData);
        linkedCharmData = null; linkedOriginalCharms = linkedAllowedCharms = null; linkedOriginalCharmFlags = null;
        linkedCharmNotice = 0;
        linkedCharmDirty = false;
    }
}
