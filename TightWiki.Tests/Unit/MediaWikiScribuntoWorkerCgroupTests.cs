using TightWiki.Engine.MediaWiki;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiScribuntoWorkerCgroupTests
{
    [Fact]
    public void MissingDelegatedCgroupFailsClosed()
    {
        var evidence = MediaWikiScribuntoWorkerCgroup.Inspect(null, 256L * 1024 * 1024, 32);

        Assert.False(evidence.IsReady);
        Assert.Equal("WorkerCgroupUnavailable", evidence.FailureCode);
    }

    [Fact]
    public void HostPathAndRootCgroupAreRejected()
    {
        var hostPath = MediaWikiScribuntoWorkerCgroup.Inspect("/tmp/cw-worker-cgroup", 256L * 1024 * 1024, 32);
        var root = MediaWikiScribuntoWorkerCgroup.Inspect(MediaWikiScribuntoWorkerCgroup.CgroupRoot, 256L * 1024 * 1024, 32);

        Assert.False(hostPath.IsReady);
        Assert.False(root.IsReady);
        Assert.Equal("WorkerCgroupUnavailable", hostPath.FailureCode);
        Assert.Equal("WorkerCgroupUnavailable", root.FailureCode);
    }
}
