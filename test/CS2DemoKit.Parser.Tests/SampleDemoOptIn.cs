#region

using System.Runtime.CompilerServices;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Lets this assembly's demo-dependent tests resolve the committed sample demo
///     (<c>tests/assets/sample-de_nuke.dem</c>) when no full match is present locally.
///     <para>
///         Safe here and deliberately not enabled for the analysis suite: everything in this
///         project asserts on structure — the file parses, frames decode, seeks land on the tick
///         they asked for, the decode trace populates when enabled — none of which depends on the
///         match running a full thirty rounds. Analysis has fixtures pinned to a specific full
///         match, and a four-round trim would fail them rather than skip.
///     </para>
///     <para>
///         Tests that name a specific demo go through <c>RequireDemo(filename)</c> and still skip
///         when that file is absent, so this cannot silently redirect them at the sample.
///     </para>
/// </summary>
internal static class SampleDemoOptIn
{
    [ModuleInitializer]
    internal static void Enable() => DemoTestHelper.AllowSampleDemo = true;
}
