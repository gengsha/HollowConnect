#if HOLLOW_UNITY6
using System.Reflection;

namespace HollowConnect.Smoke;

// API #164 keeps these native methods private. Invoke the same game routines
// as the legacy fixtures without changing the game's publicized surface.
internal static class Unity6FixtureExtensions {
    private static readonly MethodInfo AttackMethod = typeof(HeroController).GetMethod("Attack",
        BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(GlobalEnums.AttackDirection) }, null);

    internal static void Attack(this HeroController hero, GlobalEnums.AttackDirection direction) =>
        AttackMethod.Invoke(hero, new object[] { direction });

    internal static System.Collections.IEnumerator SkipVideo(this CinematicPlayer player) => player.Skip();
}
#endif
