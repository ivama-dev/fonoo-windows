using Fonoo.Windows.Desktop;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
Check(PcmLevel.Measure([]) == default && PcmLevel.Measure(new float[480]) == default, "Silence must not show microphone activity.");
Check(PcmLevel.Measure([float.NaN, float.PositiveInfinity, float.NegativeInfinity]) == default, "Non-finite audio must not contaminate the meter.");
var quiet = PcmLevel.Measure([.001f, -.001f]);
Check(quiet.Rms < .001 && quiet.Peak < .001, "-60 dB floor.");
var tone = Enumerable.Range(0, 480).Select(i => (float)(.25 * Math.Sin(i * 2 * Math.PI / 48))).ToArray();
var reading = PcmLevel.Measure(tone);
Check(Math.Abs(reading.Rms - .74914) < .001 && Math.Abs(reading.Peak - .79931) < .001, "Known sine RMS and sample peak must differ by 3 dB.");
var impulse = new float[480]; impulse[200] = 1;
var transient = PcmLevel.Measure(impulse);
Check(transient.Peak == 1 && transient.Rms < .6, "A short transient must appear in the peak indicator without pretending the average level is full scale.");
Check(PcmLevel.Measure([2f, -2f]) == new PcmLevel(1, 1), "Over-range PCM must clamp safely.");
Check(PcmLevel.Measure(new float[480]) == default, "A previous loud frame must not leak into the next measurement.");
Console.WriteLine("PASS: PCM silence, invalid samples, dB floor, sine RMS, transient peaks and over-range safety.");
