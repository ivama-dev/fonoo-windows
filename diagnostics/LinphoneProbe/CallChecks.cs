using Fonoo.Windows.Telephony;
using Linphone;
using System.Collections.Concurrent;

namespace Fonoo.Windows.Telephony
{
    // Compiled ONLY into this diagnostic executable, never the Windows application.
    public sealed partial class SipEngine
    {
        public bool TraceEnabled;
        public string TraceLabel = "";
        public async Task<int> ConfigureLoopbackAsync(string wav)
        {
            var port = 0;
            await Enqueue(() =>
            {
                core!.UseFiles = true; core.PlayFile = wav; core.Ring = null!; core.Ringback = null!;
                core.Config.SetString("sip", "bind_address", "127.0.0.1");
                core.PrimaryContact = "sip:fonoo-check@127.0.0.1";
                var transports = Factory.Instance.CreateTransports(); transports.UdpPort = -1; transports.TcpPort = transports.TlsPort = 0;
                core.Transports = transports; core.NetworkReachable = true;
                port = core.TransportsUsed.UdpPort;
                core.PrimaryContact = "sip:fonoo-check-" + port + "@127.0.0.1:" + port;
                registered = true;
            });
            return port;
        }
        public Task PointAtLoopbackAsync(int port) => Enqueue(() => domain = "127.0.0.1:" + port);
    }
}
namespace Fonoo.Windows.Diagnostics
{
    public static class CallChecks
    {
        public static async Task RunAsync()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "call-checks"); Directory.CreateDirectory(folder);
            var wav = Path.Combine(folder, "synthetic.wav"); WriteWave(wav);
            var a = new SipEngine(null); var b = new SipEngine(null); var c = new SipEngine(null);
            SipSnapshot sa = new(false, false, false, false, "", ""), sb = sa, sc = sa;
            var archived = new ConcurrentQueue<CallHistoryEntry>();
            a.Changed += s => Volatile.Write(ref sa, s); b.Changed += s => Volatile.Write(ref sb, s); c.Changed += s => Volatile.Write(ref sc, s);
            a.CallEnded += archived.Enqueue;
            try
            {
                await Task.WhenAll(a.Ready, b.Ready, c.Ready).WaitAsync(TimeSpan.FromSeconds(15));
                var pa = await a.ConfigureLoopbackAsync(wav); var pb = await b.ConfigureLoopbackAsync(wav); var pc = await c.ConfigureLoopbackAsync(wav);
                if (new[] { pa, pb, pc }.Any(p => p <= 0)) throw new Exception("Loopback SIP transports not listening.");
                await a.PointAtLoopbackAsync(pb); await b.PointAtLoopbackAsync(pa); await c.PointAtLoopbackAsync(pa);
                await a.DialAsync("101"); await Wait(() => Volatile.Read(ref sb).Incoming, "incoming"); await b.AnswerAsync();
                await Wait(() => Volatile.Read(ref sa).Active && Volatile.Read(ref sb).Active, "SRTP connected");
                await a.MuteAsync(true); Check(sa.Muted, "Mute state"); await a.MuteAsync(false); await a.DtmfAsync('5');
                await a.HoldAsync(true); await Wait(() => Volatile.Read(ref sa).Held, "held");
                await a.HoldAsync(false); await Wait(() => Volatile.Read(ref sa).Active && !Volatile.Read(ref sa).HoldPending, "resumed");
                Console.WriteLine("PASS: incoming, answer, SRTP media, mute, DTMF, hold and resume using synthetic audio on loopback.");
                // A consultation must not invite until original hold is confirmed.
                await a.PointAtLoopbackAsync(pc); await a.TransferAsync("102", true);
                await Wait(() => Volatile.Read(ref sc).Incoming && Volatile.Read(ref sa).Held, "consultation after hold"); await c.AnswerAsync();
                await Wait(() => Volatile.Read(ref sa).ConsultationActive, "consultation connected");
                await a.ReturnToOriginalAsync(); await Wait(() => !Volatile.Read(ref sa).Consulting && Volatile.Read(ref sa).Active, "return original");
                await a.HangUpAsync(); await Wait(() => !Volatile.Read(ref sa).InCall && !Volatile.Read(ref sb).InCall, "end all");
                Check(archived.Count == 2 && archived.All(e => e.Outcome == "completed"), "Original and consultation archived once");
                a.TraceLabel = "A"; b.TraceLabel = "B"; c.TraceLabel = "C"; a.TraceEnabled = b.TraceEnabled = c.TraceEnabled = Environment.GetCommandLineArgs().Contains("--trace");

                // Attended transfer: B and C continue talking after A leaves.
                await a.PointAtLoopbackAsync(pb); await a.DialAsync("101"); await Wait(() => sb.Incoming, "transfer incoming"); await b.AnswerAsync(); await Wait(() => sa.Active, "transfer original active");
                await a.PointAtLoopbackAsync(pc); await a.TransferAsync("102", true); await Wait(() => sc.Incoming, "transfer consultation incoming"); await c.AnswerAsync(); await Wait(() => sa.ConsultationActive && sa.Held, "transfer consultation active");
                await a.CompleteTransferAsync(); await Wait(() => !sa.InCall && sb.Active && sc.Active, "attended transfer confirmation");
                await b.HangUpAsync(); await Wait(() => !sb.InCall && !sc.InCall, "attended peers end");
                Console.WriteLine("PASS: attended transfer confirmed; original caller and consultation peer remain connected.");
                // Blind transfer: original party follows REFER and destination rings.
                await a.PointAtLoopbackAsync(pb); await a.DialAsync("101"); await Wait(() => sb.Incoming, "blind original incoming"); await b.AnswerAsync(); await Wait(() => sa.Active, "blind original active");
                await a.PointAtLoopbackAsync(pc); await a.TransferAsync("102", false); await Wait(() => sc.Incoming, "blind destination incoming"); await c.AnswerAsync();
                await Wait(() => !sa.InCall && sb.Active && sc.Active, "blind transfer confirmation"); await b.HangUpAsync(); await Wait(() => !sb.InCall && !sc.InCall, "blind peers end");
                Console.WriteLine("PASS: blind transfer confirmed; peers continue after transferor leaves.");
                // Failed REFER must retain both parties' original call controls.
                await c.SetDoNotDisturbAsync(true); await a.PointAtLoopbackAsync(pb); await a.DialAsync("101"); await Wait(() => sb.Incoming, "failure original incoming"); await b.AnswerAsync(); await Wait(() => sa.Active, "failure original active");
                await a.PointAtLoopbackAsync(pc); await a.TransferAsync("102", false);
                await Wait(() => sa.InCall && sa.Active && !sa.TransferPending && sb.InCall && sb.Active, "failed transfer restores original");
                await a.HangUpAsync(); await Wait(() => !sa.InCall && !sb.InCall, "failed transfer end"); await c.SetDoNotDisturbAsync(false);
                Console.WriteLine("PASS: rejected transfer preserves the original conversation and controls.");
                // Cancelling while hold is in progress must restore the original later.
                await a.PointAtLoopbackAsync(pb); await a.DialAsync("101"); await Wait(() => sb.Incoming, "second incoming"); await b.AnswerAsync(); await Wait(() => sa.Active, "second active");
                await a.PointAtLoopbackAsync(pc); await a.TransferAsync("102", true); await a.ReturnToOriginalAsync();
                await Wait(() => sa.Active && !sa.Consulting && !sa.HoldPending, "cancel consultation");
                await a.HangUpAsync(); await Wait(() => !sa.InCall && !sb.InCall, "second end");
                // DND rejects a new call and records it without replacing an active session.
                await a.SetDoNotDisturbAsync(true); await b.DialAsync("101"); await Wait(() => !sb.InCall, "DND declined");
                Check(archived.Any(e => e.Incoming && e.Outcome == "declined"), "DND history");
                Console.WriteLine("PASS: consultation, cancellation, return to original, DND and history. No microphone or production account used.");
            }
            finally { await Task.WhenAll(a.StopAsync(), b.StopAsync(), c.StopAsync()).WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        private static void Check(bool value, string label) { if (!value) throw new Exception(label); }
        private static async Task Wait(Func<bool> test, string label)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
            while (!test()) { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(label); await Task.Delay(40); }
        }
        private static void WriteWave(string path)
        {
            const int rate = 16000, samples = rate * 20;
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
            for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 5000));
        }
    }
}
