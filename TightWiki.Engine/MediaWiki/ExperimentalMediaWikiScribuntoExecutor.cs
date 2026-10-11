using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TightWiki.Engine.MediaWiki;

/// <summary>Explicit opt-in settings for the disposable Scribunto worker experiment.</summary>
public sealed record ExperimentalMediaWikiScribuntoExecutorOptions(
    string WorkerAssemblyPath,
    bool EnableExperimentalExecution = false,
    bool RequireBubblewrap = true,
    int MaxConcurrentExecutions = 1,
    int WorkerOutputCharacters = 100_000,
    long WorkerMemoryBytes = 2L * 1024 * 1024 * 1024,
    int WorkerCpuSeconds = 2,
    int WorkerInputCharacters = 2_000_000,
    string? WorkerCgroupPath = null,
    int WorkerTaskLimit = 32,
    int WorkerErrorOutputCharacters = 16_384,
    string? DotnetRootPath = null,
    int? WorkerSandboxUid = null,
    int? WorkerSandboxGid = null);

/// <summary>
/// Runs the experimental Lua interpreter in a fresh, disposable worker. This
/// type is never registered by the web application and fails closed unless an
/// explicit test/development opt-in and a Linux isolation wrapper are supplied.
/// </summary>
public sealed class ExperimentalMediaWikiScribuntoExecutor : IMediaWikiScribuntoExecutor, IDisposable
{
    private readonly ExperimentalMediaWikiScribuntoExecutorOptions _options;
    private readonly SemaphoreSlim _concurrency;

    public ExperimentalMediaWikiScribuntoExecutor(ExperimentalMediaWikiScribuntoExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.WorkerAssemblyPath))
            throw new ArgumentException("A worker assembly path is required.", nameof(options));
        if (options.MaxConcurrentExecutions < 1 || options.WorkerOutputCharacters < 1
            || options.WorkerMemoryBytes < 64L * 1024 * 1024 || options.WorkerCpuSeconds < 1
            || options.WorkerInputCharacters < 1 || options.WorkerTaskLimit < 1
            || options.WorkerErrorOutputCharacters < 1 || options.WorkerSandboxUid is < 1 or > 65534
            || options.WorkerSandboxGid is < 1 or > 65534)
            throw new ArgumentOutOfRangeException(nameof(options), "Worker limits must be positive and practical.");
        if (options.EnableExperimentalExecution && !options.RequireBubblewrap)
            throw new ArgumentException("Experimental execution must retain bubblewrap isolation.", nameof(options));
        if (options.EnableExperimentalExecution && !File.Exists(options.WorkerAssemblyPath))
            throw new FileNotFoundException("The experimental worker assembly was not found.", options.WorkerAssemblyPath);
        if (options.EnableExperimentalExecution && string.IsNullOrWhiteSpace(options.WorkerCgroupPath))
            throw new ArgumentException("Experimental execution requires an administrator-provisioned worker cgroup.", nameof(options));
        _options = options;
        _concurrency = new SemaphoreSlim(options.MaxConcurrentExecutions, options.MaxConcurrentExecutions);
    }

    public async Task<MediaWikiScribuntoExecutionResult> ExecuteAsync(
        MediaWikiScribuntoInvocation invocation,
        IMediaWikiScribuntoExecutionHost host,
        MediaWikiScribuntoExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.EnableExperimentalExecution)
            return Rejected("Scribunto experimental execution is disabled; the rejecting executor remains the default.");
        if (!OperatingSystem.IsLinux() && _options.RequireBubblewrap)
            return Failed("ScribuntoIsolationUnavailable", "The experimental worker requires Linux bubblewrap isolation.");
        if (_options.RequireBubblewrap && !File.Exists("/usr/bin/bwrap"))
            return Failed("ScribuntoIsolationUnavailable", "bubblewrap was not available; the worker was not started.");
        MediaWikiScribuntoWorkerCgroupEvidence? cgroupEvidence = null;
        if (_options.RequireBubblewrap)
        {
            cgroupEvidence = MediaWikiScribuntoWorkerCgroup.Inspect(_options.WorkerCgroupPath, _options.WorkerMemoryBytes, _options.WorkerTaskLimit);
            if (!cgroupEvidence.IsReady)
                return Failed(cgroupEvidence.FailureCode ?? "WorkerCgroupUnavailable", cgroupEvidence.FailureMessage ?? "The worker cgroup was not verified; the worker was not started.");
        }

        var bundle = await host.ResolveModuleBundleAsync(invocation.Module, context.Page.RevisionTimestampUtc, cancellationToken);
        if (bundle is null)
            return Failed("ScribuntoModuleMissing", $"No source was available for '{invocation.Module.CanonicalTitle}'.");
        if (!string.Equals(bundle.Root.Module.CanonicalTitle, invocation.Module.CanonicalTitle, StringComparison.Ordinal))
            return Failed("ScribuntoModuleIdentityMismatch", "The host returned a module source for a different canonical title.");
        if (bundle.Root.SourceWiki.Value == "unversioned-host")
            bundle = bundle with { Root = bundle.Root with { SourceWiki = context.Page.SourceWiki } };
        var rootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bundle.Root.Source)));
        if (!string.Equals(rootHash, bundle.Root.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("ScribuntoModuleSourceHashMismatch", "The host returned a root module source with a mismatched checksum.");
        if (!bundle.Root.ContentModel.Equals("Scribunto", StringComparison.OrdinalIgnoreCase)
            && !bundle.Root.ContentModel.Equals("Scribunto/Lua", StringComparison.OrdinalIgnoreCase)
            && !bundle.Root.ContentModel.Equals("lua", StringComparison.OrdinalIgnoreCase))
            return Failed("UnsupportedContentModel", "The root module used an unsupported content model.");
        var budgetDiagnostics = MediaWikiScribuntoBudgetValidator.Validate(
            invocation,
            context,
            1,
            bundle.Root.Source.Length,
            0,
            bundle.Dependencies.Sum(dependency => (long)dependency.Source.Length));
        if (budgetDiagnostics.Count > 0)
            return new MediaWikiScribuntoExecutionResult(null, budgetDiagnostics,
                [new("module", invocation.Module.CanonicalTitle, invocation.CallingSourceRevisionId, "BudgetRejected")]);

        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            return await RunWorkerAsync(invocation, host, context, bundle, cgroupEvidence, cancellationToken);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void Dispose() => _concurrency.Dispose();

    private async Task<MediaWikiScribuntoExecutionResult> RunWorkerAsync(
        MediaWikiScribuntoInvocation invocation,
        IMediaWikiScribuntoExecutionHost host,
        MediaWikiScribuntoExecutionContext context,
        MediaWikiScribuntoModuleBundle bundle,
        MediaWikiScribuntoWorkerCgroupEvidence? cgroupEvidence,
        CancellationToken cancellationToken)
    {
        var root = bundle.Root;
        var correlationId = Guid.NewGuid().ToString("N");
        var dependencies = bundle.Dependencies.Select(dependency => new MediaWikiScribuntoWorkerModuleDependency(
            dependency.SourceWiki.Value,
            dependency.Module.CanonicalTitle,
            dependency.SourcePageId,
            dependency.SourceRevisionId,
            dependency.ContentModel,
            dependency.SourceSha256,
            dependency.Source,
            dependency.Dependencies.Select(value => value.CanonicalTitle).ToArray())).ToArray();
        var request = new MediaWikiScribuntoWorkerRequest(
            MediaWikiScribuntoWorkerProtocol.CurrentVersion,
            correlationId,
            invocation.Module.CanonicalTitle,
            $"{root.SourcePageId}@{root.SourceRevisionId}",
            root.SourceWiki.Value,
            root.SourceRevisionId,
            root.Source,
            invocation.ExportedFunction,
            invocation.PositionalArguments,
            invocation.NamedArguments,
            new(
                context.Budget.MaxModuleSourceCharacters,
                context.Budget.MaxArgumentCount,
                context.Budget.MaxArgumentCharacters,
                context.Budget.MaxIndividualArgumentCharacters,
                context.Budget.MaxResultCharacters,
                context.Budget.MaxInstructionCount,
                context.Budget.MaxModuleDependencyDepth,
                context.Budget.MaxDependencySourceCharacters,
                _options.WorkerInputCharacters,
                _options.WorkerOutputCharacters,
                context.Budget.MaxMemoryBytes,
                Math.Max(1, (int)Math.Min(int.MaxValue, context.Budget.EffectiveMaxExecutionTime.TotalMilliseconds))),
            MediaWikiScribuntoWorkerProtocol.DefaultCapabilities,
            dependencies,
            root.Dependencies.Select(value => value.CanonicalTitle).ToArray(),
            invocation.CallingPageTitle,
            invocation.TemplateInvocationChain.LastOrDefault(),
            context.Page.SourceRevisionId,
            invocation.TemplateInvocationChain);
        var serializedRequest = MediaWikiScribuntoWorkerProtocol.SerializeRequest(request);
        if (serializedRequest.Length > _options.WorkerInputCharacters)
            return Failed("ScribuntoWorkerInputLimitExceeded", "The worker request exceeded the configured input limit.");
        var start = Stopwatch.GetTimestamp();
        Process process;
        try { process = StartWorker(); }
        catch (Exception) { return Failed("ScribuntoWorkerStartFailed", "The experimental worker could not be started."); }
        using (process)
        {
            var maxOutputBytes = (int)Math.Min(int.MaxValue, (long)_options.WorkerOutputCharacters * 4L);
            var stdoutTask = ReadBoundedPipeAsync(process.StandardOutput.BaseStream, maxOutputBytes);
            var stderrTask = ReadBoundedPipeAsync(process.StandardError.BaseStream, _options.WorkerErrorOutputCharacters);
            try
            {
                if (cgroupEvidence is not null)
                    MediaWikiScribuntoWorkerCgroup.AttachProcess(cgroupEvidence, process.Id);
            }
            catch (Exception)
            {
                TryKill(process);
                await ReapAsync(process, stdoutTask, stderrTask);
                return Failed("WorkerCgroupAttachFailed", "The worker could not be placed in the verified cgroup; it was not executed.");
            }

            try
            {
                await process.StandardInput.WriteAsync(serializedRequest.AsMemory(), cancellationToken);
                await process.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await ReapAsync(process, stdoutTask, stderrTask);
                throw;
            }

            var timeout = context.Budget.EffectiveMaxExecutionTime;
            var exitTask = process.WaitForExitAsync();
            var timeoutTask = Task.Delay(timeout);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var waiters = new List<Task> { exitTask, stdoutTask, stderrTask, timeoutTask, cancellationTask };
            while (true)
            {
                var completed = await Task.WhenAny(waiters);
                if (completed == cancellationTask)
                {
                    TryKill(process);
                    await ReapAsync(process, stdoutTask, stderrTask);
                    throw new OperationCanceledException(cancellationToken);
                }
                if (completed == timeoutTask)
                {
                    TryKill(process);
                    await ReapAsync(process, stdoutTask, stderrTask);
                    return Failed("ScribuntoTimeout", $"The worker was terminated after {timeout.TotalMilliseconds:0} ms.");
                }
                if (completed == stdoutTask)
                {
                    var stdoutResult = await stdoutTask;
                    waiters.Remove(stdoutTask);
                    if (stdoutResult.Overflowed)
                    {
                        TryKill(process);
                        await ReapAsync(process, stdoutTask, stderrTask);
                        return Failed("ScribuntoWorkerOutputLimitExceeded", "The worker stdout exceeded the configured byte limit.");
                    }
                    continue;
                }
                if (completed == stderrTask)
                {
                    var stderrResult = await stderrTask;
                    waiters.Remove(stderrTask);
                    if (stderrResult.Overflowed)
                    {
                        TryKill(process);
                        await ReapAsync(process, stdoutTask, stderrTask);
                        return Failed("ScribuntoWorkerDiagnosticOutputLimitExceeded", "The worker stderr exceeded the configured byte limit.");
                    }
                    continue;
                }
                if (completed == exitTask)
                {
                    waiters.Remove(exitTask);
                    break;
                }
            }

            var outputResult = await stdoutTask;
            var errorResult = await stderrTask;
            if (outputResult.Overflowed)
                return Failed("ScribuntoWorkerOutputLimitExceeded", "The worker stdout exceeded the configured byte limit.");
            if (errorResult.Overflowed)
                return Failed("ScribuntoWorkerDiagnosticOutputLimitExceeded", "The worker stderr exceeded the configured byte limit.");
            var output = outputResult.Text;
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                return Failed("ScribuntoWorkerCrashed", "The experimental worker exited without a structured response.");
            if (!MediaWikiScribuntoWorkerProtocol.TryParseResponse(output.Trim(), _options.WorkerOutputCharacters, correlationId, out var response, out _, out var protocolMessage))
                return Failed("ScribuntoWorkerProtocolError", protocolMessage);
        var responseDiagnostics = response!.Diagnostics.Select(diagnostic => new MediaWikiCompatibilityDiagnostic(
            diagnostic.Code,
            diagnostic.Message,
            invocation.Location.Offset,
            invocation.Location.Length,
            invocation.Location.Line,
            invocation.Location.Column,
            diagnostic.Severity == "Information" ? MediaWikiDiagnosticSeverity.Information : diagnostic.Severity == "Warning" ? MediaWikiDiagnosticSeverity.Warning : MediaWikiDiagnosticSeverity.Error)).ToArray();
        if (response.Status is not "Succeeded")
            return new MediaWikiScribuntoExecutionResult(null, responseDiagnostics.Length > 0 ? responseDiagnostics : [new("ScribuntoExecutionFailed", "The worker rejected execution.", invocation.Location.Offset, invocation.Location.Length, invocation.Location.Line, invocation.Location.Column, MediaWikiDiagnosticSeverity.Error)],
                [new("module", invocation.Module.CanonicalTitle, invocation.CallingSourceRevisionId, response.Status == "ResourceLimit" ? "ResourceLimit" : "ExecutionFailed")], false);
        var result = response.Text ?? string.Empty;
        if (result.Length > context.Budget.MaxResultCharacters)
            return Failed("ScribuntoResultBudgetExceeded", "The worker result exceeded the configured limit.");
        var elapsed = Stopwatch.GetElapsedTime(start);
        var reportedDependencies = (response.Dependencies ?? []).Select(dependency =>
        {
            var value = new MediaWikiScribuntoDependency(
                "module",
                dependency.CanonicalModuleTitle,
                dependency.SourceRevisionId,
                dependency.Status,
                dependency.SourceWikiIdentity,
                dependency.SourcePageId,
                dependency.SourceSha256);
            host.RegisterDependency(value);
            return value;
        }).ToArray();
        return new MediaWikiScribuntoExecutionResult(
            result,
            [..responseDiagnostics, new("ScribuntoWorkerCompleted", $"The isolated worker completed in {elapsed.TotalMilliseconds:0.0} ms.", invocation.Location.Offset, invocation.Location.Length, invocation.Location.Line, invocation.Location.Column, MediaWikiDiagnosticSeverity.Information)],
            reportedDependencies.Length > 0 ? reportedDependencies : [new("module", invocation.Module.CanonicalTitle, root.SourceRevisionId, "Executed", root.SourceWiki.Value, root.SourcePageId, root.SourceSha256)],
            true);
        }
    }

    private Process StartWorker()
    {
        var workerDirectory = Path.GetDirectoryName(Path.GetFullPath(_options.WorkerAssemblyPath))!;
        var workerInsideSandbox = "/worker/" + Path.GetFileName(_options.WorkerAssemblyPath);
        var dotnetRoot = ResolveDotnetRoot();
        var processStartInfo = new ProcessStartInfo
        {
            FileName = _options.RequireBubblewrap ? "/usr/bin/prlimit" : "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (_options.RequireBubblewrap)
        {
            processStartInfo.ArgumentList.Add($"--as={_options.WorkerMemoryBytes}");
            processStartInfo.ArgumentList.Add($"--cpu={_options.WorkerCpuSeconds}");
            processStartInfo.ArgumentList.Add("--nofile=256");
            processStartInfo.ArgumentList.Add("/usr/bin/bwrap");
            processStartInfo.ArgumentList.Add("--die-with-parent");
            processStartInfo.ArgumentList.Add("--unshare-all");
            processStartInfo.ArgumentList.Add("--new-session");
            processStartInfo.ArgumentList.Add("--clearenv");
            if (_options.WorkerSandboxUid is int sandboxUid)
            {
                processStartInfo.ArgumentList.Add("--uid");
                processStartInfo.ArgumentList.Add(sandboxUid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (_options.WorkerSandboxGid is int sandboxGid)
            {
                processStartInfo.ArgumentList.Add("--gid");
                processStartInfo.ArgumentList.Add(sandboxGid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            AddReadOnlyBind(processStartInfo, dotnetRoot, "/dotnet");
            AddReadOnlyBind(processStartInfo, workerDirectory, "/worker");
            AddReadOnlyBindIfPresent(processStartInfo, "/lib", "/lib");
            AddReadOnlyBindIfPresent(processStartInfo, "/lib64", "/lib64");
            AddReadOnlyBindIfPresent(processStartInfo, "/usr/lib", "/usr/lib");
            AddReadOnlyBindIfPresent(processStartInfo, "/usr/lib64", "/usr/lib64");
            processStartInfo.ArgumentList.Add("--proc"); processStartInfo.ArgumentList.Add("/proc");
            processStartInfo.ArgumentList.Add("--dev"); processStartInfo.ArgumentList.Add("/dev");
            processStartInfo.ArgumentList.Add("--tmpfs"); processStartInfo.ArgumentList.Add("/tmp");
            processStartInfo.ArgumentList.Add("--chdir"); processStartInfo.ArgumentList.Add("/worker");
            AddEnvironment(processStartInfo, "DOTNET_ROOT", "/dotnet");
            AddEnvironment(processStartInfo, "DOTNET_ROOT_X64", "/dotnet");
            AddEnvironment(processStartInfo, "HOME", "/tmp");
            AddEnvironment(processStartInfo, "TMPDIR", "/tmp");
            AddEnvironment(processStartInfo, "PATH", "/dotnet");
            processStartInfo.ArgumentList.Add("--");
            processStartInfo.ArgumentList.Add("/dotnet/dotnet");
            processStartInfo.ArgumentList.Add(workerInsideSandbox);
        }
        else
        {
            processStartInfo.ArgumentList.Add(_options.WorkerAssemblyPath);
        }
        var process = Process.Start(processStartInfo) ?? throw new InvalidOperationException("The Scribunto worker could not be started.");
        return process;
    }

    private string ResolveDotnetRoot()
    {
        var configured = _options.DotnetRootPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return ValidateRuntimeRoot(configured);

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return ValidateRuntimeRoot(Path.GetDirectoryName(Path.GetFullPath(processPath))!);

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT_X64")
            ?? Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
            return ValidateRuntimeRoot(dotnetRoot);
        throw new InvalidOperationException("The .NET runtime root could not be resolved without assuming a host installation path.");
    }

    private static string ValidateRuntimeRoot(string value)
    {
        var path = Path.GetFullPath(value);
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "dotnet")))
            throw new DirectoryNotFoundException("The configured .NET runtime root was not usable.");
        return path;
    }

    private static void AddReadOnlyBind(ProcessStartInfo startInfo, string source, string target)
    {
        if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source))
            throw new InvalidOperationException("A required sandbox mount was not a fully qualified directory.");
        startInfo.ArgumentList.Add("--ro-bind");
        startInfo.ArgumentList.Add(source);
        startInfo.ArgumentList.Add(target);
    }

    private static void AddReadOnlyBindIfPresent(ProcessStartInfo startInfo, string source, string target)
    {
        if (Directory.Exists(source))
            AddReadOnlyBind(startInfo, source, target);
    }

    private static void AddEnvironment(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add("--setenv");
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static async Task ReapAsync(Process process, Task<BoundedPipeResult> stdoutTask, Task<BoundedPipeResult> stderrTask)
    {
        try { await process.WaitForExitAsync(); } catch (Exception) { }
        try { await stdoutTask; } catch (Exception) { }
        try { await stderrTask; } catch (Exception) { }
    }

    private static async Task<BoundedPipeResult> ReadBoundedPipeAsync(Stream stream, int maxBytes)
    {
        var buffer = new byte[8192];
        await using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                return new(Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length)), false);
            if (read > maxBytes - total)
                return new(string.Empty, true);
            await output.WriteAsync(buffer.AsMemory(0, read));
            total += read;
        }
    }

    private sealed record BoundedPipeResult(string Text, bool Overflowed);

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static MediaWikiScribuntoExecutionResult Rejected(string message)
        => new(null, [new("ScribuntoExecutionRejected", message, 0, 0, 0, 0, MediaWikiDiagnosticSeverity.Warning)], [], false);

    private static MediaWikiScribuntoExecutionResult Failed(string code, string message)
        => new(null, [new(code, message, 0, 0, 0, 0, MediaWikiDiagnosticSeverity.Error)], [], false);

}
