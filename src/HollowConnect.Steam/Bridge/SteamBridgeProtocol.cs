// Adapted from Muurrcc/AHK-asyncronus-hollow-knight, commit 622735cb788dc718c8de0ec5c0ab4522366bcf2f.
// LGPL-2.1; see LICENSE and ../THIRD-PARTY.md. HollowConnect adaptations (2026).
using System;
using System.Text;

namespace HollowConnect.Steam.Bridge;

/// <summary>
/// Result of the bridge handshake, sent from the host bridge to the client bridge.
/// </summary>
internal enum SteamBridgeResult : byte {
    /// <summary>The client was accepted and datagrams are forwarded from now on.</summary>
    Accepted = 0,
    /// <summary>The password did not match.</summary>
    WrongPassword = 1,
    /// <summary>The host has reached its connection limit.</summary>
    ServerFull = 2,
    /// <summary>The bridge protocol version differs.</summary>
    VersionMismatch = 3,
    /// <summary>The host is not hosting anymore.</summary>
    NotHosting = 4,
    /// <summary>The hello message could not be parsed.</summary>
    Malformed = 5,
    /// <summary>No hello/response was received in time.</summary>
    TimedOut = 6
}

/// <summary>
/// Messages exchanged by the bridges before raw HKMP datagrams are forwarded. This is NOT part of the HKMP protocol:
/// it only lives on the Steam connection between the two bridges.
/// <para>
/// Hello (client -> host, reliable): "HKSB" | version (1 byte) | flags (1 byte, 0) | password length (2 bytes LE) |
/// password (UTF-8). Response (host -> client, reliable): "HKSR" | version (1 byte) | result (1 byte).
/// </para>
/// Pure code without game or Steam dependencies.
/// </summary>
internal static class SteamBridgeProtocol {
    /// <summary>
    /// Version of the bridge handshake.
    /// </summary>
    public const byte Version = 1;

    /// <summary>
    /// Maximum length (in characters) of a password.
    /// </summary>
    public const int MaxPasswordLength = 64;

    /// <summary>
    /// Base value for application specific Steam connection end reasons (k_ESteamNetConnectionEnd_App_Min).
    /// </summary>
    public const int CloseReasonBase = 1000;

    /// <summary>
    /// Maximum size of a forwarded datagram (maximum UDP payload).
    /// </summary>
    public const int MaxDatagramSize = 1400;

    private static readonly byte[] HelloMagic = { (byte) 'H', (byte) 'K', (byte) 'S', (byte) 'B' };
    private static readonly byte[] ResponseMagic = { (byte) 'H', (byte) 'K', (byte) 'S', (byte) 'R' };

    /// <summary>
    /// Build the hello message.
    /// </summary>
    /// <param name="password">The password, or null/empty for none.</param>
    /// <returns>The message bytes.</returns>
    public static byte[] BuildHello(string password) {
        var passwordBytes = Encoding.UTF8.GetBytes(TrimPassword(password));
        var message = new byte[8 + passwordBytes.Length];
        Array.Copy(HelloMagic, message, 4);
        message[4] = Version;
        message[5] = 0;
        message[6] = (byte) (passwordBytes.Length & 0xFF);
        message[7] = (byte) (passwordBytes.Length >> 8);
        Array.Copy(passwordBytes, 0, message, 8, passwordBytes.Length);
        return message;
    }

    /// <summary>
    /// Parse a hello message.
    /// </summary>
    /// <param name="data">The received bytes.</param>
    /// <param name="version">The version announced by the client.</param>
    /// <param name="password">The password (empty string if none).</param>
    /// <returns>True if the message is a well formed hello (of any version), false otherwise.</returns>
    public static bool TryParseHello(byte[] data, out byte version, out string password) {
        version = 0;
        password = null;

        if (data == null || data.Length < 8 || !StartsWith(data, HelloMagic)) {
            return false;
        }

        version = data[4];
        var length = data[6] | (data[7] << 8);
        if (data[5] != 0 || length > MaxPasswordLength * 4 || data.Length != 8 + length) {
            return false;
        }

        try {
            password = new UTF8Encoding(false, true).GetString(data, 8, length);
        } catch (ArgumentException) {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Build the response message.
    /// </summary>
    /// <param name="result">The handshake result.</param>
    /// <returns>The message bytes.</returns>
    public static byte[] BuildResponse(SteamBridgeResult result) {
        return [ResponseMagic[0], ResponseMagic[1], ResponseMagic[2], ResponseMagic[3], Version, (byte) result];
    }

    /// <summary>
    /// Parse a response message.
    /// </summary>
    /// <param name="data">The received bytes.</param>
    /// <param name="result">The handshake result.</param>
    /// <returns>True if the message is a well formed response, false otherwise.</returns>
    public static bool TryParseResponse(byte[] data, out SteamBridgeResult result) {
        result = SteamBridgeResult.Malformed;
        if (data == null || data.Length != 6 || !StartsWith(data, ResponseMagic) ||
            data[4] != Version || data[5] > (byte)SteamBridgeResult.TimedOut) {
            return false;
        }

        result = (SteamBridgeResult) data[5];
        return true;
    }

    /// <summary>
    /// Compare the expected password with the received one in time independent of where they differ.
    /// An empty/null expected password accepts any input.
    /// </summary>
    /// <param name="expected">The password set by the host.</param>
    /// <param name="received">The password sent by the client.</param>
    /// <returns>True if the client may connect.</returns>
    public static bool PasswordMatches(string expected, string received) {
        expected = TrimPassword(expected);
        if (expected.Length == 0) {
            return true;
        }

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(received ?? string.Empty);

        var diff = a.Length ^ b.Length;
        for (var i = 0; i < a.Length; i++) {
            diff |= a[i] ^ (i < b.Length ? b[i] : 0);
        }

        return diff == 0;
    }

    /// <summary>
    /// Normalize a password: null becomes empty, and it is cut to <see cref="MaxPasswordLength"/> characters.
    /// </summary>
    public static string TrimPassword(string password) {
        if (string.IsNullOrEmpty(password)) {
            return string.Empty;
        }

        return password.Length > MaxPasswordLength ? password.Substring(0, MaxPasswordLength) : password;
    }

    /// <summary>
    /// Short bilingual (English / Spanish) description of a handshake result for the UI.
    /// </summary>
    public static string Describe(SteamBridgeResult result) {
        return result switch {
            SteamBridgeResult.Accepted => "Accepted / Aceptado",
            SteamBridgeResult.WrongPassword => "Wrong password / Contraseña incorrecta",
            SteamBridgeResult.ServerFull => "Host is full / Anfitrión lleno",
            SteamBridgeResult.VersionMismatch => "Different HKMP version / Versión distinta",
            SteamBridgeResult.NotHosting => "Not hosting / No está alojando",
            SteamBridgeResult.Malformed => "Invalid handshake / Saludo inválido",
            SteamBridgeResult.TimedOut => "Timed out / Tiempo agotado",
            _ => "Unknown error / Error desconocido"
        };
    }

    private static bool StartsWith(byte[] data, byte[] prefix) {
        for (var i = 0; i < prefix.Length; i++) {
            if (data[i] != prefix[i]) {
                return false;
            }
        }

        return true;
    }
}

