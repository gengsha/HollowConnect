using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Hkmp.Menu;

// Only rows created by our menus opt in. Other mods and vanilla menus keep
// their own pointer handling, even when they use CustomSetting too.
internal sealed class MenuPointerNavigation : MonoBehaviour {
    internal static MenuScreen Attach(MenuScreen screen) {
        foreach (var option in screen.GetComponentsInChildren<MenuOptionHorizontal>(true))
            if (option.GetComponent<MenuPointerNavigation>() == null)
                option.gameObject.AddComponent<MenuPointerNavigation>();
        return screen;
    }

    internal static bool HandleClick(MenuOptionHorizontal option, PointerEventData pointer) {
        if (option.GetComponent<MenuPointerNavigation>() == null) return false;
        if (!option.interactable) return true;
        // Native arrow hitboxes can miss the left ornament and fall through
        // to increment. Use the row coordinates, then keep native navigation
        // and its setting callbacks (including locked single-value rows).
        var rect = (RectTransform)option.transform;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, pointer.position, pointer.pressEventCamera, out var point))
            option.OnMove(new AxisEventData(EventSystem.current) {
                moveDir = point.x < rect.rect.center.x ? MoveDirection.Left : MoveDirection.Right
            });
        return true;
    }
}
