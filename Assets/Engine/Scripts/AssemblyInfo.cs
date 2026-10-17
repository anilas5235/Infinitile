using System.Runtime.CompilerServices;

// INT-52: much of the engine is internal; editor tooling, the test assemblies and the
// game scripts (Assembly-CSharp) need access. Part II assemblies get no internals access.
[assembly: InternalsVisibleTo("Engine.Editor")]
[assembly: InternalsVisibleTo("Engine.Tests.EditMode")]
[assembly: InternalsVisibleTo("Engine.Tests.PlayMode")]
[assembly: InternalsVisibleTo("Engine.Tests.Performance")]
[assembly: InternalsVisibleTo("Assembly-CSharp")]
