using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Networking.Packet.Data;
using HkmpFontManager = Hkmp.Ui.Resources.FontManager;
using Hkmp.Ui.Resources;
using UnityEngine;
using UnityEngine.UI;

namespace Hkmp.Ui.Component;

/// <summary>
/// The chat input and its session-local editing history.
/// </summary>
internal class ChatInputComponent : InputComponent {
    private const int HistoryLimit = 100;
    private const float CompletionRefreshInterval = .25f;

    private readonly ChatKeyboardField _keyboard;
    private readonly Text _ghost;
    private readonly List<string> _history = new();
    private int _historyIndex;
    private string _draft = "";
    private string _completionBase;
    private string _lastCompletion;
    private string _previewInput;
    private bool _hasPreview;
    private float _nextPreviewRefresh;

    internal Func<string, IReadOnlyList<string>> CompleteCommand { get; set; }
    internal bool IsComposing => _keyboard.IsComposing;
    public event Action<string> OnSubmit;
    internal event Action OnCancel;

    public ChatInputComponent(ComponentGroup componentGroup, Vector2 position, Vector2 size, int fontSize) : base(
        componentGroup, position, size, "", "", TextureManager.InputFieldBg,
        HkmpFontManager.UIFontRegular, fontSize, createInputField: obj => obj.AddComponent<ChatKeyboardField>()
    ) {
        Text.alignment = TextAnchor.MiddleLeft;
        // Canvas scaling can round the localized font line taller than the input rect.
        // Keep the single line visible while InputField still scrolls it horizontally.
        Text.verticalOverflow = VerticalWrapMode.Overflow;
        Text.supportRichText = false;
        Text.font = Localize.Font ?? Text.font;
        InputField.characterLimit = ChatMessage.MaxMessageLength;
        InputField.onValidateInput += (_, _, addedChar) => addedChar is '\n' or '\r' or '\t' ? '\0' : addedChar;

        _ghost = new GameObject("Command completion", typeof(RectTransform)).AddComponent<Text>();
        _ghost.transform.SetParent(Text.transform, false);
        var rect = _ghost.rectTransform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, .5f);
        _ghost.color = new Color(.88f, .91f, .96f, .48f);
        _ghost.alignment = TextAnchor.MiddleLeft;
        _ghost.supportRichText = false;
        _ghost.raycastTarget = false;
        _ghost.horizontalOverflow = HorizontalWrapMode.Overflow;
        _ghost.verticalOverflow = VerticalWrapMode.Overflow;
        _ghost.enabled = false;

        _keyboard = (ChatKeyboardField)InputField;
        _keyboard.Shortcut = HandleShortcut;
        _keyboard.RefreshPreview = RefreshCompletion;
        InputField.onValueChanged.AddListener(_ => ClearCompletion());
    }

    internal void Submit() {
        var message = InputField.text;
        if (!string.IsNullOrWhiteSpace(message) && (_history.Count == 0 || _history[_history.Count - 1] != message)) {
            if (_history.Count == HistoryLimit) _history.RemoveAt(0);
            _history.Add(message);
        }
        InputField.text = "";
        ResetNavigation();
        OnSubmit?.Invoke(string.IsNullOrWhiteSpace(message) ? "" : message);
    }

    internal void Cancel() {
        InputField.text = "";
        ResetNavigation();
        OnCancel?.Invoke();
    }

    public void Focus() {
        ResetNavigation();
        Text.font = Localize.Font ?? Text.font;
        InputField.ActivateInputField();
    }

    private void ResetNavigation() {
        _historyIndex = _history.Count;
        _draft = "";
        ClearCompletion();
    }

    private void ClearCompletion() {
        _completionBase = _lastCompletion = _previewInput = null;
        _ghost.enabled = false;
    }

    private bool HandleShortcut(Event evt) {
        if ((evt.shift || evt.control || evt.alt || evt.command) && evt.keyCode is KeyCode.UpArrow or KeyCode.DownArrow)
            return false;
        switch (evt.keyCode) {
            case KeyCode.UpArrow:
                if (_historyIndex == 0) return true;
                if (_historyIndex == _history.Count) _draft = InputField.text;
                SetEditingText(_history[--_historyIndex]);
                return true;
            case KeyCode.DownArrow:
                if (_historyIndex == _history.Count) return true;
                _historyIndex++;
                SetEditingText(_historyIndex == _history.Count ? _draft : _history[_historyIndex]);
                return true;
            case KeyCode.Tab:
                AcceptCompletion();
                return true;
            case KeyCode.Escape:
                Cancel();
                return true;
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                Submit();
                return true;
            default:
                return false;
        }
    }

    private void SetEditingText(string value) {
        InputField.text = value;
        InputField.caretPosition = InputField.text.Length;
        InputField.ForceLabelUpdate();
    }

    private bool CanComplete(string input) => input.StartsWith("/", StringComparison.Ordinal) &&
        InputField.selectionAnchorPosition == input.Length && InputField.selectionFocusPosition == input.Length;

    private string[] Candidates(string input) => CompleteCommand?.Invoke(input)
        .Where(value => value.Length <= InputField.characterLimit).ToArray() ?? Array.Empty<string>();

    private void AcceptCompletion() {
        var input = InputField.text;
        if (!CanComplete(input)) return;
        var prefix = input == _lastCompletion ? _completionBase : input;
        var candidates = Candidates(prefix);
        if (candidates.Length == 0) return;
        var current = Array.IndexOf(candidates, input);
        var candidate = candidates[(current + 1) % candidates.Length];
        SetEditingText(candidate);
        if (candidates.Length > 1 && !candidate.EndsWith(" ", StringComparison.Ordinal)) {
            _completionBase = prefix;
            _lastCompletion = candidate;
        }
    }

    private void RefreshCompletion() {
        var input = InputField.text;
        if (!InputField.isFocused || _keyboard.IsComposing || !CanComplete(input) || Text.text != input) {
            _ghost.enabled = false;
            return;
        }

        if (_previewInput != input || Time.unscaledTime >= _nextPreviewRefresh) {
            _previewInput = input;
            var candidate = Candidates(input).FirstOrDefault();
            _nextPreviewRefresh = Time.unscaledTime + CompletionRefreshInterval;
            _hasPreview = _ghost.enabled = false;
            if (candidate == null) return;
            // Quoting is applied on Tab. A quote inserted before the caret
            // cannot be part of the inline preview, so show the name's suffix.
            if (!candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase)) candidate = candidate.Replace("\"", "");
            if (!candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase)) return;

            var rect = Text.rectTransform.rect;
            var settings = Text.GetGenerationSettings(rect.size);
            var generator = Text.cachedTextGeneratorForLayout;
            // Measure trailing spaces too, so `/tp ` leaves a gap before the hint.
            var offset = (generator.GetPreferredWidth(input + "|", settings) -
                generator.GetPreferredWidth("|", settings)) / Text.pixelsPerUnit;
            var width = generator.GetPreferredWidth(candidate, settings) / Text.pixelsPerUnit;
            if (width > rect.width || offset >= rect.width) return;
            _ghost.font = Text.font;
            _ghost.fontSize = Text.fontSize;
            _ghost.fontStyle = Text.fontStyle;
            _ghost.text = candidate.Substring(input.Length);
            _ghost.rectTransform.anchoredPosition = new Vector2(offset, 0);
            _ghost.rectTransform.sizeDelta = new Vector2(rect.width - offset, rect.height);
            _hasPreview = !string.IsNullOrWhiteSpace(_ghost.text);
        }
        _ghost.enabled = _hasPreview;
    }
}
