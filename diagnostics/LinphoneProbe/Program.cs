using Linphone;
using Fonoo.Windows.Telephony;

if (args.Contains("--calls"))
{
    try { await Fonoo.Windows.Diagnostics.CallChecks.RunAsync(); return 0; }
    catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.Message); return 1; }
}

if (args.Contains("--audio"))
{
    for (var cycle = 0; cycle < 3; cycle++)
    {
        var engine = new SipEngine(null);
        try
        {
            await engine.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            var state = await engine.GetAudioAsync(true).WaitAsync(TimeSpan.FromSeconds(10));
            if (state.InCall || state.Testing || state.Level != 0) throw new Exception("Unexpected media activity.");
            if (state.InputId is not null && !state.Inputs.Any(d => d.Id == state.InputId)) throw new Exception("Invalid input route.");
            if (state.OutputId is not null && !state.Outputs.Any(d => d.Id == state.OutputId)) throw new Exception("Invalid output route.");
            var rejected = false;
            try { await engine.SelectAudioAsync(true, "fonoo-nonexistent-test-device"); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid device was accepted.");
            await engine.TestAudioAsync(false);
            Console.WriteLine($"Audio cycle {cycle + 1}: inputs={state.Inputs.Length}, outputs={state.Outputs.Length}; routing, refresh and invalid-device rejection passed.");
        }
        finally { await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        try { await engine.GetAudioAsync(); throw new Exception("Stopped engine accepted work."); }
        catch (InvalidOperationException) { }
    }
    Console.WriteLine("PASS: production audio engine lifecycle; no account, capture or call started.");
    return 0;
}

Exception? failure = null;
var worker = new Thread(() =>
{
    try
    {
        var resources = Path.Combine(AppContext.BaseDirectory, "share");
        if (!File.Exists(Path.Combine(resources, "belr", "grammars", "vcard_grammar.belr")))
            throw new FileNotFoundException("Required Linphone grammar resources are missing.");

        var factory = Factory.Instance;
        factory.TopResourcesDir = resources;
        factory.MspluginsDir = Path.Combine(AppContext.BaseDirectory, "mediastreamer", "plugins");
        var storage = Path.Combine(AppContext.BaseDirectory, "probe-data");
        Directory.CreateDirectory(storage);
        factory.DataDir = storage;
        factory.ConfigDir = storage;
        factory.CacheDir = storage;

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            // Ephemeral configuration: no accounts, credentials, database or listening SIP transports.
            var config = factory.CreateConfigFromString("""
                [sip]
                store_auth_info=0
                sip_port=0
                sip_tcp_port=0
                sip_tls_port=0
                [storage]
                uri=null
                call_logs_db_uri=null
                [video]
                enabled=0
                capture=0
                display=0
                """);
            var core = factory.CreateCoreWithConfig(config, IntPtr.Zero);
            try
            {
                core.AutoIterateEnabled = false;
                core.VideoCaptureEnabled = false;
                core.VideoDisplayEnabled = false;
                core.NetworkReachable = false;
                core.Start();
                for (var i = 0; i < 50; i++)
                {
                    core.Iterate();
                    Thread.Sleep(20);
                }
                var devices = core.ExtendedAudioDevices.ToArray();
                var inputs = devices.Count(d => d.HasCapability(AudioDeviceCapabilities.CapabilityRecord));
                var outputs = devices.Count(d => d.HasCapability(AudioDeviceCapabilities.CapabilityPlay));
                Console.WriteLine($"Cycle {cycle}: SDK {Core.Version}, {devices.Length} audio devices, capture={inputs}, playback={outputs}");
            }
            finally
            {
                core.Stop();
            }
            Console.WriteLine($"Cycle {cycle}: stopped");
        }
    }
    catch (Exception ex)
    {
        failure = ex;
    }
}) { Name = "Fonoo.SipProbe", IsBackground = true };

worker.Start();
if (!worker.Join(TimeSpan.FromSeconds(30)))
{
    Console.Error.WriteLine("Engine lifecycle probe exceeded 30 seconds.");
    return 2;
}
if (failure is not null)
{
    Console.Error.WriteLine(failure);
    return 1;
}
return 0;
