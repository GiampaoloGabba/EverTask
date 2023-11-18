namespace EverTask.Logger;

/// <summary>
/// EverTask custom logger
/// </summary>
public interface IEverTaskLogger<out T> : ILogger<T>;
