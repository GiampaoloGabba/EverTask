namespace EverTask.Worker;

public interface IWorkerBlacklist
{
    void Add(Guid guid);
    bool IsBlacklisted(Guid guid);
    void Remove(Guid guid);
}
