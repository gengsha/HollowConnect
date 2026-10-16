using System;
using System.Collections;
using HollowConnect;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HollowConnect.Smoke;

// Not connected to a runner yet. Capture immediately before the native lethal
// collision; call WhileDown before permitting the other peer to swing. The
// caller owns that real damage/rescue, so this observer cannot pass by faking a
// Down event or by forcibly unfreezing a camera after native respawn.
internal static class CoverageDeathCameraChecks {
    internal sealed class BeforeDown {
        internal CameraController Camera;
        internal CameraController.CameraMode Mode;
        internal int SceneHandle;
        internal Vector3 Position;
        internal Rect VisibleWorld, LockCameraCenters;
    }

    internal static BeforeDown Capture() {
        var camera = GameCameras.instance?.cameraController;
        var hero = HeroController.instance;
        if (camera == null || camera.cam == null || hero == null)
            throw new Exception("Death camera fixture requires a native hero and camera");
        var depth = camera.cam.WorldToViewportPoint(hero.transform.position).z;
        var low = camera.cam.ViewportToWorldPoint(new Vector3(0, 0, depth));
        var high = camera.cam.ViewportToWorldPoint(new Vector3(1, 1, depth));
        var lockLow = camera.KeepWithinLockBounds(new Vector2(-100000, -100000));
        var lockHigh = camera.KeepWithinLockBounds(new Vector2(100000, 100000));
        return new BeforeDown {
            Camera = camera, Mode = camera.mode, Position = camera.transform.position,
            SceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle,
            VisibleWorld = Rect.MinMaxRect(low.x, low.y, high.x, high.y),
            LockCameraCenters = Rect.MinMaxRect(lockLow.x, lockLow.y, lockHigh.x, lockHigh.y)
        };
    }

    internal static IEnumerator WhileDown(CoopRuntime runtime, BeforeDown before, string label,
        Action<string> log, bool requirePreviouslyOffscreenLanding = true) {
        void Require(bool value, string message) {
            if (!value) throw new Exception("Death camera " + label + ": " + message);
            log("PASS death camera " + label + " " + message);
        }
        Require(runtime.Down && UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == before.SceneHandle,
            "real lethal collision entered local rescue in the original room");
        var camera = before.Camera;
        var anchor = runtime.DeathPosition + Vector3.up * .5f;
        if (requirePreviouslyOffscreenLanding)
            Require(!before.VisibleWorld.Contains(new Vector2(anchor.x, anchor.y)),
                "safe landing was outside the old view, so the fixture can detect the reported bug");
        // Resume after at least one complete LateUpdate, not merely after the
        // code that moved HeroController to the rescue point.
        yield return null;
        yield return null;
        Require(runtime.Down && UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == before.SceneHandle,
            "rescuer held the swing until camera verification completed");
        Require(camera == GameCameras.instance.cameraController && camera.mode == CameraController.CameraMode.FROZEN,
            "the local rescue view is frozen on its own camera");
        var viewport = camera.cam.WorldToViewportPoint(anchor);
        Require(viewport.z > 0 && viewport.x > .01f && viewport.x < .99f && viewport.y > .01f && viewport.y < .99f,
            "the rescue marker is visible before being rescued");
        var frozen = camera.transform.position;
        Require(Math.Abs(frozen.z - before.Position.z) < .001f, "positioning preserves native camera depth");
        var withinScene = camera.KeepWithinSceneBounds(frozen);
        Require(Vector2.Distance(frozen, withinScene) < .025f, "the relocated view respects native scene bounds");
        if (before.Mode == CameraController.CameraMode.LOCKED) {
            // A dead hero can move across a lock boundary while its Rigidbody
            // is disabled. An obsolete lock must not hide the safe landing.
            var lockCanShowAnchor = new Rect(before.LockCameraCenters.xMin - before.VisibleWorld.width * .5f,
                before.LockCameraCenters.yMin - before.VisibleWorld.height * .5f,
                before.LockCameraCenters.width + before.VisibleWorld.width,
                before.LockCameraCenters.height + before.VisibleWorld.height).Contains(new Vector2(anchor.x, anchor.y));
            if (lockCanShowAnchor) {
                var withinLock = camera.KeepWithinLockBounds(frozen);
                Require(Vector2.Distance(frozen, withinLock) < .025f,
                    "a compatible native lock area is respected");
            } else log("PASS death camera " + label + " obsolete lock does not prevent seeing the safe landing");
        }
        // A one-time camera assignment is insufficient if LateUpdate then
        // restores the original death view. Perturb only this disposable view.
        camera.transform.position = before.Position + new Vector3(8, 5, 0);
        try {
            yield return null;
            yield return null;
            Require(runtime.Down && Vector3.Distance(camera.transform.position, frozen) < .025f,
                "subsequent frames retain the safe view instead of restoring the death view");
        } finally {
            if (runtime.Down && camera != null && UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == before.SceneHandle)
                camera.transform.position = frozen;
        }
        log("DEATH CAMERA " + label + " source=" + before.Position + " safe=" + runtime.DeathPosition +
            " frozen=" + frozen + " viewport=" + viewport + " originalMode=" + before.Mode);
    }

    internal static IEnumerator AfterRescue(CoopRuntime runtime, BeforeDown before, string label, Action<string> log) {
        var deadline = Time.realtimeSinceStartup + 5;
        while (Time.realtimeSinceStartup < deadline && (runtime.Down || runtime.Resetting || runtime.Returning ||
            GameCameras.instance?.cameraController == null ||
            GameCameras.instance.cameraController.mode == CameraController.CameraMode.FROZEN)) yield return null;
        var camera = GameCameras.instance?.cameraController;
        if (runtime.Down || runtime.Resetting || runtime.Returning || camera == null ||
            camera.mode == CameraController.CameraMode.FROZEN || UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != before.SceneHandle)
            throw new Exception("Death camera " + label + ": rescue did not restore the native camera in the same room");
        var viewport = camera.cam.WorldToViewportPoint(HeroController.instance.transform.position);
        if (viewport.z <= 0 || viewport.x <= 0 || viewport.x >= 1 || viewport.y <= 0 || viewport.y >= 1)
            throw new Exception("Death camera " + label + ": rescued player is outside the restored view");
        log("PASS death camera " + label + " rescue releases the camera with the living player visible");
    }
}

