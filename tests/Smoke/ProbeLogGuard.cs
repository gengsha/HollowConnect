using System;
using System.IO;
using Hkmp.Logging;
using UnityEngine;

namespace HollowConnect.Smoke;

// Every mode uses the same gate, including callbacks on networking threads.
// Kept out of the production payload. Native UI warnings are still recorded
// by Unity; only mod exceptions and engine errors reject acceptance here.
internal sealed class ProbeLogGuard : Hkmp.Logging.ILogger {
    private readonly string root, role;
    private readonly object gate = new();
    private ProbeLogGuard(string root, string role) { this.root=root;this.role=role; }
    internal static void Install(string root, string role) {
        var guard=new ProbeLogGuard(root,role);
        Hkmp.Logging.Logger.AddLogger(guard);
        Application.logMessageReceivedThreaded += guard.UnityLog;
    }
    private void Fail(string source, string text) {
        lock(gate) {
            File.AppendAllText(Path.Combine(root,role+"-guard.log"), DateTime.UtcNow.ToString("O")+" ERROR "+source+" "+text+"\n");
            File.WriteAllText(Path.Combine(root,role+".failed"),"strict log gate: "+source);
        }
    }
    private static bool ModException(string text) =>
        (text.IndexOf("Exception",StringComparison.OrdinalIgnoreCase)>=0 &&
         (text.IndexOf("Hkmp.",StringComparison.OrdinalIgnoreCase)>=0 || text.IndexOf("HollowConnect.",StringComparison.OrdinalIgnoreCase)>=0)) ||
        text.IndexOf("Spawn expired",StringComparison.OrdinalIgnoreCase)>=0;
    private void UnityLog(string message,string stack,LogType type) {
        var text=message+"\n"+stack;
        if(ModException(text))Fail("Unity",text);
    }
    public void Error(string message) => Fail("HKMP",message);
    public void Warn(string message) { if(ModException(message))Fail("HKMP warning",message); }
    public void Info(string message) { if(ModException(message))Fail("HKMP info",message); }
    public void Fine(string message) { }
    public void Debug(string message) { }
}
