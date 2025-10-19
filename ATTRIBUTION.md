# Acknowledgements & Attribution

## Special Thanks

Special thanks to **[jbogard](https://github.com/jbogard)** for the **[MediatR](https://github.com/jbogard/MediatR)** project, which provided significant inspiration in the development of key components of this library.

MediatR's elegant request/response pattern and its approach to handler registration greatly influenced the design and architecture of EverTask.

## Inspired Components

The following components in EverTask were inspired by and adapted from MediatR:

### [`TaskDispatcher.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/Dispatcher/Dispatcher.cs)

The task dispatcher implementation follows MediatR's `Mediator` pattern for resolving and executing handlers.

### [`TaskHandlerExecutor.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/Handler/TaskHandlerExecutor.cs)

The handler executor structure is based on MediatR's `NotificationHandlerExecutor`, adapted for background task execution with lifecycle hooks.

### [`TaskHandlerWrapper.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/Handler/TaskHandlerWrapper.cs)

The handler wrapper abstraction follows MediatR's pattern for type-safe handler invocation.

### [`HandlerRegistrar.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/MicrosoftExtensionsDI/HandlerRegistrar.cs)

The automatic handler registration using assembly scanning is inspired by MediatR's registration approach.

## Attribution Comments

Each of these files includes comments acknowledging and referencing the specific parts of the MediatR project that inspired them.

## Request/Handler Pattern

The core concept of defining requests (tasks) and handlers separately, then resolving and executing them dynamically, comes directly from MediatR's architecture. This pattern provides:

- **Separation of concerns**: Task definitions separate from execution logic
- **Dependency injection**: Handlers receive their dependencies automatically
- **Testability**: Handlers can be tested independently
- **Extensibility**: Easy to add new tasks without modifying infrastructure

## Apache 2.0 License

This project includes code adapted from [MediatR](https://github.com/jbogard/MediatR), which is licensed under the Apache License 2.0.

The full text of the Apache License 2.0 can be found in the [LICENSE](LICENSE) file.

### Apache License 2.0 Summary

The Apache License 2.0 is a permissive open source license that allows you to:

- **Use** the software for any purpose
- **Distribute** the software
- **Modify** the software
- **Distribute modified versions**

Under the following conditions:

- **License and copyright notice**: Include a copy of the license and copyright notice
- **State changes**: Document significant changes made to the software
- **Notice file**: If the project includes a NOTICE file, you must include it

The license also includes:

- **Patent grant**: Contributors grant patent rights
- **Trademark**: Does not grant trademark rights
- **Limitation of liability**: No warranty or liability

## EverTask's Unique Contributions

While inspired by MediatR, EverTask adds significant functionality not present in MediatR:

- **Background execution**: Tasks execute asynchronously in background workers
- **Persistence**: Tasks are persisted and survive application restarts
- **Scheduling**: Support for delayed, scheduled, and recurring tasks
- **Resilience**: Retry policies, timeouts, and error handling
- **Monitoring**: Task events and real-time monitoring via SignalR
- **Multi-queue**: Workload isolation with multiple execution queues
- **High-performance scheduler**: PeriodicTimerScheduler and ShardedScheduler for extreme loads
- **Advanced features**: Continuations, cancellation, idempotent registration

These features make EverTask a complete background task processing system, while MediatR focuses on in-process request/response mediation.

## License Compatibility

EverTask is also licensed under the Apache License 2.0, ensuring compatibility with the MediatR components that inspired it.

## Contribution

EverTask is open source and welcomes contributions. If you have ideas, improvements, or bug fixes, please:

1. Visit the [GitHub repository](https://github.com/GiampaoloGabba/EverTask)
2. Open an issue to discuss your idea
3. Submit a pull request with your changes

## Credits

**EverTask** is developed and maintained by [Giampaolo Gabba](https://github.com/GiampaoloGabba).

**MediatR** is developed and maintained by [Jimmy Bogard](https://github.com/jbogard).

## Resources

- **EverTask GitHub**: https://github.com/GiampaoloGabba/EverTask
- **MediatR GitHub**: https://github.com/jbogard/MediatR
- **Apache License 2.0**: https://www.apache.org/licenses/LICENSE-2.0
