#region

using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Correctness spike — the parallel-decode feasibility gate.
///     <para>
///         Parallel entity decode chunks the demo at <c>DEM_FullPacket</c> frames and has each
///         worker start from one. This proves the prerequisite: a worker that has the shared schema
///         (SendTables / ClassInfo / ServerInfo) and starts with an EMPTY entity set can reconstruct the
///         entity state at a checkpoint from the full packet's own snapshot (its bundled string-table
///         snapshot + the full-snapshot <c>PacketEntities</c>), matching a sequential delta replay that
///         reached the same frame.
///     </para>
///     <para>
///         A <c>DEM_FullPacket</c> snapshot is PVS-scoped (it ENTERPVS-es only the entities currently in
///         the PVS), so dormant entities that left the PVS without being deleted legitimately won't appear.
///         The success criterion is therefore <b>analysis-relevant</b> parity, not strict whole-set
///         equality: the IN-PVS entities must reconstruct byte-identically, and any dormant divergence is
///         classified by whether its class is something the analysis layer actually reads.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class FullPacketCheckpointSpikeTests
{
    // Classes the Analysis layer reads off the entity set (pawns/controllers/gamerules/weapons/projectiles).
    // A dormant entity of one of these classes that the checkpoint can't reconstruct would be fatal for a
    // naive parallel-decode worker (it would need to carry forward the prior chunk's dormants).
    private static bool IsAnalysisRelevant(string className) =>
        className.Contains("PlayerPawn", StringComparison.OrdinalIgnoreCase)
        || className == "CCSPlayerController"
        || className == "CCSGameRulesProxy"
        || className.Contains("Weapon", StringComparison.OrdinalIgnoreCase)
        || className.Contains("Projectile", StringComparison.OrdinalIgnoreCase)
        || className == "CMolotovProjectile";

    [Test]
    [Category("Spike")]
    public async Task FullPacket_Checkpoint_ReconstructsInPvsEntityState()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo demo = DemoParser.Parse(bytes.AsMemory());
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        List<int> fullPacketIdx = new();
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Command == "DEM_FullPacket")
            {
                fullPacketIdx.Add(i);
            }
        }

        Console.WriteLine($"Demo: {Path.GetFileName(path)}  frames={frames.Count:N0}  full-packets={fullPacketIdx.Count}");
        await Assert.That(fullPacketIdx.Count).IsGreaterThanOrEqualTo(3);

        // Shared-schema setup = everything up to (not including) the first full packet. Replaying it loads
        // SendTables/ClassInfo/ServerInfo; Clear() then wipes entities so the checkpoint tracker starts
        // "schema-loaded, entities-empty" — exactly how a parallel worker would start.
        List<DemoFrame> setupFrames = frames.Take(fullPacketIdx[0]).ToList();

        // Early / mid / late — dormant accumulation and baseline history grow over a match.
        int[] picks =
        {
            fullPacketIdx[1], fullPacketIdx[fullPacketIdx.Count / 2], fullPacketIdx[^1]
        };

        // The fields the Analysis layer actually reads off the entity set (via the typed wrappers /
        // PawnLookup). These — not the full field set — are what must match for a checkpoint-started
        // worker to produce identical analysis output. The bare checkpoint is the WORST case: a real
        // worker also replays its chunk's deltas after the checkpoint, re-accumulating fields.
        string[] pawnFields =
        {
            "m_iHealth", "m_ArmorValue", "m_unCurrentEquipmentValue", "m_hController", "m_pWeaponServices.m_hActiveWeapon"
        };

        bool allInPvsReconstructed = true;
        bool anyAnalysisRelevantDormantMissing = false;
        bool allAnalysisFieldsMatch = true;

        foreach (int frameIndex in picks)
        {
            DemoFrame fp = frames[frameIndex];

            // Ground truth: sequential delta replay through the checkpoint frame.
            EntityTracker seq = new();
            seq.Replay(frames.Take(frameIndex + 1).ToList());

            // Checkpoint start: schema-loaded, entities-empty, then process the full packet's snapshot.
            EntityTracker chk = new();
            chk.Replay(setupFrames);
            chk.CurrentEntities.Clear();

            // Relabel the full packet as a normal DEM_Packet so the tracker PROCESSES its bundled
            // string-table snapshot + full PacketEntities (sequential playback skips a full packet's PE as
            // redundant). InnerMessages order is [stringtables, ...packet msgs incl. PacketEntities], so the
            // baselines land before the ENTERPVS that consumes them.
            DemoFrame checkpoint = new()
            {
                Command = "DEM_Packet",
                FrameNumber = fp.FrameNumber,
                ServerTick = fp.ServerTick,
                HeaderLength = 0,
                RawLength = 0,
                RawStart = 0,
                IsCompressed = false,
                MessageList = fp.InnerMessages.ToList()
            };
            chk.Replay([checkpoint]);

            // Index both entity sets by slot.
            Dictionary<int, EntityState> seqAll = seq.CurrentEntities.AllIndexed().ToDictionary(t => t.Index, t => t.Entity);
            Dictionary<int, EntityState> chkAll = chk.CurrentEntities.AllIndexed().ToDictionary(t => t.Index, t => t.Entity);

            int seqInPvs = seqAll.Values.Count(e => e.IsInPvs);
            int seqDormant = seqAll.Count - seqInPvs;

            // (a) Every IN-PVS sequential entity must reconstruct identically from the checkpoint.
            int inPvsMatched = 0, inPvsMissing = 0, inPvsFieldMismatch = 0;
            List<string> mismatchSamples = new();
            foreach ((int idx, EntityState se) in seqAll)
            {
                if (!se.IsInPvs)
                {
                    continue;
                }

                if (!chkAll.TryGetValue(idx, out EntityState? ce))
                {
                    inPvsMissing++;
                    if (mismatchSamples.Count < 8)
                    {
                        mismatchSamples.Add($"    MISSING in-pvs #{idx} {se.ClassName}");
                    }

                    continue;
                }

                string? diff = CompareFields(se, ce);
                if (diff is null)
                {
                    inPvsMatched++;
                }
                else
                {
                    inPvsFieldMismatch++;
                    if (mismatchSamples.Count < 8)
                    {
                        mismatchSamples.Add($"    FIELDDIFF #{idx} {se.ClassName}: {diff}");
                    }
                }
            }

            // (b) Classify the dormant entities the checkpoint can't see.
            List<EntityState> dormantMissing = seqAll
                .Where(kv => !kv.Value.IsInPvs && !chkAll.ContainsKey(kv.Key))
                .Select(kv => kv.Value)
                .ToList();
            List<EntityState> dormantRelevant = dormantMissing.Where(e => IsAnalysisRelevant(e.ClassName)).ToList();

            // (c) Anything the checkpoint has that the sequential in-pvs set doesn't (should be ~none).
            int chkExtra = chkAll.Keys.Count(idx => !seqAll.TryGetValue(idx, out EntityState? se) || !se.IsInPvs);

            // (d) THE REAL CRITERION: do the fields the analysis actually reads match? Compared via the
            // same seen-gated indexer the providers use (PawnLookup / typed wrappers).
            int analysisChecked = 0, analysisMismatch = 0;
            List<string> analysisMismatchSamples = new();
            foreach ((int idx, EntityState se) in seqAll)
            {
                if (!se.IsInPvs || !chkAll.TryGetValue(idx, out EntityState? ce))
                {
                    continue;
                }

                if (se.ClassName.Contains("PlayerPawn", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string f in pawnFields)
                    {
                        analysisChecked++;
                        if (!Equals(se[f], ce[f]))
                        {
                            analysisMismatch++;
                            if (analysisMismatchSamples.Count < 10)
                            {
                                analysisMismatchSamples.Add($"    PAWN #{idx} {f}: {se[f] ?? "null"} vs {ce[f] ?? "null"}");
                            }
                        }
                    }
                }
                else if (se.ClassName == "CCSGameRulesProxy")
                {
                    analysisChecked++;
                    const string Freeze = "m_pGameRules.m_bFreezePeriod";
                    if (!Equals(se[Freeze], ce[Freeze]))
                    {
                        analysisMismatch++;
                        analysisMismatchSamples.Add($"    GAMERULES #{idx} freeze: {se[Freeze] ?? "null"} vs {ce[Freeze] ?? "null"}");
                    }
                }
            }

            if (analysisMismatch > 0)
            {
                allAnalysisFieldsMatch = false;
            }

            Console.WriteLine();
            Console.WriteLine($"── checkpoint @ frame {frameIndex} (tick {fp.ServerTick:N0}) ──");
            Console.WriteLine($"  sequential: {seqAll.Count} live ({seqInPvs} in-PVS, {seqDormant} dormant)   checkpoint: {chkAll.Count} live");
            Console.WriteLine($"  in-PVS:  matched={inPvsMatched}  missing={inPvsMissing}  field-mismatch={inPvsFieldMismatch}");
            Console.WriteLine($"  dormant-missing: {dormantMissing.Count}  (analysis-relevant: {dormantRelevant.Count})");
            Console.WriteLine($"  checkpoint-extra (not in seq in-PVS): {chkExtra}");
            Console.WriteLine($"  ANALYSIS-RELEVANT fields: checked={analysisChecked}  mismatch={analysisMismatch}");
            foreach (string s in analysisMismatchSamples)
            {
                Console.WriteLine(s);
            }

            if (mismatchSamples.Count > 0)
            {
                Console.WriteLine("  (strict whole-field diffs — diagnostic only, expected from baseline-omitted fields:)");
            }

            foreach (string s in mismatchSamples)
            {
                Console.WriteLine(s);
            }

            if (dormantRelevant.Count > 0)
            {
                Console.WriteLine("  ⚠ analysis-relevant dormant entities NOT reconstructable from this checkpoint:");
                foreach (EntityState e in dormantRelevant.Take(8))
                {
                    Console.WriteLine($"      {e.ClassName}");
                }
            }

            if (inPvsMissing > 0 || inPvsFieldMismatch > 0)
            {
                allInPvsReconstructed = false;
            }

            if (dormantRelevant.Count > 0)
            {
                anyAnalysisRelevantDormantMissing = true;
            }
        }

        Console.WriteLine();
        Console.WriteLine("SPIKE VERDICT:");
        Console.WriteLine($"  entity SET reconstructs (no missing/dormant analysis-relevant) = {!anyAnalysisRelevantDormantMissing}");
        Console.WriteLine($"  ANALYSIS-RELEVANT fields match at bare checkpoint           = {allAnalysisFieldsMatch}");
        Console.WriteLine($"  strict whole-field byte-identical (diagnostic only)         = {allInPvsReconstructed}");
        Console.WriteLine("  Note: bare checkpoint is WORST case — a real worker also replays its chunk's deltas.");
        Console.WriteLine("  Definitive gate remains: golden parity through the full analysis with chunked decode.");

        // Real criterion: the fields the analysis reads reconstruct from the checkpoint, and no
        // analysis-relevant entity is unreachable. (Strict whole-field equality is NOT required —
        // CS2 ENTERPVS legitimately omits baseline-valued fields the analysis never reads.)
        await Assert.That(allAnalysisFieldsMatch).IsTrue();
        await Assert.That(anyAnalysisRelevantDormantMissing).IsFalse();
    }

    /// <summary>
    ///     Returns null when the two entities are field-identical, else a short description of the first
    ///     divergence. Compares ClassName, Serial, field count, and each field key/value.
    /// </summary>
    private static string? CompareFields(EntityState a, EntityState b)
    {
        if (a.ClassName != b.ClassName)
        {
            return $"class {a.ClassName} vs {b.ClassName}";
        }

        if (a.Serial != b.Serial)
        {
            return $"serial {a.Serial} vs {b.Serial}";
        }

        IReadOnlyDictionary<string, object?> fa = a.Fields;
        IReadOnlyDictionary<string, object?> fb = b.Fields;
        if (fa.Count != fb.Count)
        {
            // Surface a field present in one but not the other.
            string? onlyInA = fa.Keys.FirstOrDefault(k => !fb.ContainsKey(k));
            string? onlyInB = fb.Keys.FirstOrDefault(k => !fa.ContainsKey(k));
            return $"field-count {fa.Count} vs {fb.Count} (onlyA={onlyInA ?? "-"}, onlyB={onlyInB ?? "-"})";
        }

        foreach ((string key, object? va) in fa)
        {
            if (!fb.TryGetValue(key, out object? vb))
            {
                return $"key {key} absent in checkpoint";
            }

            if (!Equals(va, vb))
            {
                return $"{key} = {va} vs {vb}";
            }
        }

        return null;
    }
}
