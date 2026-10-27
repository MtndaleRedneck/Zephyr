using Xunit;

namespace ZephyrVlcRemote.Tests;

/// <summary>Tests that mutate Program/VlcSetup process-wide state (DataDir redirect,
/// the vlcrc seam, the store-broken flag) must never run concurrently with each other
/// or with tests that read that state (round-7 N8).</summary>
[CollectionDefinition("ProgramState", DisableParallelization = true)]
public class ProgramStateCollection { }
