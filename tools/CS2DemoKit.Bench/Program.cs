using System.Diagnostics;
using System.Runtime;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

// One measured demo load, emitted as a CSV row, then exit. Driven by run-baseline.sh.
//
// A fresh process per measurement is the point: no cross-run heap state, no allocator history,
// nothing carried between runs. The warm-up below runs a full discarded pipeline so no timed
// phase pays JIT, and each phase is preceded by a forced blocking collection so a collection owed
// to earlier garbage cannot land inside the window being measured.
//
// Deliberately uses only long-standing public API, so the same source can be published from two
// checkouts and used to compare them.
//
// usage: CS2DemoKit.Bench <demo.dem> <variant-label> <run-index>

string demoPath = args[0];
string variant = args[1];
string runIndex = args[2];

byte[] bytes = File.ReadAllBytes(demoPath);
var rules = YamlConfigLoader.LoadShippedEmbedded();

Profiling.Enabled = true;

static double Ms(long t) => (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;

static void Settle()
{
    GC.Collect(2, GCCollectionMode.Forced, true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, true);
}

// Warm-up: full pipeline once, discarded, so no timed phase pays JIT.
{
    ParsedDemo w = MemoryMappedDemoSource.ParseFile(demoPath);
    BuildResult wb = DemoAnalysis.Build(w, rules.Rulesets);
    _ = DemoAnalysis.Evaluate(w, wb);
}

Settle();
long memBefore = GC.GetTotalMemory(true);
long allocBefore = GC.GetTotalAllocatedBytes();
TimeSpan pauseBefore = GC.GetTotalPauseDuration();
int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

long t = Stopwatch.GetTimestamp();
ParsedDemo demo = DemoParser.Parse(bytes.AsMemory());
double parseMs = Ms(t);

double parsePause = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
long parseAlloc = GC.GetTotalAllocatedBytes() - allocBefore;
int p0 = GC.CollectionCount(0) - g0, p1c = GC.CollectionCount(1) - g1, p2c = GC.CollectionCount(2) - g2;
ParseProfilingSnapshot snap = ParseProfilingSnapshot.Read();

// Live set with only the ParsedDemo (and the file bytes, which are in memBefore too) reachable.
// The slabs are large objects and a gen2 does not compact the LOH by default, so compact once or
// the figure counts segment slack as live data.
GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
long retained = GC.GetTotalMemory(false) - memBefore;

long innerMessages = 0;
foreach (DemoFrame f in demo.Frames)
{
    innerMessages += f.InnerMessages.Count;
}

Settle();
long tb = Stopwatch.GetTimestamp();
BuildResult build = DemoAnalysis.Build(demo, rules.Rulesets);
double buildMs = Ms(tb);

Settle();
long allocEval = GC.GetTotalAllocatedBytes();
TimeSpan pauseEval = GC.GetTotalPauseDuration();
int e0 = GC.CollectionCount(0), e1 = GC.CollectionCount(1), e2 = GC.CollectionCount(2);
long te = Stopwatch.GetTimestamp();
_ = DemoAnalysis.Evaluate(demo, build);
double evalMs = Ms(te);
double evalPause = (GC.GetTotalPauseDuration() - pauseEval).TotalMilliseconds;
long evalAlloc = GC.GetTotalAllocatedBytes() - allocEval;
int ev0 = GC.CollectionCount(0) - e0, ev1 = GC.CollectionCount(1) - e1, ev2 = GC.CollectionCount(2) - e2;

// Full enumeration of InnerMessages across every frame: what a consumer walking the message
// list actually pays. Under a lazy view this is where synthesis lands, so it is the number that
// decides whether "transient garbage is cheap" holds on a real traversal.
Settle();
long allocEnum = GC.GetTotalAllocatedBytes();
TimeSpan pauseEnum = GC.GetTotalPauseDuration();
long ten = Stopwatch.GetTimestamp();
long walked = 0;
foreach (DemoFrame f in demo.Frames)
{
    // foreach, the path a consumer normally takes and the one the composed view optimises.
    foreach (NetMessage msg in f.InnerMessages)
    {
        if (msg.Payload is CSVCMsg_PacketEntities)
        {
            walked++;
        }
    }
}
double enumMs = Ms(ten);
double enumPause = (GC.GetTotalPauseDuration() - pauseEnum).TotalMilliseconds;
long enumAlloc = GC.GetTotalAllocatedBytes() - allocEnum;

GC.KeepAlive(demo);
GC.KeepAlive(walked);

const double MB = 1024.0 * 1024;
Console.WriteLine(string.Join(",",
    variant, Path.GetFileName(demoPath), runIndex,
    parseMs.ToString("F2"),
    (snap.Pass1HeaderTicks * 1000.0 / Stopwatch.Frequency).ToString("F2"),
    (snap.Pass2WallTicks * 1000.0 / Stopwatch.Frequency).ToString("F2"),
    (snap.Pass3EnrichTicks * 1000.0 / Stopwatch.Frequency).ToString("F2"),
    parsePause.ToString("F2"),
    (parseAlloc / MB).ToString("F1"),
    (retained / MB).ToString("F1"),
    p0, p1c, p2c,
    buildMs.ToString("F2"),
    evalMs.ToString("F2"),
    evalPause.ToString("F2"),
    (evalAlloc / MB).ToString("F1"),
    snap.FrameCount, innerMessages,
    enumMs.ToString("F2"),
    (enumAlloc / MB).ToString("F1"),
    enumPause.ToString("F2"),
    walked,
    ev0, ev1, ev2));
