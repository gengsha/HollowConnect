using System;
using System.Linq;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Game.Server;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;

internal static class PresentationTests {
    private static void Check(bool value,string message="presentation invariant failed"){if(!value)throw new Exception(message);}
    private static PresentationState State() {
        var state=new PresentationState{Revision=7};
        var root=new PresentationState.Node{Path="",Active=true};
        root.Sprites.Add(new PresentationState.Sprite{Frame=5,Clip="Roll",Playback=12,Enabled=true,Playing=true,Time=.25f,Fps=24,Color=new(1,1,1,1)});
        var leaf=new PresentationState.Node{Path="Roll%20Effect#0",OwnPose=true,Parent="e:7",Active=false,Position=new(1,2,3)};
        leaf.Renderers.Add(new PresentationState.Renderer{Enabled=false,Colors=2,Material=new(.1f,.2f,.3f,1)});leaf.Particles.Add(0);
        state.Nodes.Add(root);state.Nodes.Add(leaf);return state;
    }
    private static void Rejected(PresentationState s) {
        try{PresentationState.Decode(s.Encode());}catch(FormatException){return;}throw new Exception("malformed state accepted");
    }
    internal static void Register(Action<string,Action> test) {
        test("presentation complete root and decorative leaf roundtrip",()=>{
            var s=PresentationState.Decode(State().Encode());Check(s.Revision==7&&s.Nodes.Count==2);
            var root=s.Nodes[0].Sprites.Single();Check(root.Playback==12&&root.Clip=="Roll"&&root.Playing&&root.Time==.25f);
            var leaf=s.Nodes[1];Check(leaf.OwnPose&&!leaf.Active&&leaf.Position.Y==2&&!leaf.Renderers[0].Enabled&&leaf.Particles[0]==0);
        });
        test("presentation advancing animation does not dirty semantic state",()=>{
            var s=State();var p=new Packet();s.WriteBody(p,false);s.Nodes[0].Sprites[0].Time=30;s.Nodes[0].Sprites[0].Frame=200;
            var q=new Packet();s.WriteBody(q,false);Check(p.ContentEquals(q));Check(s.SamePresentation(PresentationState.Decode(State().Encode())));
            s.Nodes[0].Sprites[0].Playback++;q.Clear();s.WriteBody(q,false);Check(!p.ContentEquals(q),"same clip new activation was lost");Check(!s.SamePresentation(State()));
        });
        test("presentation stop and terminal frame remain recoverable",()=>{
            var s=State();s.Nodes[0].Sprites[0].Playing=false;s.Nodes[0].Sprites[0].Frame=71;
            var p=new Packet();s.WriteBody(p,false);s.Nodes[0].Sprites[0].Frame=72;var q=new Packet();s.WriteBody(q,false);Check(!p.ContentEquals(q));
            var r=PresentationState.Decode(s.Encode()).Nodes[0].Sprites[0];Check(!r.Playing&&r.Frame==72);
        });
        test("presentation snapshot needs no previously received delta",()=>{
            var lost=State();lost.Nodes[1].Active=true;var current=State();current.Revision=90;current.Nodes[1].Active=false;
            var restored=PresentationState.Decode(current.Encode());Check(!restored.Nodes[1].Active&&restored.Nodes[0].Sprites[0].Clip=="Roll");
        });
        test("presentation order rejects late retry duplicate and survives epoch change",()=>{
            var s=new PresentationOrder();Check(s.Accept(2,100));Check(!s.Accept(2,99)&&!s.Accept(2,100));Check(s.Accept(2,101));Check(s.Accept(3,1));
            s.Clear();Check(s.Accept(3,1));Check(s.Accept(4,uint.MaxValue)&&s.Accept(4,0)&&!s.Accept(4,uint.MaxValue));
            var cache=new ServerEntityData();Check(cache.PresentationOrder.Accept(1,12)&&!cache.PresentationOrder.Accept(1,11));
        });
        test("presentation aggregate budget bounds many entities and an oversized state",()=>{
            var budget=new PresentationBudget();var sent=0;
            for(var frame=0;frame<200;frame++)for(var entity=0;entity<80;entity++)if(budget.Spend(frame*.01f,300))sent+=300;
            Check(sent<=PresentationBudget.Burst+PresentationBudget.BytesPerSecond*2);
            budget=new PresentationBudget();Check(budget.Spend(0,16000));Check(!budget.Spend(.01f,100));Check(budget.Spend(.5f,100));Check(!budget.Spend(1,PresentationState.MaxBytes+1));
        });
        test("presentation malformed duplicate path nan and root pose rejected",()=>{
            var s=State();s.Nodes.Add(s.Nodes[1]);Rejected(s);s=State();s.Nodes[0].OwnPose=true;Rejected(s);
            s=State();s.Nodes[0].Sprites[0].Time=float.NaN;Rejected(s);s=State();s.Nodes[1].Particles[0]=4;Rejected(s);
            s=State();var bytes=s.Encode().ToArray().Concat(new byte[]{1}).ToArray();
            try{PresentationState.Decode(new Packet(bytes));throw new Exception("trailing bytes accepted");}catch(FormatException){}
        });
        test("presentation full snapshot retries compact independently from combat and actions",()=>{
            ServerUpdatePacket Batch(ushort seq,int n) {
                var p=new ServerUpdatePacket{Sequence=seq};var u=new ReliableEntityUpdate{Id=8,SceneName="Arena",Visit=2,Epoch=3};u.UpdateTypes.Add(EntityUpdateType.Data);
                foreach(var type in new[]{EntityComponentType.CombatState,EntityComponentType.Presentation,EntityComponentType.Fsm}){
                    var d=new EntityNetworkData{Type=type};d.Packet.Write(n);u.GenericData.Add(d);
                }
                var c=new PacketDataCollection<ReliableEntityUpdate>();c.DataInstances.Add(u);p.SetSendingPacketData(ServerUpdatePacketId.ReliableEntityUpdate,c);return p;
            }
            var p=Batch(8,8);for(ushort i=1;i<8;i++)p.SetLostReliableData(Batch(i,i));var wire=new Packet();p.CreatePacket(wire);
            var read=new ServerUpdatePacket();Check(read.ReadPacket(PacketManager.ReadDatagram(wire.ToArray())));
            var raw=(RawPacketDataCollection)read.GetPacketData()[ServerUpdatePacketId.ReliableEntityUpdate];
            var ds=raw.DataInstances.Cast<ReliableEntityUpdate>().SelectMany(u=>u.GenericData).ToArray();
            Check(ds.Count(d=>d.Type==EntityComponentType.Presentation)==1&&ds.Count(d=>d.Type==EntityComponentType.CombatState)==1&&ds.Count(d=>d.Type==EntityComponentType.Fsm)==8);
        });
    }
}
