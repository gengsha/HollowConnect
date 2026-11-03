using System;
using System.Collections.Generic;
using Hkmp.Networking.Packet;

namespace Hkmp.Game.Client.Entity.Component;

// A complete presentation subtree. It can replace an older snapshot in a
// reliable queue or late-join cache without depending on a missing delta.
internal sealed class PresentationState {
    internal const int MaxNodes = 256, MaxComponents = 16, MaxBytes = 60000;
    internal uint Revision;
    internal readonly List<Node> Nodes = new();
    internal sealed class Node {
        internal string Path = "", Parent = "";
        internal bool Gone, Active, OwnPose;
        internal Vector Position, Rotation, Scale = new(1, 1, 1);
        internal readonly List<Renderer> Renderers = new();
        internal readonly List<Sprite> Sprites = new();
        internal readonly List<byte> Particles = new();
    }
    internal struct Vector {
        internal float X, Y, Z;
        internal Vector(float x, float y, float z) { X=x; Y=y; Z=z; }
    }
    internal struct Tint {
        internal float R, G, B, A;
        internal Tint(float r, float g, float b, float a) { R=r; G=g; B=b; A=a; }
    }
    internal sealed class Renderer {
        internal bool Enabled;
        internal byte Colors;
        internal Tint Color, Material, TintColor;
    }
    internal sealed class Sprite {
        internal int Frame;
        internal Tint Color;
        internal string Clip = "";
        internal uint Playback;
        internal byte Wrap;
        internal bool Enabled, Playing;
        internal float Time, Fps;
    }

    private static void WriteVector(Packet p, Vector v) { p.Write(v.X); p.Write(v.Y); p.Write(v.Z); }
    private static void WriteTint(Packet p, Tint c) { p.Write(c.R); p.Write(c.G); p.Write(c.B); p.Write(c.A); }
    private static float Number(Packet p, float limit) {
        var n=p.ReadFloat();
        if(float.IsNaN(n)||float.IsInfinity(n)||System.Math.Abs(n)>limit)throw new FormatException("Invalid presentation number");
        return n;
    }
    private static Vector ReadVector(Packet p)=>new(Number(p,100000),Number(p,100000),Number(p,100000));
    private static Tint ReadTint(Packet p)=>new(Number(p,100),Number(p,100),Number(p,100),Number(p,100));
    private static string Text(Packet p, int limit) {
        var s=p.ReadString();if(s.Length>limit)throw new FormatException("Invalid presentation identifier");return s;
    }
    private static int Count(Packet p) {
        var n=p.ReadByte();if(n>MaxComponents)throw new FormatException("Too many presentation components");return n;
    }
    internal void WriteBody(Packet p, bool timing) {
        if(Nodes.Count>MaxNodes)throw new FormatException("Too many presentation nodes");
        p.Write((ushort)Nodes.Count);
        foreach(var n in Nodes) {
            p.Write(n.Path);p.Write(n.Gone);if(n.Gone)continue;
            p.Write(n.Active);p.Write(n.OwnPose);
            if(n.OwnPose){p.Write(n.Parent);WriteVector(p,n.Position);WriteVector(p,n.Rotation);WriteVector(p,n.Scale);}
            if(n.Renderers.Count>MaxComponents||n.Sprites.Count>MaxComponents||n.Particles.Count>MaxComponents)
                throw new FormatException("Too many presentation components");
            p.Write((byte)n.Renderers.Count);
            foreach(var r in n.Renderers){p.Write(r.Enabled);p.Write(r.Colors);if((r.Colors&1)!=0)WriteTint(p,r.Color);if((r.Colors&2)!=0)WriteTint(p,r.Material);if((r.Colors&4)!=0)WriteTint(p,r.TintColor);}
            p.Write((byte)n.Sprites.Count);
            foreach(var s in n.Sprites) {
                // Moving frames/clock do not count as state transitions. A
                // budgeted heartbeat carries the live clock for drift repair.
                p.Write(!timing&&s.Playing?-1:s.Frame);WriteTint(p,s.Color);
                p.Write(s.Clip);p.Write(s.Playback);p.Write(s.Wrap);p.Write(s.Enabled);p.Write(s.Playing);
                p.Write(timing?s.Time:0);p.Write(s.Fps);
            }
            p.Write((byte)n.Particles.Count);foreach(var b in n.Particles)p.Write(b);
        }
    }
    private static bool Same(Vector a,Vector b)=>a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;
    private static bool Same(Tint a,Tint b)=>a.R==b.R&&a.G==b.G&&a.B==b.B&&a.A==b.A;
    internal bool SamePresentation(PresentationState other) {
        if(other==null||Nodes.Count!=other.Nodes.Count)return false;
        for(var i=0;i<Nodes.Count;i++) {
            var a=Nodes[i];var b=other.Nodes[i];
            if(a.Path!=b.Path||a.Gone!=b.Gone)return false;
            if(a.Gone)continue;
            if(a.Active!=b.Active||a.OwnPose!=b.OwnPose)return false;
            if(a.OwnPose&&(a.Parent!=b.Parent||!Same(a.Position,b.Position)||!Same(a.Rotation,b.Rotation)||!Same(a.Scale,b.Scale)))return false;
            if(a.Renderers.Count!=b.Renderers.Count||a.Sprites.Count!=b.Sprites.Count||a.Particles.Count!=b.Particles.Count)return false;
            for(var k=0;k<a.Renderers.Count;k++) {
                var x=a.Renderers[k];var y=b.Renderers[k];
                if(x.Enabled!=y.Enabled||x.Colors!=y.Colors||
                    (x.Colors&1)!=0&&!Same(x.Color,y.Color)||(x.Colors&2)!=0&&!Same(x.Material,y.Material)||
                    (x.Colors&4)!=0&&!Same(x.TintColor,y.TintColor))return false;
            }
            for(var k=0;k<a.Sprites.Count;k++) {
                var x=a.Sprites[k];var y=b.Sprites[k];
                if(x.Clip!=y.Clip||x.Playback!=y.Playback||x.Wrap!=y.Wrap||x.Enabled!=y.Enabled||x.Playing!=y.Playing||x.Fps!=y.Fps||
                    !Same(x.Color,y.Color)||!x.Playing&&x.Frame!=y.Frame)return false;
            }
            for(var k=0;k<a.Particles.Count;k++)if(a.Particles[k]!=b.Particles[k])return false;
        }
        return true;
    }
    internal Packet Encode() {
        var body=new Packet();WriteBody(body,true);
        if(body.Length+4>MaxBytes)throw new FormatException("Presentation snapshot exceeds its bound");
        var result=new Packet();result.Write(Revision);result.Write(CombatStatePayload.Encode(body).ToArray());return result;
    }
    internal static uint ReadRevision(Packet p) {
        var bytes=p.ToArray();if(bytes.Length<6||bytes.Length>MaxBytes)throw new FormatException("Invalid presentation size");
        return BitConverter.ToUInt32(bytes,0);
    }
    internal static PresentationState Decode(Packet payload) {
        // Validate the entire snapshot before touching Unity objects.
        var result=new PresentationState{Revision=ReadRevision(payload)};
        var bytes=payload.ToArray();var p=CombatStatePayload.Decode(new Packet(SubArray(bytes,4)),out var count);
        if(count>MaxNodes)throw new FormatException("Too many presentation nodes");
        var paths=new HashSet<string>(StringComparer.Ordinal);
        for(var i=0;i<count;i++) {
            var n=new Node{Path=Text(p,2048),Gone=p.ReadBool()};
            if(!paths.Add(n.Path))throw new FormatException("Duplicate presentation node");
            result.Nodes.Add(n);if(n.Gone)continue;
            n.Active=p.ReadBool();n.OwnPose=p.ReadBool();
            if(n.OwnPose){if(n.Path.Length==0)throw new FormatException("Presentation cannot own root motion");n.Parent=Text(p,2048);n.Position=ReadVector(p);n.Rotation=ReadVector(p);n.Scale=ReadVector(p);}
            var rs=Count(p);for(var k=0;k<rs;k++) {
                var r=new Renderer{Enabled=p.ReadBool(),Colors=p.ReadByte()};
                if(r.Colors>7)throw new FormatException("Invalid presentation color flags");
                if((r.Colors&1)!=0)r.Color=ReadTint(p);if((r.Colors&2)!=0)r.Material=ReadTint(p);if((r.Colors&4)!=0)r.TintColor=ReadTint(p);n.Renderers.Add(r);
            }
            var ss=Count(p);for(var k=0;k<ss;k++) {
                var s=new Sprite{Frame=p.ReadInt(),Color=ReadTint(p),Clip=Text(p,128),Playback=p.ReadUInt(),Wrap=p.ReadByte(),Enabled=p.ReadBool(),Playing=p.ReadBool(),Time=Number(p,1000000),Fps=Number(p,1000)};
                if(s.Frame< -1||s.Frame>65535||s.Wrap>6||s.Time<0||s.Fps<0||s.Playing&&(s.Clip.Length==0||s.Fps==0))throw new FormatException("Invalid presentation animation");
                n.Sprites.Add(s);
            }
            var ps=Count(p);for(var k=0;k<ps;k++){var b=p.ReadByte();if(b>3)throw new FormatException("Invalid presentation particle state");n.Particles.Add(b);}
        }
        if(p.Remaining!=0)throw new FormatException("Trailing presentation bytes");
        return result;
    }
    private static byte[] SubArray(byte[] bytes,int offset){var result=new byte[bytes.Length-offset];Buffer.BlockCopy(bytes,offset,result,0,result.Length);return result;}
}

internal sealed class PresentationOrder {
    private bool hasValue;
    private uint epoch, revision;
    internal bool Accept(uint nextEpoch,uint nextRevision) {
        if(hasValue&&epoch==nextEpoch&&unchecked((int)(nextRevision-revision))<=0)return false;
        epoch=nextEpoch;revision=nextRevision;hasValue=true;return true;
    }
    internal void Clear(){hasValue=false;}
}

// Shared per client, never per entity: an arena with many animators must obey
// the same aggregate budget. A large indivisible snapshot borrows one slot.
internal sealed class PresentationBudget {
    internal const float BytesPerSecond=32768, Burst=8192;
    private float tokens=Burst, last;
    internal bool Spend(float now,int bytes) {
        tokens=System.Math.Min(Burst,tokens+System.Math.Max(0,now-last)*BytesPerSecond);last=now;
        // Include entity ID, scene/visit/epoch, component framing and carrier
        // overhead. Counting tiny payloads alone understates their wire cost.
        if(bytes<0||bytes>PresentationState.MaxBytes)return false;
        var cost=bytes+96;
        if(tokens<System.Math.Min(cost,Burst))return false;
        tokens-=cost;return true;
    }
}
