using System;
using System.Reflection;
using Modding;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

/// <summary>Corrects the display basis when a replica first wakes with already-damaged HP.</summary>
internal sealed class EnemyHpBarCompatibility {
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
    private readonly Type _enemyBar, _bossBar;
    private readonly FieldInfo _enemyMax, _bossMax;
    private int _observedMax;

    private EnemyHpBarCompatibility(Type enemyBar, Type bossBar, FieldInfo enemyMax, FieldInfo bossMax, int initialHp) {
        _enemyBar = enemyBar; _bossBar = bossBar;
        _enemyMax = enemyMax; _bossMax = bossMax;
        _observedMax = initialHp;
    }

    internal static EnemyHpBarCompatibility TryCreate(int initialHp) {
        var assembly = ModHooks.GetMod("EnemyHPBar", onlyEnabled: true)?.GetType().Assembly;
        if (assembly == null) return null;
        var enemy = assembly.GetType("EnemyHPBar.HPBar");
        var boss = assembly.GetType("EnemyHPBar.BossHPBar");
        var enemyMax = enemy?.GetField("maxHP", PublicInstance);
        var bossMax = boss?.GetField("maxHP", PublicInstance);
        return typeof(MonoBehaviour).IsAssignableFrom(enemy) && typeof(MonoBehaviour).IsAssignableFrom(boss) &&
            enemyMax?.FieldType == typeof(float) && bossMax?.FieldType == typeof(float)
            ? new EnemyHpBarCompatibility(enemy, boss, enemyMax, bossMax, initialHp) : null;
    }

    internal void Observe(int hp) => _observedMax = System.Math.Max(_observedMax, hp);

    internal void UpdateReplica(HealthManager replica) {
        if (_observedMax <= 0 || replica == null || !replica.gameObject.activeInHierarchy || replica.isDead) return;
        var bar = replica.GetComponent(_bossBar);
        var field = _bossMax;
        if (bar == null) { bar = replica.GetComponent(_enemyBar); field = _enemyMax; }
        if (bar != null && (float)field.GetValue(bar) < _observedMax) field.SetValue(bar, (float)_observedMax);
    }
}
