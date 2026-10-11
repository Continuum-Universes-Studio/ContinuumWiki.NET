namespace TightWiki.Engine.MediaWiki;

/// <summary>
/// Evidence and attachment operations for the administrator-provisioned cgroup
/// v2 directory used by the experimental worker. The web process may use this
/// only when an operator has delegated this one directory; it never creates a
/// unit, changes controllers, or accepts arbitrary systemd properties.
/// </summary>
public sealed record MediaWikiScribuntoWorkerCgroupEvidence(
    string Path,
    long MemoryMaxBytes,
    int TasksMax,
    string CpuMax,
    string CurrentProcessIds,
    bool Writable,
    string? FailureCode = null,
    string? FailureMessage = null)
{
    public bool IsReady => FailureCode is null && Writable;
}

public static class MediaWikiScribuntoWorkerCgroup
{
    public const string CgroupRoot = "/sys/fs/cgroup";

    public static MediaWikiScribuntoWorkerCgroupEvidence Inspect(
        string? cgroupPath,
        long requiredMemoryBytes,
        int requiredTaskLimit)
    {
        if (string.IsNullOrWhiteSpace(cgroupPath))
            return Failure(string.Empty, "WorkerCgroupUnavailable", "An administrator-provisioned worker cgroup is required.");
        if (requiredMemoryBytes < 1 || requiredTaskLimit < 1)
            return Failure(cgroupPath, "WorkerCgroupConfigurationInvalid", "The worker cgroup limits must be positive.");

        string path;
        try
        {
            path = Path.GetFullPath(cgroupPath);
        }
        catch (Exception)
        {
            return Failure(cgroupPath, "WorkerCgroupPathInvalid", "The worker cgroup path was not valid.");
        }

        if (!IsSafeDelegatedPath(path) || !Directory.Exists(path))
            return Failure(path, "WorkerCgroupUnavailable", "The worker cgroup must be an existing dedicated directory below /sys/fs/cgroup.");

        var memoryMaxPath = System.IO.Path.Combine(path, "memory.max");
        var tasksMaxPath = System.IO.Path.Combine(path, "pids.max");
        var cpuMaxPath = System.IO.Path.Combine(path, "cpu.max");
        var processesPath = System.IO.Path.Combine(path, "cgroup.procs");
        if (!File.Exists(memoryMaxPath) || !File.Exists(tasksMaxPath) || !File.Exists(cpuMaxPath) || !File.Exists(processesPath))
            return Failure(path, "WorkerCgroupUnavailable", "The worker cgroup did not expose the required v2 control files.");

        try
        {
            var memoryText = File.ReadAllText(memoryMaxPath).Trim();
            var tasksText = File.ReadAllText(tasksMaxPath).Trim();
            var cpuText = File.ReadAllText(cpuMaxPath).Trim();
            var processIds = File.ReadAllText(processesPath).Trim();
            if (!long.TryParse(memoryText, out var memoryMax) || memoryMax < 1 || memoryMax > requiredMemoryBytes)
                return Failure(path, "WorkerCgroupMemoryNotEnforced", "memory.max was unlimited, invalid, or above the worker memory policy.");
            if (!int.TryParse(tasksText, out var tasksMax) || tasksMax < 1 || tasksMax > requiredTaskLimit)
                return Failure(path, "WorkerCgroupTasksNotEnforced", "pids.max was unlimited, invalid, or above the worker task policy.");
            if (!HasFiniteCpuLimit(cpuText))
                return Failure(path, "WorkerCgroupCpuNotEnforced", "cpu.max was unlimited or invalid.");

            using var stream = new FileStream(processesPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return new(path, memoryMax, tasksMax, cpuText, processIds, true);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(path, "WorkerCgroupNotWritable", "The application could not write the delegated cgroup.");
        }
        catch (IOException)
        {
            return Failure(path, "WorkerCgroupUnavailable", "The worker cgroup could not be read safely.");
        }
    }

    public static void AttachProcess(MediaWikiScribuntoWorkerCgroupEvidence evidence, int processId)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.IsReady)
            throw new InvalidOperationException(evidence.FailureMessage ?? "The worker cgroup was not ready.");
        if (processId < 1)
            throw new ArgumentOutOfRangeException(nameof(processId));

        var processesPath = System.IO.Path.Combine(evidence.Path, "cgroup.procs");
        File.WriteAllText(processesPath, processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var processIds = File.ReadAllLines(processesPath);
        if (!processIds.Any(value => string.Equals(value.Trim(), processId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)))
            throw new InvalidOperationException("The worker process was not observable in the delegated cgroup after attachment.");
    }

    private static bool IsSafeDelegatedPath(string path)
    {
        if (string.Equals(path, CgroupRoot, StringComparison.Ordinal)
            || !path.StartsWith(CgroupRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;
        var relative = System.IO.Path.GetRelativePath(CgroupRoot, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
            return false;
        var current = CgroupRoot;
        foreach (var segment in relative.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            if (Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null)
                return false;
        }
        return true;
    }

    private static bool HasFiniteCpuLimit(string value)
    {
        var fields = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 2
            && long.TryParse(fields[0], out var quota)
            && quota > 0
            && long.TryParse(fields[1], out var period)
            && period > 0;
    }

    private static MediaWikiScribuntoWorkerCgroupEvidence Failure(string path, string code, string message)
        => new(path, 0, 0, string.Empty, string.Empty, false, code, message);
}
