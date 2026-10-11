using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TightWiki.Engine.MediaWiki;

namespace TightWiki.Tests.Unit;

public sealed class ExperimentalMediaWikiScribuntoRuntimeTests
{
    private static string WorkerPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../ScribuntoWorker/bin/Debug/net10.0/ContinuumWiki.ScribuntoWorker.dll"));

    [Fact]
    public async Task SyntheticInvocationSupportsNamedPositionalArgumentsAndRestrictedApi()
    {
        var response = await RunWorkerAsync(
            "return { main = function(frame) return mw.text.trim(frame.args[1]) .. '|' .. frame.args.name .. '|' .. tostring(mw.ustring.len('é✓中')) end }",
            "main",
            ["  left  "],
            new Dictionary<string, string> { ["name"] = "right" });

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("left|right|3", response.Text);
    }

    [Fact]
    public async Task NativeContinuumApiUsesOnlyTypedInvocationSnapshot()
    {
        var response = await RunWorkerAsync(
            "return { main = function() return cw.args.get('name') .. '|' .. tostring(cw.args.has('name')) .. '|' .. cw.module.name .. '|' .. cw.module.revision .. '|' .. cw.title.normalize(' A_B ') end }",
            "main", capabilities: ["cw.args", "cw.module", "cw.title"], named: new Dictionary<string, string> { ["name"] = "Continuum" },
            moduleTitle: "Module:Native", sourceRevisionId: "native-revision", frameTitle: "Article");

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("Continuum|true|Module:Native|native-revision|A B", response.Text);
    }

    [Fact]
    public async Task NativeApiCapabilityIsDeniedWhenNotDeclared()
    {
        var response = await RunWorkerAsync("return { main = function() return cw.text.trim(' x ') end }", "main", capabilities: ["frame.args"]);

        Assert.False(response.Success);
        Assert.Equal("LuaRuntimeError", response.ErrorCode);
    }

    [Fact]
    public async Task MediaWikiJsonTextApiIsBoundedAndRoundTripsScalars()
    {
        var response = await RunWorkerAsync(
            "return { main = function() local encoded = mw.text.jsonEncode({z='last', a=2, list={'é', true}}); local value = mw.text.jsonDecode(encoded); return encoded .. '|' .. value.a .. '|' .. value.list[1] .. '|' .. tostring(value.list[2]) end }",
            "main");

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("{\"a\":2,\"list\":[\"\\u00E9\",true],\"z\":\"last\"}|2|é|true", response.Text);
    }

    [Fact]
    public async Task MediaWikiJsonTextApiRejectsInvalidJsonAndUnsupportedValues()
    {
        var invalid = await RunWorkerAsync("return { main = function() return mw.text.jsonDecode('{') end }", "main");
        var unsupported = await RunWorkerAsync("return { main = function() return mw.text.jsonEncode({f=function() end}) end }", "main");

        Assert.False(invalid.Success);
        Assert.Equal("LuaRuntimeError", invalid.ErrorCode);
        Assert.False(unsupported.Success);
        Assert.Equal("LuaRuntimeError", unsupported.ErrorCode);
    }

    [Fact]
    public async Task MediaWikiTitleUsesOnlySuppliedFrameContext()
    {
        var response = await RunWorkerAsync(
            "return { main = function() local current = mw.title; local other = mw.title.new(' Talk_Page '); return current.text .. '|' .. tostring(current.namespace) .. '|' .. tostring(current.exists) .. '|' .. tostring(current.getContent()) .. '|' .. other.text .. '|' .. tostring(other.namespace) end }",
            "main", capabilities: ["mw.title"], frameTitle: "Main_Page");

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("Main_Page|0|false|nil|Talk Page|0", response.Text);
    }

    [Fact]
    public async Task MediaWikiSiteUsesValidatedWikiIdentityOnly()
    {
        var response = await RunWorkerAsync(
            "return { main = function() return mw.site.name .. '|' .. mw.site.identifier .. '|' .. tostring(mw.site.language) .. '|' .. tostring(mw.site.namespaces.foo) end }",
            "main", capabilities: ["mw.site"], sourceWikiIdentity: "synthetic-wiki");

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("synthetic-wiki|synthetic-wiki|nil|nil", response.Text);
    }

    [Fact]
    public async Task MediaWikiLanguageUsesInvariantUnicodeOperations()
    {
        var response = await RunWorkerAsync(
            "return { main = function() return tostring(mw.language.getCode()) .. '|' .. mw.language.lc('É✓') .. '|' .. mw.language.uc('é✓') .. '|' .. mw.language.lcfirst('Éclair') .. '|' .. mw.language.ucfirst('éclair') end }",
            "main", capabilities: ["mw.language"]);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("nil|é✓|É✓|éclair|Éclair", response.Text);
    }

    [Fact]
    public async Task NativePageApiFailsClosedWithoutAnAuthorizedSnapshot()
    {
        var response = await RunWorkerAsync(
            "return { main = function() return tostring(cw.page.exists('Any_Page')) .. '|' .. tostring(cw.page.metadata('Any_Page')) end }",
            "main", capabilities: ["cw.page"]);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("false|nil", response.Text);
    }

    [Fact]
    public async Task MediaWikiHtmlElementEncodesTextAndAttributes()
    {
        var response = await RunWorkerAsync(
            "return { main = function() return mw.html.element('span', '<safe>', {class='notice', title='a&b'}) end }",
            "main", capabilities: ["mw.html"]);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("<span class=\"notice\" title=\"a&amp;b\">&lt;safe&gt;</span>", response.Text);
    }

    [Fact]
    public async Task MediaWikiHtmlRejectsUnsafeTagsAndAttributes()
    {
        var tag = await RunWorkerAsync("return { main = function() return mw.html.element('script', 'x') end }", "main", capabilities: ["mw.html"]);
        var attribute = await RunWorkerAsync("return { main = function() return mw.html.element('span', 'x', {onclick='bad'}) end }", "main", capabilities: ["mw.html"]);

        Assert.False(tag.Success);
        Assert.Equal("LuaRuntimeError", tag.ErrorCode);
        Assert.False(attribute.Success);
        Assert.Equal("LuaRuntimeError", attribute.ErrorCode);
    }

    [Fact]
    public async Task DangerousLibrariesAreAbsentAndSeparateWorkersDoNotShareState()
    {
        var first = await RunWorkerAsync(
            "leaked = 'secret'; return { main = function(frame) return tostring(os) .. '|' .. tostring(io) .. '|' .. tostring(loadfile) end }",
            "main");
        var second = await RunWorkerAsync(
            "return { main = function(frame) return tostring(leaked) .. '|' .. tostring(debug) .. '|' .. tostring(require) end }",
            "main");

        Assert.True(first.Success, first.ErrorMessage);
        Assert.Equal("nil|nil|nil", first.Text);
        Assert.True(second.Success, second.ErrorMessage);
        Assert.StartsWith("nil|nil|function", second.Text);
    }

    [Fact]
    public async Task EnvironmentAndClrAccessAreUnavailable()
    {
        var environment = await RunWorkerAsync("return { main = function() return os.getenv('PATH') end }", "main");
        var clr = await RunWorkerAsync("return { main = function() return import('System.String') end }", "main");

        Assert.False(environment.Success);
        Assert.Equal("LuaRuntimeError", environment.ErrorCode);
        Assert.False(clr.Success);
        Assert.Equal("LuaRuntimeError", clr.ErrorCode);
    }

    [Fact]
    public async Task InstructionBudgetStopsInfiniteLoop()
    {
        var response = await RunWorkerAsync(
            "return { main = function(frame) while true do end end }",
            "main",
            maxInstructionCount: 2_000);

        Assert.False(response.Success);
        Assert.Equal("InstructionLimitExceeded", response.ErrorCode);
    }

    [Fact]
    public async Task LargeTableAllocationIsStoppedByTheWorkBudget()
    {
        var response = await RunWorkerAsync(
            "return { main = function() local values = {}; for i = 1, 100000 do values[i] = i end; return 'ok' end }",
            "main",
            maxInstructionCount: 2_000);

        Assert.False(response.Success);
        Assert.Equal("InstructionLimitExceeded", response.ErrorCode);
    }

    [Fact]
    public async Task RecursiveCallsAndDynamicLoadingAreBounded()
    {
        var response = await RunWorkerAsync(
            "return { main = function(frame) local function recurse() return recurse() end; recurse() end }",
            "main",
            maxInstructionCount: 2_000);
        var capabilityProbe = await RunWorkerAsync(
            "return { main = function(frame) return tostring(load) .. '|' .. tostring(dofile) .. '|' .. tostring(package.loadlib) .. '|' .. tostring(_G) end }",
            "main");

        Assert.False(response.Success);
        Assert.Equal("InstructionLimitExceeded", response.ErrorCode);
        Assert.True(capabilityProbe.Success, capabilityProbe.ErrorMessage);
        Assert.Contains("nil|nil|nil|table", capabilityProbe.Text);
    }

    [Fact]
    public async Task WorkerRejectsOversizedSourceAndArgumentsAndRecovers()
    {
        var source = await RunWorkerAsync("return { main = function(frame) return 'ok' end }", "main");
        var oversizedSource = await RunWorkerAsync(
            "return { main = function(frame) return 'ok' end }",
            "main",
            maxSourceCharacters: 10);
        var oversizedArgument = await RunWorkerAsync(
            "return { main = function(frame) return 'ok' end }",
            "main",
            [new string('x', 101_000)]);

        Assert.True(source.Success, source.ErrorMessage);
        Assert.Equal("ok", source.Text);
        Assert.False(oversizedSource.Success);
        Assert.Equal("SourceLimitExceeded", oversizedSource.ErrorCode);
        Assert.False(oversizedArgument.Success);
        Assert.Equal("IndividualArgumentLimitExceeded", oversizedArgument.ErrorCode);
    }

    [Fact]
    public async Task InvalidSyntaxAndOversizedResultsReturnStructuredErrors()
    {
        var syntax = await RunWorkerAsync("return {", "main");
        var output = await RunWorkerAsync(
            "return { main = function(frame) return string.rep('x', 1000) end }",
            "main",
            maxResultCharacters: 100);

        Assert.False(syntax.Success);
        Assert.False(string.IsNullOrWhiteSpace(syntax.ErrorCode));
        Assert.False(output.Success);
        Assert.Equal("ResultLimitExceeded", output.ErrorCode);
    }

    [Fact]
    public async Task TextAndUnicodeCompatibilitySurfaceUsesStructuredProtocol()
    {
        var response = await RunWorkerAsync(
            "return { main = function(frame) local x = mw.text.listToText({'one','two','three'}, ', ', ' and '); return mw.text.nowiki('<x>|{y}') .. '|' .. mw.text.encode('<&') .. '|' .. mw.text.decode('&lt;&amp;') .. '|' .. x .. '|' .. mw.ustring.sub('Aé✓中', 2, -2) .. '|' .. tostring(mw.ustring.find('Aé✓中', '✓', 1, true)) end }",
            "main");

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("&lt;x&gt;&#124;&#123;y&#125;|&lt;&amp;|<&|one, two and three|é✓|3", response.Text);
        Assert.Equal("Succeeded", response.Status);
    }

    [Fact]
    public async Task MissingAndEmptyArgumentsRemainDistinctAndNamedKeysAreOrderedSafely()
    {
        var response = await RunWorkerAsync(
            "return { main = function(frame) return tostring(frame.args.missing) .. '|' .. tostring(frame.args.empty == '') .. '|' .. frame.args[1] end }",
            "main",
            ["first"],
            new Dictionary<string, string> { ["empty"] = string.Empty });

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("nil|true|first", response.Text);
    }

    [Fact]
    public async Task FrameArgumentEnumerationUsesStablePositionalThenNamedOrder()
    {
        var response = await RunWorkerAsync(
            "return { main = function(frame) local values = {}; for key, value in pairs(frame.args) do values[#values + 1] = tostring(key) .. '=' .. value end; return table.concat(values, '|') end }",
            "main",
            ["one", "two"],
            new Dictionary<string, string> { ["z"] = "last", ["a"] = "first" });

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("1=one|2=two|a=first|z=last", response.Text);
    }

    [Fact]
    public async Task UnsupportedPatternAndFrameOperationsReturnExplicitDiagnostics()
    {
        var pattern = await RunWorkerAsync("return { main = function(frame) return mw.ustring.find('abc', 'a.', 1, false) end }", "main");
        var frame = await RunWorkerAsync("return { main = function(frame) return frame:getTitle() end }", "main", frameTitle: "Article");
        var badType = await RunWorkerAsync("return { main = function(frame) return mw.text.decode('&lt;', 'yes') end }", "main");

        Assert.False(pattern.Success);
        Assert.Equal("UnsupportedApi", pattern.ErrorCode);
        Assert.True(frame.Success, frame.ErrorMessage);
        Assert.Equal("Article", frame.Text);
        Assert.False(badType.Success);
        Assert.Equal("LuaRuntimeError", badType.ErrorCode);
    }

    [Fact]
    public async Task ProtocolRejectsUnsupportedVersionDuplicateKeysAndCapabilities()
    {
        var unsupported = await RunRawWorkerAsync("{\"protocolVersion\":99,\"correlationId\":\"raw-1\"}");
        var valid = await ValidRequestJsonAsync();
        var duplicate = await RunRawWorkerAsync(valid.Replace("\"protocolVersion\":1,", "\"protocolVersion\":1,\"protocolVersion\":1,", StringComparison.Ordinal));
        var capability = await RunRawWorkerAsync(valid.Replace("mw.ustring", "os", StringComparison.Ordinal));

        Assert.Equal("UnsupportedProtocolVersion", unsupported.Diagnostics[0].Code);
        Assert.Equal("MalformedJson", duplicate.Diagnostics[0].Code);
        Assert.Equal("UnsupportedCapability", capability.Diagnostics[0].Code);
    }

    [Fact]
    public async Task ProtocolRejectsDuplicateNamedArgumentsAndUnexpectedSequence()
    {
        var duplicate = await RunRawWorkerAsync((await ValidRequestJsonAsync()).Replace("\"namedArguments\":{}", "\"namedArguments\":{\"Name\":\"a\",\"name\":\"b\"}", StringComparison.Ordinal));
        var sequence = await RunRawWorkerAsync((await ValidRequestJsonAsync()) + "\n{}", preserveSequence: true);

        Assert.Equal("DuplicateOrInvalidNamedArgument", duplicate.Diagnostics[0].Code);
        Assert.Equal("UnexpectedMessageSequence", sequence.Diagnostics[0].Code);
    }

    [Fact]
    public async Task OversizedJsonInputIsRejectedBeforeInterpretation()
    {
        var response = await RunRawWorkerAsync(new string('x', 2_100_000));

        Assert.Equal("InputLimitExceeded", response.ErrorCode);
    }

    [Fact]
    public async Task MissingExportsAndDynamicModuleCallsFailClosed()
    {
        var missing = await RunWorkerAsync("return { other = function() return 'ok' end }", "main");
        var dynamicLoad = await RunWorkerAsync("return { main = function() return require('Module:Other') end }", "main");

        Assert.False(missing.Success);
        Assert.Equal("ExportNotFound", missing.ErrorCode);
        Assert.False(dynamicLoad.Success);
        Assert.Equal("UndeclaredModuleDependency", dynamicLoad.ErrorCode);
    }

    [Fact]
    public async Task ControlledRequireLoadsOnlyBundledModulesAndReportsVersions()
    {
        var response = await RunWorkerAsync(
            "local b = require(\"Module:B\"); local again = require(\"B\"); return { main = function() return b.value .. again.value end }",
            "main",
            dependencies: [Module("B", "return { value = \"B\" }", "page-b", "revision-b")],
            rootDependencies: ["Module:B"]);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("BB", response.Text);
        Assert.Equal(["Module:Test", "Module:B"], response.Dependencies.Select(value => value.CanonicalModuleTitle).ToArray());
        Assert.Equal("revision-b", response.Dependencies.Single(value => value.CanonicalModuleTitle == "Module:B").SourceRevisionId);
    }

    [Fact]
    public async Task DependencyBundleRejectsCyclesMissingModulesHashesCrossWikiAndPathReferences()
    {
        var cycle = await RunWorkerAsync(
            "local b = require(\"Module:B\"); return { main = function() return b.value end }",
            "main",
            dependencies: [
                Module("B", "local a = require(\"Module:A\"); return { value = a.value }", "page-b", "revision-b", ["Module:A"]),
                Module("A", "return { value = \"A\" }", "page-a", "revision-a", ["Module:B"])],
            rootDependencies: ["Module:B"]);
        var missing = await RunWorkerAsync(
            "return { main = function() return require(\"Module:Missing\") end }",
            "main",
            rootDependencies: ["Module:Missing"]);
        var hash = Module("B", "return { value = \"B\" }", "page-b", "revision-b") with { SourceSha256 = new string('0', 64) };
        var modified = await RunWorkerAsync(
            "return { main = function() return require(\"Module:B\") end }",
            "main",
            dependencies: [hash],
            rootDependencies: ["Module:B"]);
        var crossWiki = await RunWorkerAsync(
            "return { main = function() return require(\"Module:B\") end }",
            "main",
            dependencies: [Module("B", "return {}", "page-b", "revision-b") with { SourceWikiIdentity = "other-wiki" }],
            rootDependencies: ["Module:B"]);
        var path = await RunWorkerAsync(
            "return { main = function() return require(\"../B\") end }",
            "main",
            dependencies: [Module("B", "return {}", "page-b", "revision-b")],
            rootDependencies: ["Module:B"]);

        Assert.Equal("CyclicModuleDependency", cycle.ErrorCode);
        Assert.Equal("MissingRequiredDependency", missing.ErrorCode);
        Assert.Equal("DependencySourceHashMismatch", modified.ErrorCode);
        Assert.Equal("CrossWikiModuleDependency", crossWiki.ErrorCode);
        Assert.Equal("InvalidModuleReference", path.ErrorCode);
    }

    [Fact]
    public async Task LoadDataIsImmutableAndJsonDataIsBoundedAndTyped()
    {
        var data = await RunWorkerAsync(
            "local value = mw.loadData(\"Module:Data\"); return { main = function() value.value = \"changed\"; return value.value end }",
            "main",
            dependencies: [Module("Data", "return { value = \"D\" }", "page-data", "revision-data")],
            rootDependencies: ["Module:Data"]);
        var json = await RunWorkerAsync(
            "local value = mw.loadJsonData(\"Module:Json\"); return { main = function() return value.value .. \"|\" .. tostring(value.count) end }",
            "main",
            dependencies: [Module("Json", "{\"value\":\"J\",\"count\":2}", "page-json", "revision-json", contentModel: "Json")],
            rootDependencies: ["Module:Json"]);
        var malformed = await RunWorkerAsync(
            "return { main = function() return mw.loadJsonData(\"Module:Json\") end }",
            "main",
            dependencies: [Module("Json", "{bad", "page-json", "revision-json", contentModel: "Json")],
            rootDependencies: ["Module:Json"]);

        Assert.False(data.Success);
        Assert.Equal("ImmutableDataViolation", data.ErrorCode);
        Assert.True(json.Success, json.ErrorMessage);
        Assert.Equal("J|2", json.Text);
        Assert.False(malformed.Success);
        Assert.Equal("MalformedJsonData", malformed.ErrorCode);
    }

    [Fact]
    public async Task FrameTitleParentAndHistoricalInvocationMetadataAreTrusted()
    {
        var response = await RunWorkerAsync(
            "return { main = function(frame) return frame:getTitle() .. \"|\" .. frame:getParent():getTitle() end }",
            "main",
            frameTitle: "Article",
            parentFrameTitle: "Template:Shell",
            parentFrameSourceRevisionId: "template-revision",
            invocationChain: ["Template:Shell"]);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("Article|Template:Shell", response.Text);
        Assert.Equal("revision-one", (await RunWorkerAsync("return { main = function() return 'one' end }", "main", sourceRevisionId: "revision-one")).Dependencies.Single().SourceRevisionId);
        Assert.Equal("revision-two", (await RunWorkerAsync("return { main = function() return 'two' end }", "main", sourceRevisionId: "revision-two")).Dependencies.Single().SourceRevisionId);
    }

    [Fact]
    public async Task DependencyBundleRejectsUnsupportedModelsDuplicateIdentitiesOversizeGraphsAndInvalidParents()
    {
        var unsupported = await RunWorkerAsync(
            "return { main = function() return require(\"Module:B\") end }",
            "main",
            dependencies: [Module("B", "return {}", "page-b", "revision-b", contentModel: "wikitext")],
            rootDependencies: ["Module:B"]);
        var duplicate = await RunWorkerAsync(
            "return { main = function() return require(\"Module:B\") end }",
            "main",
            dependencies: [Module("B", "return {}", "page-b", "revision-b"), Module("B", "return { changed = true }", "page-b", "revision-b2")],
            rootDependencies: ["Module:B"]);
        var oversized = await RunWorkerAsync(
            "return { main = function() return 'ok' end }",
            "main",
            dependencies: [Module("B", "return {}", "page-b", "revision-b")],
            maxDependencySourceCharacters: 1);
        var large = await RunWorkerAsync(
            "return { main = function() return 'ok' end }",
            "main",
            dependencies: Enumerable.Range(1, 1_025).Select(index => Module($"B{index}", "return {}", $"page-{index}", $"revision-{index}")).ToArray());
        var invalidParent = await RunRawWorkerAsync((await ValidRequestJsonAsync()).Replace("\"parentFrameTitle\":null", "\"parentFrameTitle\":\"Template:X\"", StringComparison.Ordinal));

        Assert.Equal("UnsupportedContentModel", unsupported.ErrorCode);
        Assert.Equal("DuplicateModuleIdentity", duplicate.ErrorCode);
        Assert.Equal("DependencySourceLimitExceeded", oversized.ErrorCode);
        Assert.Equal("DependencyCountLimitExceeded", large.ErrorCode);
        Assert.Equal("InvalidParentFrame", invalidParent.ErrorCode);
    }

    [Fact]
    public async Task ProtocolRejectsUnknownFieldsBadArgumentTypesAndArgumentCount()
    {
        var valid = await ValidRequestJsonAsync();
        var unknown = await RunRawWorkerAsync(valid[..^1] + ",\"unknown\":1}");
        var badType = await RunRawWorkerAsync(valid.Replace("\"positionalArguments\":[]", "\"positionalArguments\":[1]", StringComparison.Ordinal));
        var tooMany = await RunWorkerAsync("return { main = function() return 'ok' end }", "main", Enumerable.Repeat("x", 129).ToArray());

        Assert.Equal("ProtocolError", unknown.Status);
        Assert.Equal("InvalidArguments", badType.ErrorCode);
        Assert.Equal("ArgumentCountLimitExceeded", tooMany.ErrorCode);
    }

    [Fact]
    public async Task CapabilitySelectionRemovesUnavailableApiAndErrorsAreRedacted()
    {
        var response = await RunWorkerAsync(
            "return { main = function() return mw.text.trim('x') end }",
            "main",
            capabilities: ["frame.args"]);
        var syntax = await RunWorkerAsync("return { main = function() return '/home/cdjensen94/private' end ", "main");

        Assert.False(response.Success);
        Assert.Equal("LuaRuntimeError", response.ErrorCode);
        Assert.False(syntax.Success);
        Assert.DoesNotContain("/home/cdjensen94", syntax.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeparateWorkerProcessesRemainIsolatedUnderConcurrentInvocation()
    {
        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(index =>
            RunWorkerAsync($"return {{ main = function() local value = 'worker-{index}' return value end }}", "main")));

        Assert.Equal(["worker-1", "worker-2", "worker-3", "worker-4"], results.Select(result => result.Text!).ToArray());
        Assert.All(results, result => Assert.True(result.Success, result.ErrorMessage));
    }

    [Fact]
    public async Task AggregateArgumentBudgetRejectsManyBoundedValues()
    {
        var response = await RunWorkerAsync(
            "return { main = function() return 'ok' end }",
            "main",
            Enumerable.Repeat(new string('x', 2_000), 100).ToArray());

        Assert.False(response.Success);
        Assert.Equal("ArgumentLimitExceeded", response.ErrorCode);
    }

    [Fact]
    public async Task MalformedJsonReturnsAProtocolDiagnosticWithoutLogs()
    {
        var response = await RunRawWorkerAsync("{");

        Assert.Equal("ProtocolError", response.Status);
        Assert.Equal("MalformedJson", response.ErrorCode);
    }

    [Fact]
    public async Task InvalidExportedFunctionNameIsRejectedBeforeExecution()
    {
        var valid = await ValidRequestJsonAsync();
        var response = await RunRawWorkerAsync(valid.Replace("\"function\":\"main\"", "\"function\":\"bad name\"", StringComparison.Ordinal));

        Assert.Equal("InvalidFunction", response.ErrorCode);
    }

    [Fact]
    public async Task DirectWorkerHarnessCanCancelAnUnboundedInvocation()
    {
        Assert.True(File.Exists(WorkerPath), $"Worker was not built at {WorkerPath}.");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(WorkerPath);
        using var process = Process.Start(startInfo)!;
        var request = new MediaWikiScribuntoWorkerRequest(
            MediaWikiScribuntoWorkerProtocol.CurrentVersion,
            "cancel-correlation",
            "Module:Test",
            "Module:Test@fixture",
            "test-wiki",
            "fixture",
            "return { main = function() while true do end end }",
            "main",
            [],
            new Dictionary<string, string>(),
            new(100_000, 128, 100_000, 25_000, 100_000, 100_000_000, 16, 4_000_000, 2_000_000, 100_000, 64L * 1024 * 1024, 250),
            MediaWikiScribuntoWorkerProtocol.DefaultCapabilities);
        await process.StandardInput.WriteLineAsync(MediaWikiScribuntoWorkerProtocol.SerializeRequest(request));
        try { process.StandardInput.Close(); } catch (IOException) { }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelled = false;
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; process.Kill(entireProcessTree: true); }
        await process.WaitForExitAsync();

        Assert.True(cancelled);
        Assert.True(process.HasExited);
    }

    [Fact]
    public void CompatibilityFixtureContainsTheRequiredSyntheticModuleCases()
    {
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures/scribunto-moonsharp-compatibility-fixture.xml"));
        var document = XDocument.Load(fixture);
        XNamespace mediaWiki = "http://www.mediawiki.org/xml/export-0.11/";
        var pages = document.Descendants(mediaWiki + "page")
            .ToDictionary(page => (string)page.Element(mediaWiki + "title")!, page => (string)page.Descendants(mediaWiki + "text").Last());

        Assert.Contains("Compatibility", pages.Keys);
        Assert.Contains("NestedCalls", pages.Keys);
        Assert.Contains("mw.text.encode", pages["TextUtilities"]);
        Assert.Contains("MissingExport", pages.Keys);
        Assert.Contains("MalformedModule", pages.Keys);
        Assert.Contains("UnsupportedCapability", pages.Keys);
        Assert.Contains("ResourceExhaustion", pages.Keys);
        Assert.Contains("require('Module:NotImported')", pages["MissingStaticDependency"]);
        Assert.Contains("dynamicName", pages["InvalidDependency"]);
    }

    [Fact]
    public void ExperimentalExecutorCannotBeEnabledWithoutOsIsolation()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ExperimentalMediaWikiScribuntoExecutor(
            new ExperimentalMediaWikiScribuntoExecutorOptions(
                WorkerAssemblyPath: "/tmp/worker.dll",
                EnableExperimentalExecution: true,
                RequireBubblewrap: false)));

        Assert.Contains("bubblewrap", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExperimentalExecutorRequiresADelegatedWorkerCgroup()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ExperimentalMediaWikiScribuntoExecutor(
            new ExperimentalMediaWikiScribuntoExecutorOptions(
                WorkerPath,
                EnableExperimentalExecution: true,
                RequireBubblewrap: true)));

        Assert.Contains("cgroup", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultExperimentalExecutorModeRejectsWithoutStartingWorker()
    {
        using var executor = new ExperimentalMediaWikiScribuntoExecutor(
            new ExperimentalMediaWikiScribuntoExecutorOptions("/tmp/not-used.dll"));
        var invocation = new MediaWikiScribuntoInvocation(
            "{{#invoke:Test|main}}",
            new MediaWikiModuleTitle("Test"),
            "main",
            [],
            new Dictionary<string, string>(),
            "Article",
            "page",
            "revision",
            [],
            System.Globalization.CultureInfo.InvariantCulture,
            TimeZoneInfo.Utc,
            new(0, 20));
        var context = new MediaWikiScribuntoExecutionContext(
            new("Article", "page", "revision", DateTime.UtcNow, "wikitext", new MediaWikiSourceWikiIdentity("fixture")),
            new(),
            0,
            []);

        var result = await executor.ExecuteAsync(invocation, new Host(), context);

        Assert.False(result.Executed);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ScribuntoExecutionRejected");
    }

    private static async Task<WorkerResponse> RunWorkerAsync(
        string source,
        string function,
        IReadOnlyList<string>? positional = null,
        IReadOnlyDictionary<string, string>? named = null,
        int maxSourceCharacters = 100_000,
        int maxResultCharacters = 100_000,
        long maxInstructionCount = 100_000,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<MediaWikiScribuntoWorkerModuleDependency>? dependencies = null,
        IReadOnlyList<string>? rootDependencies = null,
        long maxDependencySourceCharacters = 4_000_000,
        string? frameTitle = null,
        string? parentFrameTitle = null,
        string? parentFrameSourceRevisionId = null,
        IReadOnlyList<string>? invocationChain = null,
        string moduleTitle = "Module:Test",
        string sourceRevisionId = "fixture",
        string sourceWikiIdentity = "test-wiki")
    {
        Assert.True(File.Exists(WorkerPath), $"Worker was not built at {WorkerPath}.");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(WorkerPath);
        using var process = Process.Start(startInfo)!;
        var request = new MediaWikiScribuntoWorkerRequest(
            MediaWikiScribuntoWorkerProtocol.CurrentVersion,
            "test-correlation",
            moduleTitle,
            $"{moduleTitle}@{sourceRevisionId}",
            sourceWikiIdentity,
            sourceRevisionId,
            source,
            function,
            positional ?? [],
            named ?? new Dictionary<string, string>(),
            new(maxSourceCharacters, 128, 100_000, 25_000, maxResultCharacters, maxInstructionCount, 16, maxDependencySourceCharacters, 2_000_000, 100_000, 64L * 1024 * 1024, 250),
            capabilities ?? MediaWikiScribuntoWorkerProtocol.DefaultCapabilities,
            dependencies,
            rootDependencies ?? [],
            frameTitle,
            parentFrameTitle,
            parentFrameSourceRevisionId,
            invocationChain ?? []);
        await process.StandardInput.WriteLineAsync(MediaWikiScribuntoWorkerProtocol.SerializeRequest(request));
        try { process.StandardInput.Close(); } catch (IOException) { }
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(MediaWikiScribuntoWorkerProtocol.TryParseResponse(output.Trim(), 250_000, "test-correlation", out var response, out _, out var message), message);
        return new WorkerResponse(response!.Status == "Succeeded", response.Text, response.Diagnostics.FirstOrDefault()?.Code, response.Diagnostics.FirstOrDefault()?.Message, response.Status, response.Diagnostics, response.Dependencies ?? []);
    }

    private static async Task<string> ValidRequestJsonAsync()
    {
        var request = new MediaWikiScribuntoWorkerRequest(
            MediaWikiScribuntoWorkerProtocol.CurrentVersion,
            "raw-correlation",
            "Module:Test",
            "Module:Test@fixture",
            "test-wiki",
            "fixture",
            "return { main = function() return 'ok' end }",
            "main",
            [],
            new Dictionary<string, string>(),
            new(100_000, 128, 100_000, 25_000, 100_000, 100_000, 16, 4_000_000, 2_000_000, 100_000, 64L * 1024 * 1024, 250),
            MediaWikiScribuntoWorkerProtocol.DefaultCapabilities);
        return await Task.FromResult(MediaWikiScribuntoWorkerProtocol.SerializeRequest(request));
    }

    private static async Task<WorkerResponse> RunRawWorkerAsync(string json, bool preserveSequence = false)
    {
        Assert.True(File.Exists(WorkerPath), $"Worker was not built at {WorkerPath}.");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(WorkerPath);
        using var process = Process.Start(startInfo)!;
        try
        {
            await process.StandardInput.WriteAsync(json);
            await process.StandardInput.WriteAsync(preserveSequence ? "\n{}\n" : "\n");
        }
        catch (IOException)
        {
            // The bounded worker may close stdin as soon as it rejects an oversized line.
        }
        try { process.StandardInput.Close(); } catch (IOException) { }
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var parsed = JsonSerializer.Deserialize<MediaWikiScribuntoWorkerResponse>(output.Trim(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(parsed);
        return new WorkerResponse(parsed!.Status == "Succeeded", parsed.Text, parsed.Diagnostics.FirstOrDefault()?.Code, parsed.Diagnostics.FirstOrDefault()?.Message, parsed.Status, parsed.Diagnostics, parsed.Dependencies ?? []);
    }

    private static MediaWikiScribuntoWorkerModuleDependency Module(
        string name,
        string source,
        string pageId,
        string revisionId,
        IReadOnlyList<string>? dependencies = null,
        string contentModel = "Scribunto")
        => new("test-wiki", $"Module:{name}", pageId, revisionId, contentModel,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))), source, dependencies ?? []);

    private sealed record WorkerResponse(
        bool Success,
        string? Text,
        string? ErrorCode,
        string? ErrorMessage,
        string Status,
        IReadOnlyList<MediaWikiScribuntoWorkerDiagnostic> Diagnostics,
        IReadOnlyList<MediaWikiScribuntoWorkerDependencyReport> Dependencies);

    private sealed class Host : IMediaWikiScribuntoExecutionHost
    {
        public Task<string?> ResolveModuleSourceAsync(MediaWikiModuleTitle module, DateTime? asOfUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
        public Task<string?> ResolveTemplateTextAsync(string templateTitle, DateTime? asOfUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
        public Task<MediaWikiScribuntoPageMetadata?> ResolvePageMetadataAsync(string title, DateTime? asOfUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<MediaWikiScribuntoPageMetadata?>(null);
        public void RegisterDependency(MediaWikiScribuntoDependency dependency) { }
    }
}
