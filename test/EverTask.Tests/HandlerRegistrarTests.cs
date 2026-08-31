using System.Reflection;
using System.Reflection.Emit;
using EverTask.Handler;

namespace EverTask.Tests;

/// <summary>
/// Direct unit tests for <c>HandlerRegistrar.RegisterConnectedImplementations</c> (the assembly
/// scanner): discovery, exact-interface grouping (contravariance!), first-wins duplicates, G1/G2
/// warning formats, TryAdd semantics. Complements <see cref="AssemblyResolutionTests"/>, which
/// exercises the same behavior through the full AddEverTask pipeline.
/// </summary>
public class HandlerRegistrarTests
{
    private static readonly Assembly TestsAssembly = typeof(HandlerRegistrarTests).Assembly;

    private sealed class NestedPrivateHandler : EverTaskHandler<NestedPrivateTask>
    {
        public override Task Handle(NestedPrivateTask backgroundTask, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static (ServiceCollection Services, List<string> Warnings) Scan(params Assembly[] assemblies)
    {
        var services = new ServiceCollection();
        var warnings = new List<string>();
        HandlerRegistrar.RegisterConnectedImplementations(services, assemblies, warnings);
        return (services, warnings);
    }

    private static ServiceDescriptor[] DescriptorsFor(IServiceCollection services, Type serviceType) =>
        [.. services.Where(d => d.ServiceType == serviceType)];

    // Mirrors HandlerRegistrar.Describe: handler identity in the G2 warning is the
    // namespace-qualified name plus the assembly simple name.
    private static string DescribeForWarning(Type handler) =>
        $"{handler.Namespace}.{handler.Name} ({handler.Assembly.GetName().Name})";

    /// <summary>
    /// The scanner's first-wins rule is DefinedTypes discovery order, which the compiler does not
    /// guarantee to follow source layout: compute the expected winner the same way the scanner does.
    /// </summary>
    private static Type[] InDefinedTypesOrder(params Type[] candidates)
    {
        Type[] ordered = [.. TestsAssembly.DefinedTypes.Where(candidates.Contains)];

        ordered.Length.ShouldBe(candidates.Length);
        return ordered;
    }

    // ── Core discovery ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Should_register_interface_and_concrete_descriptors_when_handler_is_closed_and_concrete()
    {
        var (services, _) = Scan(TestsAssembly);

        var byInterface = DescriptorsFor(services, typeof(IEverTaskHandler<CtvBaseTask>)).ShouldHaveSingleItem();
        byInterface.ImplementationType.ShouldBe(typeof(CtvBaseTaskHandler));
        byInterface.Lifetime.ShouldBe(ServiceLifetime.Transient);

        var byConcrete = DescriptorsFor(services, typeof(CtvBaseTaskHandler)).ShouldHaveSingleItem();
        byConcrete.ImplementationType.ShouldBe(typeof(CtvBaseTaskHandler));
        byConcrete.Lifetime.ShouldBe(ServiceLifetime.Transient);
    }

    [Fact]
    public void Should_register_each_interface_when_handler_implements_multiple_closed_handler_interfaces()
    {
        var (services, _) = Scan(TestsAssembly);

        DescriptorsFor(services, typeof(IEverTaskHandler<MultiTaskA>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(MultiTaskHandler));

        // MultiTaskB has a competitor; the multi-interface handler must at least be a candidate
        // (winner or warned loser) — the exact outcome is pinned by the dedicated duplicate test.
        var forB = DescriptorsFor(services, typeof(IEverTaskHandler<MultiTaskB>)).ShouldHaveSingleItem();
        forB.ImplementationType.ShouldBeOneOf(typeof(MultiTaskHandler), typeof(MultiTaskBOtherHandler));

        DescriptorsFor(services, typeof(MultiTaskHandler)).ShouldHaveSingleItem();
    }

    [Fact]
    public void Should_register_inherited_interface_when_handler_uses_intermediate_abstract_base()
    {
        var (services, warnings) = Scan(TestsAssembly);

        DescriptorsFor(services, typeof(IEverTaskHandler<InheritedViaBaseTask>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(InheritedViaBaseHandler));

        // The open generic abstract base is silent: no registration, no G1 warning.
        services.ShouldNotContain(d => d.ImplementationType == typeof(IntermediateHandlerBase<>));
        warnings.ShouldNotContain(w => w.Contains(nameof(IntermediateHandlerBase<IEverTask>)));
    }

    [Fact]
    public void Should_register_closed_contract_when_task_type_is_constructed_generic()
    {
        var (services, warnings) = Scan(TestsAssembly);

        DescriptorsFor(services, typeof(IEverTaskHandler<GenericPayloadTask<int>>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(GenericPayloadIntHandler));

        // Only the explicitly implemented construction is registered — nothing is synthesized.
        DescriptorsFor(services, typeof(IEverTaskHandler<GenericPayloadTask<string>>)).ShouldBeEmpty();
        warnings.ShouldNotContain(w => w.Contains(nameof(GenericPayloadIntHandler)));
    }

    [Fact]
    public async Task Should_resolve_handler_when_handler_type_is_nested_private()
    {
        var (services, _) = Scan(TestsAssembly);

        DescriptorsFor(services, typeof(IEverTaskHandler<NestedPrivateTask>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(NestedPrivateHandler));

        // Handlers implement IAsyncDisposable only: the provider must be disposed asynchronously.
        await using var provider = services.BuildServiceProvider();
        provider.GetService<IEverTaskHandler<NestedPrivateTask>>().ShouldBeOfType<NestedPrivateHandler>();
    }

    [Fact]
    public void Should_ignore_type_when_type_implements_no_closed_handler_interface()
    {
        var (services, warnings) = Scan(TestsAssembly);

        services.ShouldNotContain(d => d.ServiceType == typeof(NotAHandlerService) ||
                                       d.ImplementationType == typeof(NotAHandlerService));
        warnings.ShouldNotContain(w => w.Contains(nameof(NotAHandlerService)));

        // An OPEN generic non-handler is equally silent: G1 requires a matching handler interface,
        // not just open type parameters.
        services.ShouldNotContain(d => d.ImplementationType == typeof(OpenGenericNotAHandler<>));
        warnings.ShouldNotContain(w => w.Contains(nameof(OpenGenericNotAHandler<object>)));
    }

    [Fact]
    public void Should_ignore_handler_when_handler_type_is_abstract()
    {
        var (services, warnings) = Scan(TestsAssembly);

        services.ShouldNotContain(d => d.ImplementationType == typeof(AbstractClosedHandler) ||
                                       d.ServiceType == typeof(AbstractClosedHandler));

        // Nor does it count as a duplicate candidate for its task.
        warnings.ShouldNotContain(w => w.Contains(nameof(AbstractClosedHandler)));
    }

    // ── Open generics (G1) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Should_warn_once_when_open_generic_handler_implements_multiple_handler_interfaces()
    {
        var (_, warnings) = Scan(TestsAssembly);

        warnings.Count(w => w.Contains("OpenMultiHandler", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public void Should_not_register_closed_contract_when_open_generic_handler_implements_fixed_closed_contract()
    {
        var (services, _) = Scan(TestsAssembly);

        // OpenMultiHandler<T> implements IEverTaskHandler<OpenMultiFixedTask> (fully closed), but
        // the handler type itself still has open parameters: it can never be activated, so even
        // its closed contract must stay unregistered.
        DescriptorsFor(services, typeof(IEverTaskHandler<OpenMultiFixedTask>)).ShouldBeEmpty();
        services.ShouldNotContain(d => d.ImplementationType == typeof(OpenMultiHandler<>));
    }

    [Fact]
    public void Should_not_register_any_open_generic_descriptor_when_assembly_is_scanned()
    {
        var (services, _) = Scan(TestsAssembly);

        services.ShouldNotContain(d => d.ServiceType.ContainsGenericParameters ||
                                       (d.ImplementationType != null && d.ImplementationType.ContainsGenericParameters));
    }

    // ── Contravariance and duplicates (G2) ──────────────────────────────────────────────────────

    [Fact]
    public void Should_not_match_base_task_handler_when_handler_interface_is_contravariant()
    {
        // The trap this pins: IEverTaskHandler<in TTask> is contravariant, so the BASE task's
        // handler is assignable to the DERIVED task's interface. Assignability-based grouping
        // would see two candidates for CtvDerivedTask and could pick the wrong one.
        typeof(IEverTaskHandler<CtvDerivedTask>).IsAssignableFrom(typeof(CtvBaseTaskHandler)).ShouldBeTrue();

        var (services, warnings) = Scan(TestsAssembly);

        DescriptorsFor(services, typeof(IEverTaskHandler<CtvDerivedTask>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(CtvDerivedTaskHandler));
        DescriptorsFor(services, typeof(IEverTaskHandler<CtvBaseTask>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(CtvBaseTaskHandler));

        warnings.ShouldNotContain(w => w.Contains(nameof(CtvDerivedTask)));
        warnings.ShouldNotContain(w => w.Contains(nameof(CtvBaseTask)));
    }

    [Fact]
    public void Should_select_first_exact_handler_when_multiple_handlers_share_closed_interface()
    {
        var expectedWinner = InDefinedTypesOrder(typeof(TestTaskHanlder), typeof(TestTaskHanlderDuplicate))[0];

        var (services, _) = Scan(TestsAssembly);

        DescriptorsFor(services, typeof(IEverTaskHandler<TestTaskRequest>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(expectedWinner);
    }

    [Fact]
    public void Should_list_all_losing_handlers_in_order_when_more_than_two_duplicates_are_discovered()
    {
        var ordered = InDefinedTypesOrder(
            typeof(TriplicateHandlerOne), typeof(TriplicateHandlerTwo), typeof(TriplicateHandlerThree));

        var (services, warnings) = Scan(TestsAssembly);

        DescriptorsFor(services, typeof(IEverTaskHandler<TriplicateTask>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(ordered[0]);

        var warning = warnings.Where(w => w.Contains(nameof(TriplicateTask), StringComparison.Ordinal)).ShouldHaveSingleItem();
        warning.ShouldContain($"[{DescribeForWarning(ordered[1])}, {DescribeForWarning(ordered[2])}]");
    }

    [Fact]
    public void Should_register_losing_handler_by_concrete_type_when_duplicate_closed_handlers_are_discovered()
    {
        var (services, _) = Scan(TestsAssembly);

        // Every candidate stays resolvable by concrete type: a persisted row may carry the
        // AssemblyQualifiedName of a handler that lost the scan only after a redeploy.
        DescriptorsFor(services, typeof(TestTaskHanlder)).ShouldHaveSingleItem();
        DescriptorsFor(services, typeof(TestTaskHanlderDuplicate)).ShouldHaveSingleItem();
        DescriptorsFor(services, typeof(TriplicateHandlerOne)).ShouldHaveSingleItem();
        DescriptorsFor(services, typeof(TriplicateHandlerTwo)).ShouldHaveSingleItem();
        DescriptorsFor(services, typeof(TriplicateHandlerThree)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Should_resolve_stored_losing_handler_when_lazy_executor_uses_concrete_type_name()
    {
        // The REAL lazy path: a persisted row carries the loser's AssemblyQualifiedName, and
        // TaskHandlerExecutor.GetOrResolveHandler must resolve that exact concrete type — not the
        // interface winner — from the loser's self-registration.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(TestsAssembly));

        await using var provider = services.BuildServiceProvider();

        var lazyExecutor = new TaskHandlerExecutor(
            new TestTaskRequest("lazy-loser"),
            Handler: null,
            typeof(TestTaskHanlderDuplicate).AssemblyQualifiedName,
            ExecutionTime: null,
            RecurringTask: null,
            HandlerCallback: null,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            Guid.NewGuid(),
            QueueName: null,
            TaskKey: null,
            AuditLevel.Full);

        lazyExecutor.GetOrResolveHandler(provider).ShouldBeOfType<TestTaskHanlderDuplicate>();
    }

    [Fact]
    public void Should_handle_multi_interface_handler_when_one_interface_has_a_duplicate()
    {
        var expectedWinner = InDefinedTypesOrder(typeof(MultiTaskHandler), typeof(MultiTaskBOtherHandler))[0];
        var expectedLoser  = expectedWinner == typeof(MultiTaskHandler)
                                 ? typeof(MultiTaskBOtherHandler)
                                 : typeof(MultiTaskHandler);

        var (services, warnings) = Scan(TestsAssembly);

        DescriptorsFor(services, typeof(IEverTaskHandler<MultiTaskB>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(expectedWinner);

        warnings.ShouldContain(w => w.Contains(nameof(MultiTaskB)) && w.Contains(expectedLoser.Name));

        // The uncontested contract of the multi-interface handler is unaffected.
        DescriptorsFor(services, typeof(IEverTaskHandler<MultiTaskA>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(MultiTaskHandler));
        warnings.ShouldNotContain(w => w.Contains(nameof(MultiTaskA)));
    }

    // ── Warning formats (pinned exactly: they are logged at startup and read by operators) ──────

    [Fact]
    public void Should_emit_exact_warning_format_when_handler_is_open_generic()
    {
        var (_, warnings) = Scan(TestsAssembly);

        var openHandler = typeof(OpenGenericRegistrationHandler<>);

        warnings.ShouldContain(
            $"Open-generic handler '{openHandler.FullName ?? openHandler.Name}' implementing " +
            "IEverTaskHandler<> is not supported and was ignored. Register a closed handler for each " +
            "concrete task type.");
    }

    [Fact]
    public void Should_emit_exact_warning_format_when_duplicate_closed_handlers_are_discovered()
    {
        var ordered = InDefinedTypesOrder(
            typeof(TriplicateHandlerOne), typeof(TriplicateHandlerTwo), typeof(TriplicateHandlerThree));

        var (_, warnings) = Scan(TestsAssembly);

        warnings.ShouldContain(
            $"Multiple handlers found for task '{typeof(TriplicateTask).FullName}': scanner selected " +
            $"'{DescribeForWarning(ordered[0])}' and ignored [{DescribeForWarning(ordered[1])}, " +
            $"{DescribeForWarning(ordered[2])}] by " +
            "assembly/DefinedTypes discovery order. Register only one handler per task type.");
    }

    [Fact]
    public void Should_render_nested_generic_task_name_when_duplicate_handlers_share_nested_contract()
    {
        // Pins the formatter's declaring-chain + arity-partition logic: the generic argument
        // belongs to the OUTER container, never to the nested task type.
        var (_, warnings) = Scan(TestsAssembly);

        var warning = warnings
            .Where(w => w.Contains(nameof(NestedIntHandlerOne), StringComparison.Ordinal) ||
                        w.Contains(nameof(NestedIntHandlerTwo), StringComparison.Ordinal))
            .ShouldHaveSingleItem();

        warning.ShouldContain("EverTask.Tests.NestedTaskContainer<Int32>.InnerTask");
    }

    // ── TryAdd semantics and repetition ─────────────────────────────────────────────────────────

    [Fact]
    public void Should_preserve_existing_interface_registration_when_interface_service_was_registered_first()
    {
        var services = new ServiceCollection();
        var manual   = new CtvBaseTaskHandler();
        services.AddSingleton<IEverTaskHandler<CtvBaseTask>>(manual);

        HandlerRegistrar.RegisterConnectedImplementations(services, [TestsAssembly], new List<string>());

        var descriptor = DescriptorsFor(services, typeof(IEverTaskHandler<CtvBaseTask>)).ShouldHaveSingleItem();
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        descriptor.ImplementationInstance.ShouldBeSameAs(manual);
    }

    [Fact]
    public void Should_preserve_existing_concrete_registration_when_concrete_service_was_registered_first()
    {
        var services = new ServiceCollection();
        services.AddScoped<CtvBaseTaskHandler>();

        HandlerRegistrar.RegisterConnectedImplementations(services, [TestsAssembly], new List<string>());

        DescriptorsFor(services, typeof(CtvBaseTaskHandler))
            .ShouldHaveSingleItem().Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void Should_register_handlers_when_warning_collection_is_null()
    {
        var services = new ServiceCollection();

        ICollection<string>? warnings = null;
        HandlerRegistrar.RegisterConnectedImplementations(services, [TestsAssembly], warnings);

        DescriptorsFor(services, typeof(IEverTaskHandler<CtvBaseTask>))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(CtvBaseTaskHandler));
    }

    [Fact]
    public void Should_not_duplicate_registrations_when_same_assembly_is_scanned_twice()
    {
        // The caller deduplicates, but a repeated assembly must degrade gracefully anyway:
        // re-encountering the same Type is neither a duplicate registration nor a G2 warning.
        var (services, warnings) = Scan(TestsAssembly, TestsAssembly);

        DescriptorsFor(services, typeof(IEverTaskHandler<CtvBaseTask>)).ShouldHaveSingleItem();
        DescriptorsFor(services, typeof(CtvBaseTaskHandler)).ShouldHaveSingleItem();
        warnings.ShouldNotContain(w => w.Contains(nameof(CtvBaseTaskHandler)));

        // Pins the OTHER half of the duplicate guard (Duplicates.Contains): re-encountered losers
        // appear once — the double-scan G2 warning is emitted once and equals the single-scan text.
        var ordered = InDefinedTypesOrder(
            typeof(TriplicateHandlerOne), typeof(TriplicateHandlerTwo), typeof(TriplicateHandlerThree));
        var triplicateWarning = warnings
            .Where(w => w.Contains(nameof(TriplicateTask), StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        triplicateWarning.ShouldContain($"[{DescribeForWarning(ordered[1])}, {DescribeForWarning(ordered[2])}]");
    }

    [Fact]
    public void Should_not_duplicate_handler_descriptors_when_AddEverTask_is_called_twice()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(TestsAssembly));
        services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(TestsAssembly));

        DescriptorsFor(services, typeof(IEverTaskHandler<CtvBaseTask>)).ShouldHaveSingleItem();
        DescriptorsFor(services, typeof(CtvBaseTaskHandler)).ShouldHaveSingleItem();
    }

    // ── Cross-assembly ordering (Reflection.Emit: two dynamic assemblies, one handler each) ─────

    [Fact]
    public void Should_select_handler_from_first_assembly_when_duplicate_handlers_are_in_different_assemblies()
    {
        var asmA = EmitHandlerAssembly("OrderingHandlerA");
        var asmB = EmitHandlerAssembly("OrderingHandlerB");

        var servicesAb = new ServiceCollection();
        var warningsAb = new List<string>();
        HandlerRegistrar.RegisterConnectedImplementations(servicesAb, [asmA, asmB], warningsAb);

        DescriptorsFor(servicesAb, typeof(IEverTaskHandler<SharedOrderingTask>))
            .ShouldHaveSingleItem().ImplementationType!.Name.ShouldBe("OrderingHandlerA");
        warningsAb.ShouldContain(w => w.Contains(nameof(SharedOrderingTask)) &&
                                      w.Contains("[OrderingHandlerB (EverTask.Tests.Dynamic.OrderingHandlerB"));
    }

    [Fact]
    public void Should_select_other_handler_when_duplicate_assembly_order_is_reversed()
    {
        var asmA = EmitHandlerAssembly("OrderingHandlerA");
        var asmB = EmitHandlerAssembly("OrderingHandlerB");

        var servicesBa = new ServiceCollection();
        var warningsBa = new List<string>();
        HandlerRegistrar.RegisterConnectedImplementations(servicesBa, [asmB, asmA], warningsBa);

        DescriptorsFor(servicesBa, typeof(IEverTaskHandler<SharedOrderingTask>))
            .ShouldHaveSingleItem().ImplementationType!.Name.ShouldBe("OrderingHandlerB");
        warningsBa.ShouldContain(w => w.Contains(nameof(SharedOrderingTask)) &&
                                      w.Contains("[OrderingHandlerA (EverTask.Tests.Dynamic.OrderingHandlerA"));
    }

    [Fact]
    public async Task Should_register_and_resolve_handler_when_completed_dynamic_assembly_contains_closed_handler()
    {
        var asm = EmitHandlerAssembly("DynamicSoloHandler");

        var services = new ServiceCollection();
        HandlerRegistrar.RegisterConnectedImplementations(services, [asm], new List<string>());

        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetService<IEverTaskHandler<SharedOrderingTask>>().ShouldNotBeNull();
        handler.GetType().Name.ShouldBe("DynamicSoloHandler");
    }

    /// <summary>
    /// Emits a Run-access dynamic assembly containing one public handler deriving from
    /// <c>EverTaskHandler&lt;SharedOrderingTask&gt;</c>, overriding <c>Handle</c> with
    /// <c>return Task.CompletedTask</c>. Unique assembly name per call (Run assemblies are not
    /// collectible and xUnit runs tests in parallel).
    /// </summary>
    private static AssemblyBuilder EmitHandlerAssembly(string handlerTypeName)
    {
        var assemblyName = new AssemblyName($"EverTask.Tests.Dynamic.{handlerTypeName}.{Guid.NewGuid():N}");
        var assembly     = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var module       = assembly.DefineDynamicModule("main");

        var baseType = typeof(EverTaskHandler<>).MakeGenericType(typeof(SharedOrderingTask));
        var type     = module.DefineType(handlerTypeName, TypeAttributes.Public | TypeAttributes.Class, baseType);
        type.DefineDefaultConstructor(MethodAttributes.Public);

        var handle = type.DefineMethod(
            nameof(EverTaskHandler<SharedOrderingTask>.Handle),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(Task),
            [typeof(SharedOrderingTask), typeof(CancellationToken)]);

        var il = handle.GetILGenerator();
        il.Emit(OpCodes.Call, typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetMethod!);
        il.Emit(OpCodes.Ret);

        type.CreateType();
        return assembly;
    }

    // ── Reflection boundary ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Should_throw_when_assembly_defined_types_cannot_be_loaded()
    {
        // An explicitly requested assembly that cannot be inspected is a startup failure:
        // swallowing it would let the app run with silently missing handlers. Registration is
        // all-or-nothing: a GOOD assembly scanned BEFORE the broken one must not leave partial
        // descriptors behind (discovery completes before any registration).
        var good   = EmitHandlerAssembly("AtomicityProbeHandler");
        var broken = new Mock<Assembly>();
        broken.SetupGet(a => a.DefinedTypes)
              .Throws(new ReflectionTypeLoadException([], [], "partially loadable assembly"));

        var services = new ServiceCollection();

        Should.Throw<ReflectionTypeLoadException>(() =>
            HandlerRegistrar.RegisterConnectedImplementations(services, [good, broken.Object], new List<string>()));

        services.ShouldBeEmpty();
    }
}
