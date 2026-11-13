using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HollowConnect.Localization;

// Shared by the game and the standalone installer. No Unity or UI dependencies.
public static class Translation {
    private sealed class Entry {
        internal string Source, English, Japanese;
        internal Regex Pattern;
        internal string Value(string language) => language == "JA" ? Japanese : English;
    }
    private static readonly Dictionary<string, Entry> entries = Load();
    private static readonly Entry[] patterns = entries.Values.Where(e => e.Pattern != null)
        .OrderByDescending(e => Regex.Replace(e.Source, @"\{\d+\}", "").Length).ToArray();
    public static string Normalize(string language) {
        var code = (language ?? "").Replace('_', '-').ToUpperInvariant();
        return code.StartsWith("ZH", StringComparison.Ordinal) ? "ZH" : code == "JA" || code.StartsWith("JA-", StringComparison.Ordinal) ? "JA" : "EN";
    }
    public static string Get(string source, string language) {
        if (string.IsNullOrEmpty(source) || Normalize(language) == "ZH") return source;
        language = Normalize(language);
        if (entries.TryGetValue(source, out var entry)) return entry.Value(language);
        foreach (var template in patterns) {
            var match = template.Pattern.Match(source);
            if (!match.Success) continue;
            var args = new object[match.Groups.Count - 1];
            for (var i = 0; i < args.Length; i++) args[i] = match.Groups[i + 1].Value;
            // Captures are opaque: never translate names, paths or server errors.
            return string.Format(CultureInfo.CurrentCulture, template.Value(language), args);
        }
        if (source.IndexOf('\n') >= 0) return string.Join("\n", source.Split('\n').Select(s => Get(s, language)));
        return source;
    }
    // Only for developer-authored menu help assembled with key bindings. Never
    // use substring replacement for player names, chat, paths or network data.
    public static string Help(string source, string language) {
        if (Normalize(language) == "ZH" || string.IsNullOrEmpty(source)) return source;
        foreach (var entry in entries.Values.Where(e => e.Pattern == null && e.Source.Any(c => c >= '\u4e00' && c <= '\u9fff'))
                     .OrderByDescending(e => e.Source.Length)) source = source.Replace(entry.Source, entry.Value(Normalize(language)));
        return source;
    }
    public static IEnumerable<string> Sources => entries.Keys;
    private static Dictionary<string, Entry> Load() {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        using (var stream = typeof(Translation).Assembly.GetManifestResourceStream("HollowConnect.Translations.tsv"))
        using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("Translation catalog missing"))) {
            string line;
            while ((line = reader.ReadLine()) != null) {
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var fields = line.Split('\t').Select(v => v.Replace("\\n", "\n").Replace("\\t", "\t")).ToArray();
                if (fields.Length != 3 || fields.Any(string.IsNullOrEmpty)) throw new InvalidDataException("Incomplete translation: " + line);
                var entry = new Entry { Source = fields[0], English = fields[1], Japanese = fields[2] };
                var tokens = Regex.Matches(entry.Source, @"\{(\d+)\}");
                if (tokens.Count > 0) {
                    var position = 0; var pattern = "\\A";
                    for (var i = 0; i < tokens.Count; i++) {
                        if (tokens[i].Groups[1].Value != i.ToString(CultureInfo.InvariantCulture)) throw new InvalidDataException("Unordered placeholders: " + entry.Source);
                        pattern += Regex.Escape(entry.Source.Substring(position, tokens[i].Index - position)) + "(.*?)";
                        position = tokens[i].Index + tokens[i].Length;
                    }
                    pattern += Regex.Escape(entry.Source.Substring(position)) + "\\z";
                    entry.Pattern = new Regex(pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                    foreach (var text in new[] { entry.English, entry.Japanese }) {
                        var translatedTokens = Regex.Matches(text, @"\{(\d+)\}").Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(v => v);
                        if (!translatedTokens.SequenceEqual(tokens.Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(v => v))) throw new InvalidDataException("Translation placeholders differ: " + entry.Source);
                        string.Format(CultureInfo.InvariantCulture, text, Enumerable.Repeat<object>("test", tokens.Count).ToArray());
                    }
                }
                if (result.ContainsKey(entry.Source)) throw new InvalidDataException("Duplicate translation: " + entry.Source);
                result.Add(entry.Source, entry);
            }
        }
        return result;
    }
}
