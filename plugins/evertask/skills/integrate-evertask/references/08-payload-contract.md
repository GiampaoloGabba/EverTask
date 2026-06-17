# 08: Payload contract (System.Text.Json) & analyzers ET0001–ET0007

Task payloads are persisted as JSON and deserialized on **recovery** (startup re-run) and for
monitoring. Getting the payload shape wrong silently drops data or throws on recovery: the one
moment persistence matters. The analyzers below ship inside `EverTask.Abstractions` and run
automatically the moment a project references it (no install, no runtime dependency); they exit
on projects that don't reference `IEverTask`.

The serializer is **isolated** (`EverTask.Serialization.EverTaskJson`): PascalCase, case-insensitive
read, numbers readable from strings, tolerant enum converter. It never reads the app's global
`JsonSerializerOptions` / `JsonSerializerContext` and never writes a `$type` marker.

## What serializes

- **Public instance properties** with a public getter.
- Record positional parameters (surface as public `init` properties, always safe).
- Properties with `public`/`init` setter, OR no setter but a matching constructor parameter
  (case-insensitive name).
- Properties explicitly marked `[System.Text.Json.Serialization.JsonInclude]`.

## What does NOT serialize

- **Public fields** (dropped; `IncludeFields` is off).
- Static properties, indexers, the record `EqualityContract`.
- `[JsonIgnore]` (STJ) properties; non-public properties.
- Properties with a non-public setter AND no matching ctor parameter.

## Supported types (round-trip safe)

Primitives, `string`, `Guid`, `DateTimeOffset`, `TimeSpan`, `TimeOnly`, **enums** (written numeric,
read numeric-or-string), nullable variants, arrays/`List<T>`, and nested records/classes of these.

- `object` / `dynamic` / `Dictionary<string,object>` come back as **`JsonElement`**: call
  `.GetInt32()`/`.GetString()` in the handler.
- `ValueTuple` fields are dropped.

## The cardinal rule: IDs, not entities

`record SyncOrder(Guid OrderId) : IEverTask;` ✅ load the entity fresh in the handler via DI.
`record SyncOrder(Order Order)` ❌ entities drag navigation properties, change-tracking, cycles.

## Analyzer rules

| ID | Severity | Flags | Code fix |
|---|---|---|---|
| **ET0001** | Warning | Public field on a payload (not persisted) | Yes: field → auto-property |
| **ET0002** | Warning | Property dropped on recovery (non-public setter, no matching ctor param) | Yes: make setter public |
| **ET0003** | Warning | Newtonsoft.Json attribute (ignored by STJ) | Yes: remove, or map `[JsonProperty("x")]`→`[JsonPropertyName("x")]`, Newtonsoft `[JsonIgnore]`→STJ `[JsonIgnore]` |
| **ET0004** | Warning | Abstract/interface-typed property without `[JsonPolymorphic]`+`[JsonDerivedType]` (throws on recovery) | Yes: scaffold polymorphism attrs per derived type |
| **ET0005** | Info | `object`/`dynamic`/`Dictionary<string,object>` property (returns `JsonElement`) | None |
| **ET0006** | Info, **disabled by default** | Won't round-trip: delegate, `Stream`, `Type`, `IntPtr`/`UIntPtr`, `CancellationToken`, `DbContext`, `ValueTuple` | None |
| **ET0007** | Warning | Class payload with ≥2 public ctors, none parameterless and none `[JsonConstructor]` (STJ throws) | None |

Records (single primary ctor) and structs (implicit parameterless ctor) are exempt from ET0007.
ET0004 unwraps `Nullable<T>` and single-arg collections before checking.

Tune severity in `.editorconfig`:

```ini
dotnet_diagnostic.ET0001.severity = error
dotnet_diagnostic.ET0006.severity = warning   # opt in to the round-trip heuristic
```

## Payload checklist when scaffolding a task

DO: flat `record`, public properties only; primitives/Guid/DateTimeOffset/TimeSpan/TimeOnly/enums/
collections; private-set properties only with a matching ctor param; `[JsonConstructor]` if a class
has multiple ctors; for polymorphic props annotate the base with
`[JsonPolymorphic(TypeDiscriminatorPropertyName="$kind")]` + `[JsonDerivedType(typeof(Sub),"alias")]`;
store IDs and load entities in the handler.

DON'T: public fields; Newtonsoft attributes; `object`/`Dictionary<string,object>` unless the handler
expects `JsonElement`; EF entities, services, `DbContext`, `Stream`, delegates, `CancellationToken`,
`Type`, `ValueTuple`; circular references; reliance on the app's global STJ config; Native AOT /
`JsonSerializerIsReflectionEnabledByDefault=false` (the serializer is reflection-based, so it throws).

## Legacy Newtonsoft (pre-v3.9)

Existing rows are read leniently (quoted numbers, string-named enums, `TimeSpan`/`DateTimeOffset`/
`TimeOnly`). New writes use numeric enums for byte-parity. No row migration needed. Edge: an
out-of-range numeric enum throws on read (clean fail; row stays recoverable then `Failed`).
