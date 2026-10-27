using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Game.Client.Entity;
using Newtonsoft.Json;

namespace HollowConnect.Smoke;

internal static class LocalBarrierChecks {
    internal static void Run(Action<string> mark) {
        var assembly = typeof(EntityRegistry).Assembly;
        using var stream = assembly.GetManifestResourceStream("Hkmp.Resource.entity-registry.json")
            ?? throw new Exception("Embedded entity registry is missing");
        using var reader = new StreamReader(stream);
        var entries = JsonConvert.DeserializeObject<List<EntityRegistryEntry>>(reader.ReadToEnd())
            ?? throw new Exception("Embedded entity registry is invalid");
        var all = Flatten(entries).ToArray();
        var required = new[] { "Battle Gate", "Dream Gate", "Colosseum Gate", "mantis_cage_down", "CameraLockArea" };
        if (required.Any(name => !all.Any(entry => entry.BaseObjectName == name &&
                EntityRegistry.IsLocalOnlyBarrier(entry.Type) && !EntityProcessor.ShouldNetwork(entry))))
            throw new Exception("Arena barriers must remain local-trigger objects");
        if (all.Count(entry => EntityRegistry.IsLocalOnlyBarrier(entry.Type)) < required.Length)
            throw new Exception("Local barrier type coverage is incomplete");
        var walls = all.Where(entry => entry.Type == EntityType.ColosseumWall).ToArray();
        var oldMode = Hkmp.CoopContext.LocalWorldOnly;
        try {
            Hkmp.CoopContext.LocalWorldOnly = false;
            if (walls.Length != 2 || walls.Any(entry => EntityRegistry.IsLocalOnlyBarrier(entry.Type) || !EntityProcessor.ShouldNetwork(entry)))
                throw new Exception("Moving arena walls and their collision children must synchronize in shared battles");
            Hkmp.CoopContext.LocalWorldOnly = true;
            if (walls.Any(EntityProcessor.ShouldNetwork)) throw new Exception("Private arena walls must remain native");
        } finally { Hkmp.CoopContext.LocalWorldOnly = oldMode; }
        var absoluteRadiance = all.FirstOrDefault(entry => entry.Type == EntityType.AbsoluteRadiance);
        if (absoluteRadiance == null || absoluteRadiance.ComponentTypes == null ||
            !absoluteRadiance.ComponentTypes.Contains(EntityComponentType.HazardRespawn))
            throw new Exception("Absolute Radiance must synchronize its hazard respawn component");
        if (all.Where(entry => entry.Type is EntityType.ColosseumCageSmall or EntityType.ColosseumCageLarge or EntityType.ColosseumCageZote)
                .Any(entry => !EntityProcessor.ShouldNetwork(entry)))
            throw new Exception("Enemy spawn and Zote cages must retain their existing synchronization");
        if (!EntityProcessor.ShouldNetwork(all.First(entry => entry.Type == EntityType.FalseKnight)))
            throw new Exception("Ordinary combat entities must retain network synchronization by default");
        mark("PASS battle, dream, Mantis cage and camera locks stay local; moving arena walls follow shared battles");
    }

    private static IEnumerable<EntityRegistryEntry> Flatten(IEnumerable<EntityRegistryEntry> entries) {
        foreach (var entry in entries) {
            yield return entry;
            if (entry.Children == null) continue;
            foreach (var child in Flatten(entry.Children)) yield return child;
        }
    }
}
