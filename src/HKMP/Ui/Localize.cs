using System;
using System.Collections.Generic;
using System.Linq;
using HollowConnect.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace Hkmp.Ui;

public static class Localize {
    private static readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    private static string cachedLanguage;
    private static global::Language.LanguageCode? nativeLanguage;
    private static string languageCode;
    private static Font font;
    // Compare the native enum without formatting strings on every UI update.
    // This also observes changes within a frame, unlike a frame-based cache.
    public static string Language {
        get {
            var current = global::Language.Language.CurrentLanguage();
            if (nativeLanguage != current) {
                nativeLanguage = current;
                languageCode = Translation.Normalize(current.ToString());
            }
            return languageCode;
        }
    }
    public static string T(string source) {
        if (source == null) return null;
        var language = Language;
        if (cachedLanguage != language) { cache.Clear(); cachedLanguage = language; font = null; }
        if (cache.TryGetValue(source, out var value)) return value;
        value = Translation.Get(source, language);
        if (cache.Count >= 512) cache.Clear();
        cache[source] = value;
        return value;
    }
    public static Font Font {
        get {
            var language = Language;
            if (cachedLanguage != language) T("");
            if (font != null) return font;
            var names = language == "JA" ? new[] { "NotoSerifCJKjp-Regular", "NotoSerifCJKsc-Regular", "Perpetua" }
                : new[] { "NotoSerifCJKsc-Regular", "Perpetua" };
            var fonts = UnityEngine.Resources.FindObjectsOfTypeAll<Font>();
            font = names.Select(name => fonts.FirstOrDefault(f => f.name == name)).FirstOrDefault(f => f != null);
            if (language == "JA" && (font == null || !font.HasCharacter('あ'))) font = UnityEngine.Font.CreateDynamicFontFromOSFont(new[] { "Yu Gothic", "Meiryo", "Noto Sans CJK JP", "Microsoft YaHei", "Arial" }, 32);
            return font ?? Modding.Menu.MenuResources.Perpetua;
        }
    }
    internal static void Attach(Text text, bool help = false) {
        if (text == null || text.GetComponentInParent<InputField>() != null) return;
        var binding = text.GetComponent<LocalizedText>() ?? text.gameObject.AddComponent<LocalizedText>();
        binding.Help = help; binding.Refresh(true);
    }
    internal static void Screen(MenuScreen screen) {
        foreach (var text in screen.GetComponentsInChildren<Text>(true)) {
            if (text.name == "Selected option help") continue; // Copies the already localized description.
            Attach(text, text.name == "Description");
        }
    }
    public static void Label(Rect rect, string text, GUIStyle style) { style.font = Font; GUI.Label(rect, T(text), style); }
    public static void LayoutLabel(string text, GUIStyle style) { style.font = Font; GUILayout.Label(T(text), style); }
    public static bool Button(string text) => GUILayout.Button(T(text));
}

internal sealed class LocalizedText : MonoBehaviour {
    private Text target;
    private string source, rendered, language;
    internal bool Help;
    private void OnEnable() => Refresh();
    private void LateUpdate() => Refresh();
    internal void Refresh(bool force = false) {
        if (target == null) target = GetComponent<Text>();
        if (target == null) return;
        var current = Localize.Language;
        // The native menu builder appends line endings during activation.
        // That does not make the previous translation a new canonical source.
        var changed = rendered == null || target.text != rendered && target.text.TrimEnd('\r', '\n') != rendered.TrimEnd('\r', '\n');
        if (!force && !changed && language == current) {
            if (target.text != rendered) target.text = rendered;
            return;
        }
        if (changed) source = target.text;
        language = current;
        rendered = Help ? Translation.Help(source, current) : Localize.T(source);
        target.font = Localize.Font;
        target.text = rendered;
    }
}
