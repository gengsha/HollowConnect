using System;
using HollowConnect.Core;

internal static class LinkedHealthTests {
    internal static void Register(Action<string, Action> test) {
        test("linked health clamps the configurable ordinary mask bonus", () => {
            if (LinkedHealthRules.NormalizeBonus(-4) != 0 || LinkedHealthRules.NormalizeBonus(99) != 10 ||
                LinkedHealthRules.NormalizeBonus(2) != 2) throw new Exception("bonus range changed");
        });
        test("linked health counts each mask receipt once and adds one per four pieces", () => {
            var claims = new ResourceClaims();
            claims.TryClaim("mask:slyShellFrag1", out _); claims.TryClaim("mask:slyShellFrag1", out _);
            claims.TryClaim("mask:slyShellFrag2", out _); claims.TryClaim("mask:slyShellFrag3", out _);
            if (LinkedHealthRules.MaskPieces(claims.Claimed) != 3 ||
                LinkedHealthRules.Capacity(5, LinkedHealthRules.MaskPieces(claims.Claimed), 2) != 7)
                throw new Exception("duplicate mask receipt or rollover counted incorrectly");
            claims.TryClaim("mask:slyShellFrag4", out _);
            if (LinkedHealthRules.Capacity(5, LinkedHealthRules.MaskPieces(claims.Claimed), 2) != 8)
                throw new Exception("four mask pieces did not add one team mask");
        });
        test("linked health deltas stay within the shared bar", () => {
            if (LinkedHealthRules.ApplyDelta(7, -99, 8) != 0 || LinkedHealthRules.ApplyDelta(0, 4, 8) != 4 ||
                LinkedHealthRules.ApplyDelta(8, 3, 8) != 8) throw new Exception("health delta was not clamped");
        });
        test("linked health room joins use the live bar without refilling or changing its capacity", () => {
            var room = new LinkedHealthPool(); room.Join(1); room.Initialize(5, 5, 0, 2);
            Check(room.Health == 7 && room.MaxHealth == 7);
            room.Report(1, 1, -3); room.Join(2); room.Initialize(9, 9, 16, 10);
            Check(room.Health == 4 && room.MaxHealth == 7);
            room.Report(2, 1, 2); Check(room.Health == 6);
            Check(room.UpdatePieces(4) && room.Health == 7 && room.MaxHealth == 8);
            Check(!room.UpdatePieces(4));
        });
        test("linked health reorders reports acknowledges duplicates and ignores late heals after death", () => {
            var room = new LinkedHealthPool(); room.Join(1); room.Initialize(5, 5, 0, 2);
            Check(!room.Report(1, 2, 2) && room.Health == 7);
            Check(room.Report(1, 1, -4) && room.Health == 5 && room.Ack(1) == 2);
            Check(!room.Report(1, 1, -4) && room.Health == 5);
            room.Report(1, 3, -5); room.Report(1, 4, 5); Check(room.Health == 0);
            room.Refill(); Check(room.Health == 7);
            room.Report(1, 1, -1); Check(room.Health == 6);
            Check(!room.Report(1, 9000, -5));
        });
        test("linked legacy partial collection grows from native masks and its receipt baseline", () => {
            var room = new LinkedHealthPool(); room.Join(1); room.Initialize(7, 7, 0, 2);
            Check(room.MaxHealth == 9 && room.UpdatePieces(4) && room.MaxHealth == 10 && room.Health == 10);
            Check(!room.UpdatePieces(4));
            Check(room.UpdatePieces(16) && room.MaxHealth == 11);
        });
        test("linked legacy partial fragments roll over without recounting imported sources", () => {
            var room = new LinkedHealthPool(); room.Join(1); room.Initialize(4, 7, 3, 2, 3, 3);
            Check(room.MaxHealth == 9 && room.Health == 6 && !room.UpdatePieces(3));
            Check(room.UpdatePieces(4) && room.MaxHealth == 10 && room.Health == 7);
            Check(!room.UpdatePieces(7));
            Check(room.UpdatePieces(8) && room.MaxHealth == 11);
        });
        test("linked host baseline includes distinct late peer receipts once", () => {
            var room = new LinkedHealthPool(); room.Initialize(5, 5, 6, 2, 0, 2);
            Check(room.MaxHealth == 8 && room.Health == 7);
            Check(!room.UpdatePieces(6) && room.UpdatePieces(8) && room.MaxHealth == 9);
        });
        test("linked health wire preserves acknowledgements current capacity and following messages", () => {
            var packet = new Hkmp.Networking.Packet.Packet();
            new HollowConnect.Message { Kind = HollowConnect.Kind.LinkedHealthState, Id = 2, Serial = 6, Round = 3,
                LinkedHealth = 8, LinkedMaxHealth = 19, LinkedMaskPieces = 16, LinkedBonusMasks = 10, LinkedRevision = 20,
                LinkedSaveMode = 1, LinkedRestStatus = "骑士（加载中）、队友（未就位）" }.WriteData(packet);
            new HollowConnect.Message { Kind = HollowConnect.Kind.Presence, Value = 85, LinkedRestEpoch = 31 }.WriteData(packet);
            var read = new Hkmp.Networking.Packet.Packet(packet.ToArray());
            var state = new HollowConnect.Message(); state.ReadData(read);
            var next = new HollowConnect.Message(); next.ReadData(read);
            Check(state.LinkedHealth == 8 && state.LinkedMaxHealth == 19 && state.Serial == 6 && state.LinkedSaveMode == 1 &&
                state.LinkedRestStatus.Contains("加载中") && next.Value == 85 && next.LinkedRestEpoch == 31);
        });
        test("linked rest mixes distant benches and springs and includes loading members", () => {
            var rest = new LinkedRestGroup(); rest.Join(1); rest.Join(2); rest.Join(3);
            rest.Report(1, LinkedRestKind.Bench, 10); rest.Report(2, LinkedRestKind.Spring, 10);
            var count = rest.Count(10);
            Check(count.Total == 3 && count.Resting == 2 && !count.Ready && count.Label == "休息");
            Check(rest.Healing(10) == 0);
            rest.Report(3, LinkedRestKind.Bench, 10);
            Check(rest.Healing(10) == 0 && rest.Healing(10.36) == 100);
        });
        test("linked rest stale heartbeat and get-up cancel healing without shrinking the party", () => {
            var rest = new LinkedRestGroup(); rest.Join(1); rest.Join(2);
            rest.Report(1, LinkedRestKind.Bench, 20); rest.Report(2, LinkedRestKind.Spring, 20);
            rest.Healing(20); rest.Report(1, LinkedRestKind.Bench, 21.5);
            Check(!rest.Count(21.5).Ready && rest.Count(21.5).Total == 2 && rest.Healing(21.5) == 0);
            rest.Report(2, LinkedRestKind.Spring, 21.5); Check(rest.Healing(21.5) == 0);
            rest.Report(2, LinkedRestKind.None, 21.6); Check(rest.Healing(21.9) == 0);
            rest.Report(2, LinkedRestKind.Spring, 22); Check(rest.Healing(22) == 0 && rest.Healing(22.36) == 100);
        });
        test("linked rest membership changes restart the settling window", () => {
            var rest = new LinkedRestGroup(); rest.Join(1); rest.Report(1, LinkedRestKind.Bench, 30);
            rest.Healing(30); rest.Join(2); Check(rest.Healing(30.5) == 0);
            rest.Leave(2); Check(rest.Healing(30.5) == 0 && rest.Healing(30.86) == 100);
            rest.Clear(); Check(rest.Healing(31) == 0 && !rest.Count(31).Ready);
        });
        test("linked spring healing is paced once for the entire party", () => {
            var rest = new LinkedRestGroup(); rest.Join(1); rest.Join(2);
            rest.Report(1, LinkedRestKind.Spring, 40); rest.Report(2, LinkedRestKind.Spring, 40);
            Check(rest.Healing(40) == 0 && rest.Healing(40.36) == 1 && rest.Healing(40.9) == 0);
            rest.Report(1, LinkedRestKind.Spring, 41); rest.Report(2, LinkedRestKind.Spring, 41);
            Check(rest.Healing(41.37) == 1 && rest.Healing(41.38) == 0);
        });
        test("linked rest healing preserves damage report identities and cannot revive a zero pool", () => {
            var pool = new LinkedHealthPool(); pool.Join(1); pool.Initialize(5, 5, 0, 2);
            pool.Report(1, 1, -3); Check(pool.Heal(100) && pool.Health == 7 && pool.Ack(1) == 1);
            Check(!pool.Report(1, 1, -3) && pool.Health == 7);
            Check(pool.Report(1, 2, -7) && !pool.Heal(100) && pool.Health == 0);
        });
        test("linked rest progress changes version snapshots without changing health or acknowledgements", () => {
            var pool = new LinkedHealthPool(); pool.Join(1); pool.Initialize(5, 5, 0, 2); pool.Report(1, 1, -2);
            var before = pool.Revision; pool.RestChanged();
            Check(pool.Revision > before && pool.Health == 5 && pool.Ack(1) == 1);
        });
        test("linked rest damage rejects older resting heartbeats and requires a fresh settling window", () => {
            var rest = new LinkedRestGroup(); rest.Join(1); rest.Report(1, LinkedRestKind.Bench, 50, 2);
            Check(rest.Healing(50) == 0); rest.Interrupt(1, 3, 50.3);
            rest.Report(1, LinkedRestKind.Bench, 50.4, 2);
            Check(!rest.Count(50.4).Ready && rest.Healing(50.7) == 0);
            rest.Report(1, LinkedRestKind.Spring, 50.8, 3);
            Check(rest.Healing(50.8) == 0 && rest.Healing(51.16) == 1);
        });
        test("linked damage rest epochs follow buffered hits and ignore duplicate damage reports", () => {
            var pool = new LinkedHealthPool(); pool.Join(1); pool.Initialize(5, 5, 0, 2);
            Check(!pool.Report(1, 2, -2, 4, out var damaged, out var epoch) && !damaged);
            Check(pool.Report(1, 1, 1, 3, out damaged, out epoch) && damaged && epoch == 4 && pool.Health == 5);
            Check(!pool.Report(1, 2, -2, 4, out damaged, out epoch) && !damaged && pool.Health == 5);
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("linked health invariant"); }
}
