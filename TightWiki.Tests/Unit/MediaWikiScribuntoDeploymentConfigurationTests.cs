namespace TightWiki.Tests.Unit;

public sealed class MediaWikiScribuntoDeploymentConfigurationTests
{
    private static string RepositoryPath(string relativePath) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../", relativePath));

    [Fact]
    public void SocketUnitUsesDirectoryModeAndDedicatedSocketIdentity()
    {
        var socket = File.ReadAllText(RepositoryPath("deploy/systemd/continuumwiki-scribunto-worker.socket"));

        Assert.Contains("ListenStream=/run/continuumwiki/scribunto-worker.sock", socket);
        Assert.Contains("SocketUser=continuumwiki-scribunto", socket);
        Assert.Contains("SocketGroup=continuumwiki-scribunto", socket);
        Assert.Contains("DirectoryMode=0750", socket);
        Assert.Contains("Accept=yes", socket);
        Assert.DoesNotContain("RuntimeDirectory=", socket, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceUnitPreservesFixedLeastPrivilegeAndResourceControls()
    {
        var service = File.ReadAllText(RepositoryPath("deploy/systemd/continuumwiki-scribunto-worker@.service"));

        Assert.Contains("User=continuumwiki-scribunto", service);
        Assert.Contains("Group=continuumwiki-scribunto", service);
        Assert.Contains("Delegate=yes", service);
        Assert.Contains("MemoryMax=256M", service);
        Assert.Contains("TasksMax=32", service);
        Assert.Contains("CPUQuota=100%", service);
        Assert.Contains("LimitCPU=2", service);
        Assert.Contains("LimitNOFILE=256", service);
        Assert.Contains("PrivateNetwork=yes", service);
        Assert.Contains("NoNewPrivileges=yes", service);
        Assert.Contains("ProtectKernelTunables=no", service);
        Assert.DoesNotContain("ProtectKernelTunables=yes\n", service, StringComparison.Ordinal);
        Assert.Contains("KillMode=control-group", service);
        Assert.DoesNotContain("sudo", service, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FixedLauncherUsesDynamicRuntimeMinimalMountsAndNoNprocLimit()
    {
        var launcher = File.ReadAllText(RepositoryPath("deploy/systemd/continuumwiki-scribunto-worker-launcher"));

        Assert.Contains("command -v dotnet", launcher);
        Assert.Contains("readlink -f", launcher);
        Assert.Contains("--clearenv", launcher);
        Assert.Contains("--ro-bind /usr/share/icu /usr/share/icu", launcher);
        Assert.Contains("--ro-bind \"$dotnet_root\" /dotnet", launcher);
        Assert.Contains("--ro-bind \"$worker_directory\" /worker", launcher);
        Assert.Contains("--proc /proc --remount-ro /proc", launcher);
        Assert.Contains("--as=2147483648", launcher);
        Assert.DoesNotContain("--nproc", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("/etc", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("--uid 65534", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("--gid 65534", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void CertificationHarnessNamesAllMandatoryIsolationChecks()
    {
        var harness = File.ReadAllText(RepositoryPath("scripts/verify-moonsharp-host-sandbox.sh"));

        foreach (var check in new[]
        {
            "worker-cgroup", "namespace-support", "os-filesystem-read", "os-filesystem-write",
            "os-network", "os-environment", "os-process", "os-library", "os-memory", "os-tasks",
            "os-cpu", "worker-adversarial-suite"
        })
            Assert.Contains(check, harness);
        Assert.Contains("HOST_CERTIFICATION_UNAVAILABLE", harness);
        Assert.Contains("CW_SCRIBUNTO_WORKER_CGROUP_PATH", harness);
        Assert.Contains("client.shutdown(socket.SHUT_WR)", harness);
        Assert.Contains("socket-activation", harness);
    }

    [Fact]
    public void ProcfsDiagnosticComparesOnlyTheKernelTunablesMaskingCandidate()
    {
        var diagnostic = File.ReadAllText(RepositoryPath("scripts/diagnose-moonsharp-procfs.sh"));

        Assert.Contains("--property=\"ProtectKernelTunables=$candidate\"", diagnostic);
        Assert.Contains("run_candidate yes 1", diagnostic);
        Assert.Contains("run_candidate no 0", diagnostic);
        Assert.Contains("VFS: Mount too revealing", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--proc /proc --remount-ro /proc", diagnostic);
        Assert.Contains("PROCFS_DIAGNOSTIC_VERIFIED", diagnostic);
        Assert.Contains("MemoryMax=256M", diagnostic);
        Assert.Contains("PrivateNetwork=yes", diagnostic);
    }

    [Fact]
    public void SocketActivationProbeHalfClosesTheRequest()
    {
        var provisioning = File.ReadAllText(RepositoryPath("docs/operations/CONTINUUMWIKI-LUA-SANDBOX-PROVISIONING.md"));

        Assert.Contains("client.shutdown(socket.SHUT_WR)", provisioning);
    }
}
