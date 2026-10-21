using System.Runtime.CompilerServices;

// Much of the engine is internal; editor tooling, the test assemblies and the
// game scripts (Assembly-CSharp) need access. No other assembly gets internals access.
[assembly: InternalsVisibleTo("Engine.Editor")]
[assembly: InternalsVisibleTo("Engine.Tests.EditMode")]
[assembly: InternalsVisibleTo("Engine.Tests.PlayMode")]
[assembly: InternalsVisibleTo("Engine.Tests.Performance")]
[assembly: InternalsVisibleTo("Assembly-CSharp")]
