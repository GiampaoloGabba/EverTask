---
layout: default
title: Serialization
parent: Storage
nav_order: 9
---

# JSON serialization

EverTask turns every task into JSON before it touches storage, and back into an object when it runs it again. Since v3.9 that job is done by **System.Text.Json** (it used Newtonsoft.Json before). You almost never call the serializer yourself, but the shape of your task decides whether it survives a restart, so it pays to know the rules.

## When (de)serialization actually happens

Three moments, all internal:

1. **Dispatch.** The task is serialized and the JSON is written to the `Request` column. Recurring schedules are serialized too, into `RecurringTask`.
2. **Recovery.** On startup EverTask reads back the pending rows and deserializes them to re-run the work. This is the only path that turns stored JSON back into your task, so it's where a bad payload bites.
3. **Monitoring.** The same JSON is sent to the SignalR monitoring sink as `TaskParameters`.

A normal immediate dispatch hands the in-memory object straight to the handler, so it never round-trips. Recovery does. That asymmetry is why a payload bug can pass every local run and only show up after a restart.

## The rules that matter

EverTask uses one private, isolated serializer configuration. It does not read your app's global JSON settings, and it never writes a `$type` type marker, so a hostile global config can't weaponize recovery. What it does care about:

- **Public properties only.** Public *fields* are not serialized. This is the one that catches people most often.
- **A setter it can reach.** A property with only a private/internal setter and no matching constructor parameter is dropped on read. Give it a public setter, or accept it through the constructor (records do this for free).
- **Enums are written as numbers.** `Priority.High` becomes `2`, not `"High"`. Reads are lenient: a legacy row with `"High"` still parses.
- **Newtonsoft attributes are ignored.** `[JsonProperty]`, `[JsonIgnore]`, and Newtonsoft's `[JsonConstructor]` do nothing here. Don't rely on them to rename, hide, or pick a constructor.
- **`object` and `Dictionary<string, object>` come back as `JsonElement`.** You get the raw JSON node, not a boxed `int` or `string`. Read it explicitly in the handler.

## Catching mistakes at build time

The catch with all of the above is timing: a broken payload compiles, runs locally, and only falls over after a restart, on the recovery path. To close that gap EverTask ships a Roslyn analyzer inside the `EverTask.Abstractions` package. There's nothing to install or switch on. The moment a project references `IEverTask`, the analyzer runs in the IDE and in the build, and checks the same rules this page describes against every task type (and the types they pull in).

| Rule | What it flags |
|------|---------------|
| ET0001 | A public field on a payload; it won't be serialized. A code fix turns it into a property. |
| ET0002 | A property with no setter the serializer can reach and no matching constructor parameter; dropped on read. A code fix adds a public setter. |
| ET0003 | A Newtonsoft attribute (`[JsonProperty]`, `[JsonIgnore]`, …) that System.Text.Json ignores. A code fix removes it or maps it to the STJ equivalent. |
| ET0004 | An abstract or interface property with no `[JsonPolymorphic]`/`[JsonDerivedType]` declaration; it throws on recovery. A code fix scaffolds the attributes. |
| ET0005 | An `object` or `Dictionary<string, object>` property that comes back as `JsonElement`. (Informational.) |
| ET0006 | A type that won't round-trip: a delegate, `Stream`, `Type`, `IntPtr`, `DbContext`, a `ValueTuple`. Off by default, since the guess can misfire; turn it on if you want it. |
| ET0007 | A type with more than one public constructor and no way for the serializer to pick one; recovery throws. |

Every rule is a normal diagnostic, so you tune it in `.editorconfig`. Promote the ones you care about to errors, or opt into ET0006:

```ini
[*.cs]
dotnet_diagnostic.ET0001.severity = error
dotnet_diagnostic.ET0006.severity = warning
```

## Designing a payload that survives

Keep tasks as plain data. Primitives, `Guid`, `DateTimeOffset`, strings, simple collections, and nested records all round-trip cleanly.

```csharp
// Good: flat, all public properties, no behavior.
public record ProcessOrderTask(Guid OrderId, decimal Amount, string Currency) : IEverTask;
```

Pass identifiers, not entities. An EF entity drags navigation properties, change-tracking state, and circular references into the JSON; load it fresh inside the handler instead.

```csharp
// Don't serialize the entity.
public record SendEmailTask(User User) : IEverTask;            // no

// Serialize the id, load in the handler.
public record SendEmailTask(Guid UserId) : IEverTask;          // yes
```

Skip services, `DbContext`, streams, and delegates. None of them round-trip. Inject what you need into the handler through DI.

For the full design checklist see [Storage best practices](best-practices.md).

## Polymorphic payloads

By default a property typed as an abstract base or interface does **not** work: the derived members are dropped on write, and recovery throws because the serializer can't build an abstract type. This is deliberate (no arbitrary type loading means no deserialization gadget).

If you genuinely need a polymorphic property, declare the allowed subtypes on the base type with System.Text.Json's own attributes:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(EmailChannel), "email")]
[JsonDerivedType(typeof(SmsChannel),   "sms")]
public abstract class NotifyChannel { }

public sealed class EmailChannel : NotifyChannel { public string Address { get; set; } = ""; }
public sealed class SmsChannel   : NotifyChannel { public string Number  { get; set; } = ""; }

public record NotifyTask(NotifyChannel Channel) : IEverTask;
```

Now `Channel` round-trips with its concrete type intact. The stored JSON carries a short discriminator (`"$kind":"email"`), not a CLR type name, so the closed set of types you declared is the only thing recovery can ever build. A row with an unknown discriminator is rejected, not loaded.

Use the System.Text.Json attributes (`[JsonPolymorphic]`, `[JsonDerivedType]`), not the Newtonsoft ones. And pick a discriminator name like `$kind` rather than the default `$type`, to keep it visually distinct from the old Newtonsoft type marker.

If you can avoid polymorphism, do. A flat payload with an enum discriminator and a few nullable fields is simpler to reason about and impossible to get wrong.

## Coming from Newtonsoft (upgrade notes)

If you already have rows on disk written by an older EverTask, you don't need to migrate them. The reader is intentionally lenient about the shapes Newtonsoft used to produce:

- Quoted numbers (`"Count": "42"`) parse fine.
- String-named enums (`"Priority": "High"`), including `DayOfWeek` arrays on recurring schedules, parse fine.
- `TimeSpan`, `DateTimeOffset`, `TimeOnly`, and the recurring interval types all read back unchanged.

New writes use the numeric enum form, so a row written by the new version stays readable by an un-upgraded peer during a rolling deployment.

Two behavior changes to know about:

- A 4-byte emoji is written as an escaped `🚀` surrogate pair instead of raw bytes. It still round-trips to the same string; only the on-disk representation differs.
- `IPAddress` and a handful of other types that Newtonsoft rejected at dispatch now behave differently under System.Text.Json. Keep payloads to the simple types above and it's a non-issue.

## Customizing the serializer

You can't, yet. The serializer is internal and isolated on purpose. That isolation is a feature: your app's global JSON configuration can't reach in and change how tasks are stored, which keeps recovery predictable and closes a gadget-deserialization hole.

A pluggable `IEverTaskSerializer` is planned as a separate, additive change. Until then, the lever you have is the payload shape, not the serializer settings.

Two things follow from that isolation. Your app's own System.Text.Json source generators don't apply to task storage: EverTask never looks at your `JsonSerializerContext`, so turning them on changes nothing here. And because the serializer is reflection-based, Native AOT and `JsonSerializerIsReflectionEnabledByDefault=false` aren't supported. There, serialization fails at runtime no matter how simple the payload is. AOT support is part of the planned pluggable serializer.

## When something goes missing

A few symptoms and what they usually mean:

- **A value is empty after a restart but fine before.** It was a public field, or a property with no reachable setter. Make it a public property with a public setter (or a record parameter).
- **Recovery throws for one task type.** Often a polymorphic property without `[JsonDerivedType]`, or a type that can't be deserialized at all (a service, a stream). The row stays recoverable for a few attempts, then gets marked `Failed` rather than retried forever.
- **A dictionary value isn't the type you expected.** `Dictionary<string, object>` hands you `JsonElement`. Call `.GetInt32()`, `.GetString()`, and so on.

## Testing your tasks

The cheapest insurance is a round-trip test. If a task can't survive serialize-then-deserialize in a unit test, it won't survive a restart either.

```csharp
[Fact]
public void Order_task_round_trips()
{
    var original = new ProcessOrderTask(Guid.NewGuid(), 99.90m, "EUR");

    var json     = System.Text.Json.JsonSerializer.Serialize(original);
    var restored = System.Text.Json.JsonSerializer.Deserialize<ProcessOrderTask>(json);

    restored.ShouldNotBeNull();
    restored!.OrderId.ShouldBe(original.OrderId);
    restored.Amount.ShouldBe(original.Amount);
}
```

## Next steps

- [Storage best practices](best-practices.md): the full task-design checklist
- [Task creation](../task-creation.md): patterns for modeling work as tasks
- [Custom storage](custom-storage.md): plug in your own persistence layer
