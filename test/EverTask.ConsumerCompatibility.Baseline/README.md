# Consumer compatibility fixture (issue #23, X6)

This project is a **consumer**, not a test. It is compiled against the EverTask packages that master shipped
**before** durable occurrences — the `issue23-baseline` tag — and then executed by
`test/EverTask.Tests/Serialization/ConsumerCompatibilityTests.cs` against the **current** assemblies.

That order is the whole point. Recompiling a probe against the new sources proves source compatibility only:
a public method or constructor that merely grew an optional parameter keeps compiling while its IL signature
changes, and every already-compiled caller gets a `MissingMethodException`. Here the call sites were fixed at
compile time against the old signatures, so such a change fails the test the way a real application would.

## How the wiring works

| Piece | Why |
|---|---|
| `nupkg/issue23-baseline/*.nupkg` | The two baseline packages, committed (with a `.gitignore` exception) because they are test inputs. |
| `NuGet.config` | Adds that folder as a package source, and nothing else — no `<clear />`, no source mapping, so existing feeds keep working. If your own configuration uses package source mapping, map `EverTask*` here too. |
| `PrivateAssets="all"` on the baseline `PackageReference`s | Stops the 3.11 assemblies from flowing into `EverTask.Tests`, so only this DLL crosses over and the single `EverTask.dll` at run time is the one under test. |
| Reference `3.11.0.0`, loaded assembly `4.0.0.0` | The point of X6. The default load context resolves by simple name and hands out the newer assembly, so every call site compiled against 3.11 executes against 4.0 — the situation of an application that upgrades the package without recompiling. `ConsumerCompatibilityTests` asserts the two versions really are a major apart, so the claim cannot quietly become "3.11 against 3.11" again. |

## Rebuilding the baseline packages

Only needed if the baseline itself moves (a new pre-#23 reference point).

```bash
git worktree add <scratch-dir> issue23-baseline
cd <scratch-dir>
dotnet pack src/EverTask/EverTask.csproj              -c Release -p:Version=3.11.0-issue23baseline -o <repo>/nupkg/issue23-baseline
dotnet pack src/EverTask.Abstractions/EverTask.Abstractions.csproj -c Release -p:Version=3.11.0-issue23baseline -o <repo>/nupkg/issue23-baseline
rm <repo>/nupkg/issue23-baseline/*.snupkg
```

Then clear the NuGet cache entry for the two packages (`dotnet nuget locals global-packages --clear`, or delete
`~/.nuget/packages/evertask/3.11.0-issue23baseline`) — a local feed and a fixed version otherwise resolve from
the cache.

## Adding a probe

Add a method to `BaselineConsumer` that calls the surface you care about, then call it from the test. Keep
every call a plain, direct one: a probe routed through reflection or `dynamic` resolves at run time and proves
nothing about the compiled signature.
