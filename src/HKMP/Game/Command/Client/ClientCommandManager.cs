using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Api.Command.Client;

namespace Hkmp.Game.Command.Client;

// Optional first-argument candidates from the command that resolves them.
internal interface IClientCommandCompletion {
    IEnumerable<string> CompleteFirstArgument();
}

/// <summary>
/// Class that manages commands for client-side.
/// </summary>
internal class ClientCommandManager : CommandManager<IClientCommand>, IClientCommandManager {
    /// <summary>
    /// Try to process a command given the message.
    /// </summary>
    /// <param name="message">A user-input string message.</param>
    /// <returns>True if the message was processed as a command, false otherwise.</returns>
    public bool ProcessCommand(string message) {
        var arguments = GetArguments(message);

        if (arguments.Length == 0) {
            return false;
        }

        var commandString = arguments[0];
        if (Commands.TryGetValue(commandString, out var command)) {
            command.Execute(arguments);

            return true;
        }

        return false;
    }

    // Public built-in server commands are available to connected clients. Add-on
    // server commands aren't advertised by the current protocol.
    private static readonly string[] RoomCommands = { "/list", "/skin", "/team" };

    internal IReadOnlyList<string> GetCompletions(string input, bool connected = false) {
        if (input.Length == 0 || input[0] != '/') return Array.Empty<string>();

        var separator = 0;
        while (separator < input.Length && !char.IsWhiteSpace(input[separator])) separator++;
        if (separator == input.Length) {
            var keys = connected ? Commands.Keys.Concat(RoomCommands).Distinct(StringComparer.Ordinal) : Commands.Keys.AsEnumerable();
            return keys.Where(key => key.StartsWith(input, StringComparison.Ordinal))
                .OrderBy(key => Commands.TryGetValue(key, out var registered) ? (registered.Trigger == key ? 0 : 2) : 1)
                .ThenBy(key => key, StringComparer.Ordinal)
                .Select(key => Commands.TryGetValue(key, out var registered) && registered is IClientCommandCompletion ? key + " " : key)
                .Where(key => key != input).ToArray();
        }

        if (!Commands.TryGetValue(input.Substring(0, separator), out var command) ||
            !(command is IClientCommandCompletion completion)) return Array.Empty<string>();

        var argumentStart = separator;
        while (argumentStart < input.Length && char.IsWhiteSpace(input[argumentStart])) argumentStart++;
        var head = input.Substring(0, argumentStart);
        var partial = input.Substring(argumentStart);
        var quoted = partial.StartsWith("\"");
        var namePrefix = quoted ? partial.Substring(1) : partial;
        if (namePrefix.Contains("\"") || (!quoted && namePrefix.Any(char.IsWhiteSpace))) return Array.Empty<string>();

        return completion.CompleteFirstArgument()
            .Where(value => !string.IsNullOrEmpty(value) && !value.Any(c => c == '"' || char.IsControl(c)) &&
                value.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => head + (quoted || value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value))
            .Where(candidate => candidate != input).ToArray();
    }
}
