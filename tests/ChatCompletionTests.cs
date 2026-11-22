using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Api.Command.Client;
using Hkmp.Game.Command.Client;

internal static class ChatCompletionTests {
    internal static void Register(Action<string, Action> test) {
        test("chat completes registered triggers aliases and room commands", () => {
            var manager = new ClientCommandManager();
            var command = new Teleport();
            manager.RegisterCommand(command);
            Expect(manager.GetCompletions("/t"), "/tp ", "/teleport ");
            Expect(manager.GetCompletions("/tele"), "/teleport ");
            Expect(manager.GetCompletions("/t", true), "/tp ", "/team", "/teleport ");
            Expect(manager.GetCompletions("/tp"), "/tp ");
            Expect(manager.GetCompletions("/li", true), "/list");
            Expect(manager.GetCompletions("/li"));
            Expect(manager.GetCompletions("ordinary chat"));
            manager.DeregisterCommand(command);
            Expect(manager.GetCompletions("/t"));
        });
        test("chat quotes teammate completions and preserves command parsing", () => {
            var manager = new ClientCommandManager();
            var command = new Teleport();
            manager.RegisterCommand(command);
            Expect(manager.GetCompletions("/tp "), "/tp Knight", "/tp \"Knight Two\"", "/tp 旅人");
            var quoted = manager.GetCompletions("/tp \"knight ");
            Expect(quoted, "/tp \"Knight Two\"");
            Expect(manager.GetCompletions("/teleport 旅"), "/teleport 旅人");
            if (!manager.ProcessCommand(quoted.Single())) throw new Exception("completed command was not recognized");
            Expect(command.Executed, "/tp", "Knight Two");
            Expect(manager.GetCompletions("/tp \"Knight Two\" extra"));
        });
    }

    private static void Expect(IEnumerable<string> actual, params string[] expected) {
        if (!actual.SequenceEqual(expected)) throw new Exception(string.Join(" | ", actual) + " != " + string.Join(" | ", expected));
    }

    private sealed class Teleport : IClientCommand, IClientCommandCompletion {
        public string Trigger => "/tp";
        public string[] Aliases => new[] { "/teleport" };
        internal string[] Executed;
        public void Execute(string[] arguments) => Executed = arguments;
        public IEnumerable<string> CompleteFirstArgument() => new[] { "旅人", "Knight Two", "Knight", "knight", "bad\"name" };
    }
}
