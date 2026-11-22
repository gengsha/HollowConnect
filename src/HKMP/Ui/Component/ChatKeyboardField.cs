using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Hkmp.Ui.Component;

// Consume chat shortcuts in the same event loop as Unity's text editing, so
// the native field cannot move the caret or submit before chat handles a key.
internal sealed class ChatKeyboardField : InputField {
    private readonly Event _keyboardEvent = new();
    internal Func<Event, bool> Shortcut;
    internal Action RefreshPreview;

    internal bool IsComposing => !string.IsNullOrEmpty(EventSystem.current?.currentInputModule?.input?.compositionString);

    internal bool ProcessChatEvent(Event evt) {
        if (evt.rawType == EventType.KeyDown) {
            if (IsComposing && evt.keyCode is KeyCode.UpArrow or KeyCode.DownArrow or KeyCode.Tab or KeyCode.Return or KeyCode.KeypadEnter)
                return true;
            if (Shortcut?.Invoke(evt) == true) return true;
            if (KeyPressed(evt) == EditState.Finish) DeactivateInputField();
            UpdateLabel();
            return true;
        }
        if (evt.type is EventType.ExecuteCommand or EventType.ValidateCommand && evt.commandName == "SelectAll") {
            selectionAnchorPosition = 0;
            selectionFocusPosition = text.Length;
            UpdateLabel();
            return true;
        }
        return false;
    }

    public override void OnUpdateSelected(BaseEventData eventData) {
        if (!isFocused) return;
        while (isFocused && Event.PopEvent(_keyboardEvent)) ProcessChatEvent(_keyboardEvent);
        eventData.Use();
    }

    protected override void LateUpdate() {
        base.LateUpdate();
        RefreshPreview?.Invoke();
    }
}
