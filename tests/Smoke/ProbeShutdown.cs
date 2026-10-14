using System;
using System.Collections;
using System.IO;

namespace HollowConnect.Smoke;

// Complete network teardown before a launcher may remove either game process.
internal static class ProbeShutdown {
    internal static IEnumerator Run(Hkmp.Game.GameManager engine, string root, string role) {
        var peer=role=="host"?"guest":"host";
        File.WriteAllText(Path.Combine(root,role+".shutdown-ready"),"ready");
        yield return Wait(()=>File.Exists(Path.Combine(root,peer+".shutdown-ready")),"both probes finished");
        engine.ClientManager.Disconnect();
        File.WriteAllText(Path.Combine(root,role+".shutdown-offline"),"offline");
        yield return Wait(()=>File.Exists(Path.Combine(root,peer+".shutdown-offline")),"both clients disconnected");
        if(role=="host") {
            yield return Wait(()=>engine.ServerManager.Players.Count==0,"server acknowledged disconnects");
            engine.ServerManager.Stop();
            File.WriteAllText(Path.Combine(root,"shutdown-server-stopped"),"stopped");
        }
        yield return Wait(()=>File.Exists(Path.Combine(root,"shutdown-server-stopped")),"server stopped");
        ProbeNetworkFaults.Verify(root, role);
        IEnumerator Wait(Func<bool> ready,string label) {
            var end=UnityEngine.Time.realtimeSinceStartup+15;
            while(!ready()&&UnityEngine.Time.realtimeSinceStartup<end)yield return null;
            if(!ready()) {
                File.WriteAllText(Path.Combine(root,role+".failed"),"Final shutdown timeout: "+label);
                throw new TimeoutException("Final shutdown: "+label);
            }
        }
    }
}
