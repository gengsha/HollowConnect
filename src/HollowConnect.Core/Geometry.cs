using System;

namespace HollowConnect.Core;

public static class Geometry {
    public static bool TryProject(float roomX, float roomY, float spriteWidth, float spriteHeight,
        float sceneWidth, float sceneHeight, float originX, float originY, float x, float y, out float mx, out float my) {
        mx = my = 0;
        foreach (var f in new[] { roomX, roomY, spriteWidth, spriteHeight, sceneWidth, sceneHeight, originX, originY, x, y })
            if (!Party.Finite(f)) return false;
        if (sceneWidth <= 0 || sceneHeight <= 0 || spriteWidth <= 0 || spriteHeight <= 0) return false;
        mx = roomX - spriteWidth / 2 + (x + originX) / sceneWidth * spriteWidth;
        my = roomY - spriteHeight / 2 + (y + originY) / sceneHeight * spriteHeight;
        return Party.Finite(mx) && Party.Finite(my);
    }
    public static void Edge(float dx, float dy, float halfWidth, float halfHeight, out float x, out float y) {
        if (Math.Abs(dx) + Math.Abs(dy) < .001f) { x = halfWidth; y = 0; return; }
        var scale = Math.Min(Math.Abs(dx) < .001f ? float.MaxValue : halfWidth / Math.Abs(dx),
            Math.Abs(dy) < .001f ? float.MaxValue : halfHeight / Math.Abs(dy));
        x = dx * scale; y = dy * scale;
    }
}
