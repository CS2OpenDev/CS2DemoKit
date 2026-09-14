#region

using System.Globalization;
using System.Text;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;
using CS2OpenSchema.Events;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Measures, per demo, the facts a forward-only engine cannot look ahead for: the order of the
///     two round-end markers inside a round, roster slots that never reach a materialising event,
///     player-scoped events before the first positive-tick frame, and userinfo renames. Explicit:
///     run it on purpose, read the report, and quote it when a golden moves because of this work.
///     Set <c>CS2DEMOKIT_PROBE_DIR</c> to also write one report file per demo.
/// </summary>
[Explicit]
[NotInParallel]
public class ForwardPathCorpusProbeTests
{
    private const string ProbeDirVariable = "CS2DEMOKIT_PROBE_DIR";

    public static IEnumerable<string> Demos()
    {
        string? sample = DemoTestHelper.FindDemoPath(DemoTestHelper.SampleDemoFileName);
        if (sample is not null)
        {
            yield return sample;
        }

        foreach (string path in RulesOutputGoldenTests.CorpusDemos())
        {
            yield return path;
        }
    }

    [Test]
    [MethodDataSource(nameof(Demos))]
    public async Task Probe(string demoPath)
    {
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory());
        StringBuilder sb = new();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"demo={Path.GetFileName(demoPath)} frames={demo.Frames.Count} events={demo.AllGameEvents.Count} " +
            $"players={demo.Players.Count} profile={demo.Profile.SourceKind} tickRate={demo.TickRate}");

        AppendRoundEndMarkers(sb, demo);
        AppendRoster(sb, demo);
        AppendPreDigestEvents(sb, demo);
        AppendRenames(sb, demo);

        string report = sb.ToString();
        Console.WriteLine(report);

        string? dir = Environment.GetEnvironmentVariable(ProbeDirVariable);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, Path.GetFileNameWithoutExtension(demoPath) + ".probe.txt"), report);
        }
    }

    // (a) Within each round (round_freeze_end to the next), which of the two per-round end markers
    // fires first, and how far apart they are.
    private static void AppendRoundEndMarkers(StringBuilder sb, ParsedDemo demo)
    {
        int roe = 0, cpr = 0, winPanel = 0, cprBeforeFirstFreeze = 0;
        bool seenFreeze = false;
        int roeTick = -1, roeFrame = -1, cprTick = -1, cprFrame = -1;
        int both = 0, roeFirst = 0, cprFirst = 0, sameTick = 0, onlyRoe = 0, onlyCpr = 0, neither = 0, rounds = 0;
        List<int> tickDeltas = [];
        List<int> frameDeltas = [];

        void CloseRound()
        {
            rounds++;
            if (roeTick >= 0 && cprTick >= 0)
            {
                both++;
                if (roeTick < cprTick)
                {
                    roeFirst++;
                }
                else if (roeTick > cprTick)
                {
                    cprFirst++;
                }
                else
                {
                    sameTick++;
                }

                tickDeltas.Add(cprTick - roeTick);
                frameDeltas.Add(cprFrame - roeFrame);
            }
            else if (roeTick >= 0)
            {
                onlyRoe++;
            }
            else if (cprTick >= 0)
            {
                onlyCpr++;
            }
            else
            {
                neither++;
            }

            roeTick = roeFrame = cprTick = cprFrame = -1;
        }

        foreach (GameEvent e in demo.AllGameEvents)
        {
            switch (e.Name)
            {
                case "round_freeze_end":
                    if (seenFreeze)
                    {
                        CloseRound();
                    }

                    seenFreeze = true;
                    break;
                case "round_officially_ended":
                    roe++;
                    if (roeTick < 0)
                    {
                        roeTick = e.GameTick;
                        roeFrame = e.FrameNumber;
                    }

                    break;
                case "cs_pre_restart":
                    cpr++;
                    if (!seenFreeze)
                    {
                        cprBeforeFirstFreeze++;
                    }
                    else if (cprTick < 0)
                    {
                        cprTick = e.GameTick;
                        cprFrame = e.FrameNumber;
                    }

                    break;
                case "cs_win_panel_match":
                    winPanel++;
                    break;
            }
        }

        if (seenFreeze)
        {
            CloseRound();
        }

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"[markers] round_officially_ended={roe} cs_pre_restart={cpr} cs_win_panel_match={winPanel} " +
            $"cs_pre_restart_before_first_freeze={cprBeforeFirstFreeze}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"[markers] rounds={rounds} both={both} roe_first={roeFirst} cpr_first={cprFirst} same_tick={sameTick} " +
            $"only_roe={onlyRoe} only_cpr={onlyCpr} neither={neither}");
        if (tickDeltas.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"[markers] cpr_minus_roe ticks min={tickDeltas.Min()} max={tickDeltas.Max()} " +
                $"frames min={frameDeltas.Min()} max={frameDeltas.Max()}");
        }
    }

    // (b) Which roster slots would today's construction-time materialisation cover that a
    // first-event materialisation would not, and which slots never see a player_team event.
    private static void AppendRoster(StringBuilder sb, ParsedDemo demo)
    {
        HashSet<int> materialising = [];
        HashSet<int> teamEventSlots = [];
        Dictionary<int, (int Old, int New)> firstTeamEvent = [];
        foreach (GameEvent e in demo.AllGameEvents)
        {
            switch (e.Payload)
            {
                case PlayerDeathEvent d:
                    materialising.Add(d.UserId);
                    materialising.Add(d.Attacker);
                    materialising.Add(d.Assister);
                    break;
                case PlayerHurtEvent h:
                    materialising.Add(h.UserId);
                    materialising.Add(h.Attacker);
                    break;
                case PlayerConnectEvent c:
                    materialising.Add(c.UserId);
                    break;
                case PlayerTeamEvent t:
                    materialising.Add(t.UserId);
                    teamEventSlots.Add(t.UserId);
                    firstTeamEvent.TryAdd(t.UserId, (t.OldTeam, t.Team));
                    break;
            }
        }

        int named = 0, namedTeamed = 0, neverMaterialising = 0, noTeamEvent = 0, noTeamEventButActive = 0;
        foreach ((int slot, PlayerInfo p) in demo.Players.OrderBy(kv => kv.Key))
        {
            bool hasName = !string.IsNullOrEmpty(p.Name);
            bool active = materialising.Contains(slot);
            bool hasTeamEvent = teamEventSlots.Contains(slot);
            if (hasName)
            {
                named++;
            }

            if (hasName && p.Team >= 2)
            {
                namedTeamed++;
                if (!active)
                {
                    neverMaterialising++;
                }
            }

            if (!hasTeamEvent)
            {
                noTeamEvent++;
                if (active)
                {
                    noTeamEventButActive++;
                }
            }

            string first = firstTeamEvent.TryGetValue(slot, out (int Old, int New) ft)
                ? $"{ft.Old}->{ft.New}"
                : "-";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"[roster] slot={slot} bot={p.IsBot} hltv={p.IsHltv} named={hasName} finalTeam={p.Team} " +
                $"materialises={active} teamEvents={hasTeamEvent} firstTeamEvent={first}");
        }

        int activeOutsideRoster = materialising.Count(s => s is >= 0 and < 64 && !demo.Players.ContainsKey(s));
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"[roster] named={named} named_team>=2={namedTeamed} never_materialising={neverMaterialising} " +
            $"no_team_event={noTeamEvent} no_team_event_but_active={noTeamEventButActive} " +
            $"active_outside_roster={activeOutsideRoster}");
    }

    // (c) Player-scoped events that precede the first positive-tick frame, and what the controller
    // entities say about teams once the signon has been applied.
    private static void AppendPreDigestEvents(StringBuilder sb, ParsedDemo demo)
    {
        int early = 0;
        HashSet<string> earlyNames = [];
        foreach (GameEvent e in demo.AllGameEvents)
        {
            if (e.GameTick > 0)
            {
                continue;
            }

            if (e.Payload is PlayerDeathEvent or PlayerHurtEvent or PlayerConnectEvent or PlayerTeamEvent)
            {
                early++;
                earlyNames.Add(e.Name);
            }
        }

        DemoFrame? firstPositive = demo.Frames.FirstOrDefault(f => f.ServerTick > 0);
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"[pre-digest] player_events_at_tick<=0={early} names={string.Join(",", earlyNames.Order(StringComparer.Ordinal))} " +
            $"first_positive_frame={firstPositive?.FrameNumber.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
            $"tick={firstPositive?.ServerTick.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        if (firstPositive is null)
        {
            return;
        }

        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        tracker.ReplayToIndex(firstPositive.FrameNumber, demo.Frames);
        int controllers = 0, teamed = 0, nameMatch = 0, nameMismatch = 0;
        foreach ((int index, EntityState state) in tracker.CurrentEntities.AllIndexed())
        {
            if (state.ClassName != "CCSPlayerController")
            {
                continue;
            }

            controllers++;
            int team = state.TryGet<int>("m_iTeamNum") ?? -1;
            if (team >= 2)
            {
                teamed++;
            }

            int slot = index - 1;
            string? entityName = state.TryGetValue("m_iszPlayerName", out object? n) ? n as string : null;
            if (demo.Players.TryGetValue(slot, out PlayerInfo? p) && !string.IsNullOrEmpty(entityName))
            {
                if (string.Equals(p.Name, entityName, StringComparison.Ordinal))
                {
                    nameMatch++;
                }
                else
                {
                    nameMismatch++;
                }
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"[pre-digest] controllers_after_signon={controllers} with_team>=2={teamed} " +
            $"slot_is_index_minus_one: match={nameMatch} mismatch={nameMismatch} lastError={tracker.LastEntityError ?? "none"}");
    }

    // (d) userinfo renames: a name that changes for a slot after it was first non-empty.
    private static void AppendRenames(StringBuilder sb, ParsedDemo demo)
    {
        StringTableProcessor tables = new();
        Dictionary<int, string> lastName = [];
        int renames = 0;
        foreach (DemoFrame frame in demo.Frames)
        {
            bool touched = false;
            foreach (NetMessage msg in frame.DecodedMessages)
            {
                switch (msg.Payload)
                {
                    case CDemoStringTables snapshot:
                        tables.ProcessSnapshot(snapshot);
                        touched = true;
                        break;
                    case CSVCMsg_CreateStringTable create:
                        tables.ProcessCreate(create);
                        touched = true;
                        break;
                    case CSVCMsg_UpdateStringTable update:
                        tables.ProcessUpdate(update);
                        touched = true;
                        break;
                }
            }

            if (!touched)
            {
                continue;
            }

            foreach ((int slot, PlayerInfo p) in tables.Players)
            {
                if (string.IsNullOrEmpty(p.Name))
                {
                    continue;
                }

                if (lastName.TryGetValue(slot, out string? previous) && !string.Equals(previous, p.Name, StringComparison.Ordinal))
                {
                    renames++;
                    sb.AppendLine(CultureInfo.InvariantCulture, $"[renames] slot={slot} frame={frame.FrameNumber}");
                }

                lastName[slot] = p.Name;
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"[renames] total={renames}");
    }
}
