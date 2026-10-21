using System.Runtime.CompilerServices;

// Much of the engine is internal; editor tooling and the test assemblies need access. The game scripts
// (Assembly-CSharp) use the public API only, so no other assembly gets internals access.
[assembly: InternalsVisibleTo("Engine.Editor")]
[assembly: InternalsVisibleTo("Engine.Tests.EditMode")]
[assembly: InternalsVisibleTo("Engine.Tests.PlayMode")]
[assembly: InternalsVisibleTo("Engine.Tests.Performance")]
