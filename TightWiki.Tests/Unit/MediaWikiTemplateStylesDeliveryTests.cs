using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TightWiki;
using TightWiki.Controllers;
using TightWiki.Engine.MediaWiki;
using TightWiki.Library.Caching;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using TightWiki.ViewModels.Page;
using System.Reflection;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiTemplateStylesDeliveryTests
{
    [Fact]
    public async Task InvalidAssetIdentifiersFailClosedWithSecurityHeaders()
    {
        var controller = CreateController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var result = await controller.TemplateStyles("not-a-signed-asset");

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("private, no-store", controller.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("no-cache", controller.Response.Headers["Pragma"].ToString());
    }

    [Fact]
    public async Task MissingAndOversizedAssetIdentifiersDoNotReachStorage()
    {
        var controller = CreateController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        Assert.IsType<NotFoundResult>(await controller.TemplateStyles(null));
        Assert.IsType<NotFoundResult>(await controller.TemplateStyles(new string('x', 8193)));
    }

    [Fact]
    public async Task SignedAssetIsReResolvedAndReturnsOnlySanitizedCss()
    {
        var sourceCss = ".example { color: red; }";
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceCss)));
        var provenance = new TwMediaWikiRenderSource(
            TwPageSourceFormat.ImportedMediaWiki,
            7,
            SourceTimestampUtc: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var sanitized = MediaWikiTemplateStylesCompatibility.SanitizeCss(sourceCss, new MediaWikiWikitextRenderOptions());
        var sanitizedCss = sanitized.SanitizedCss!;
        var sanitizedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitizedCss)));
        var repository = new FakeImportRepository(sourceCss, provenance.SourceTimestampUtc);
        var pageRepository = DispatchProxy.Create<ITwApplicationPageRepository, LivePageProxy>();
        Assert.NotNull(await pageRepository.GetPageByIdAsync(22));
        var controller = CreateController(repository, pageRepository);
        PrepareSession(controller, pageRepository, administrator: true);
        var token = CreateAssetToken(sourceHash, sanitizedHash, provenance.SourceTimestampUtc);

        // MVC query binding decodes the URL-escaped token before the action
        // receives it; emulate that boundary in the direct action test.
        var result = await controller.TemplateStyles(Uri.UnescapeDataString(token));

        Assert.True(repository.PageResolved, "The source page authorization lookup did not run.");
        Assert.True(repository.TemplateResolved, "The exact TemplateStyles source lookup did not run.");
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/css; charset=utf-8", content.ContentType);
        Assert.Equal(sanitizedCss, content.Content);
        Assert.DoesNotContain(sourceCss, content.Content, StringComparison.Ordinal);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
    }

    [Fact]
    public async Task UnauthorizedSourceReturnsNotFoundWithoutResolvingCss()
    {
        var repository = new FakeImportRepository(".example { color: red; }", DateTime.UtcNow);
        var pageRepository = DispatchProxy.Create<ITwApplicationPageRepository, LivePageProxy>();
        var controller = CreateController(repository, pageRepository);
        PrepareSession(controller, pageRepository, administrator: false, permissions: []);
        var token = CreateAssetToken(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(".example { color: red; }"))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(".wiki-page-body .example {color: red;}"))),
            DateTime.UtcNow);

        var result = await controller.TemplateStyles(Uri.UnescapeDataString(token));

        Assert.IsType<NotFoundResult>(result);
        Assert.False(repository.TemplateResolved);
    }

    [Fact]
    public async Task DeletedSourceReturnsNotFoundBeforeCssResolution()
    {
        var repository = new FakeImportRepository(".example { color: red; }", DateTime.UtcNow);
        var pageRepository = DispatchProxy.Create<ITwApplicationPageRepository, LivePageProxy>();
        ((LivePageProxy)(object)pageRepository).ReturnLivePage = false;
        var controller = CreateController(repository, pageRepository);
        PrepareSession(controller, pageRepository, administrator: true);
        var token = CreateAssetToken(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(".example { color: red; }"))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(".wiki-page-body .example {color: red;}"))),
            DateTime.UtcNow);

        var result = await controller.TemplateStyles(Uri.UnescapeDataString(token));

        Assert.IsType<NotFoundResult>(result);
        Assert.False(repository.TemplateResolved);
    }

    [Fact]
    public async Task StaleRevisionIdentityReturnsNotFoundAfterReResolution()
    {
        var sourceCss = ".example { color: red; }";
        var repository = new FakeImportRepository(sourceCss, DateTime.UtcNow);
        var pageRepository = DispatchProxy.Create<ITwApplicationPageRepository, LivePageProxy>();
        var controller = CreateController(repository, pageRepository);
        PrepareSession(controller, pageRepository, administrator: true);
        var sanitized = MediaWikiTemplateStylesCompatibility.SanitizeCss(sourceCss, new MediaWikiWikitextRenderOptions()).SanitizedCss!;
        var token = CreateAssetToken(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceCss))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized))),
            repository.Timestamp,
            sourceRevisionId: "stale-revision");

        var result = await controller.TemplateStyles(Uri.UnescapeDataString(token));

        Assert.IsType<NotFoundResult>(result);
        Assert.True(repository.TemplateResolved);
    }

    [Fact]
    public async Task ActualHttpEndpointUsesMvcRoutingHeadersAndPermissionBoundary()
    {
        var sourceCss = ".example { color: red; }";
        var repository = new FakeImportRepository(sourceCss, DateTime.UtcNow);
        var pageRepository = DispatchProxy.Create<ITwApplicationPageRepository, LivePageProxy>();
        await using var host = await TemplateStylesHttpHost.StartAsync(repository, pageRepository);

        var sanitized = MediaWikiTemplateStylesCompatibility.SanitizeCss(
            sourceCss,
            new MediaWikiWikitextRenderOptions()).SanitizedCss!;
        var token = CreateAssetToken(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceCss))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized))),
            repository.Timestamp);

        using var authorizedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/Page/TemplateStyles?t={token}", UriKind.Relative));
        authorizedRequest.Headers.Add("X-CW-Test-Read", "allow");
        using var authorized = await host.Client.SendAsync(authorizedRequest);

        var authorizedBody = await authorized.Content.ReadAsStringAsync();
        Assert.True(authorized.StatusCode == HttpStatusCode.OK,
            $"Expected an authorized 200 response but received {(int)authorized.StatusCode}: {authorizedBody}");
        Assert.Equal("text/css", authorized.Content.Headers.ContentType?.MediaType);
        Assert.Contains("charset=utf-8", authorized.Content.Headers.ContentType?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("nosniff", authorized.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("private", authorized.Headers.CacheControl?.Private is true ? "private" : null);
        Assert.True(authorized.Headers.CacheControl?.NoStore);
        Assert.Equal(sanitized, authorizedBody);
        Assert.DoesNotContain(sourceCss, authorizedBody, StringComparison.Ordinal);

        using var unauthorizedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/Page/TemplateStyles?t={token}", UriKind.Relative));
        unauthorizedRequest.Headers.Add("X-CW-Test-Read", "deny");
        using var unauthorized = await host.Client.SendAsync(unauthorizedRequest);

        Assert.Equal(HttpStatusCode.NotFound, unauthorized.StatusCode);
        Assert.Equal("nosniff", unauthorized.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.DoesNotContain("color: red", await unauthorized.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageDisplayPropagatesImportedStylesheetUrlIntoViewModel()
    {
        var timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sourceCss = ".example { color: red; }";
        var repository = new FakeImportRepository(
            sourceCss,
            timestamp,
            new TwMediaWikiRenderSource(
                TwPageSourceFormat.ImportedMediaWiki,
                ImportBatchId: 7,
                SourceTimestampUtc: timestamp));
        var pageRepository = DispatchProxy.Create<ITwApplicationPageRepository, LivePageProxy>();
        var pageProxy = (LivePageProxy)(object)pageRepository;
        pageProxy.DisplayPage = new TwPage
        {
            Id = 10,
            Name = "Home",
            Navigation = "Home",
            Revision = 1,
            MostCurrentRevision = 1,
            ModifiedDate = timestamp,
            Body = "<templatestyles src=\"Template:Example/styles.css\" />\n<div class=\"example\">Example</div>"
        };

        var configuration = new TwConfiguration
        {
            DefaultTimeZone = TimeZoneInfo.Utc.Id,
            PageCacheSeconds = 0,
            EnableSidebar = false,
            EnablePageComments = false,
            ShowCommentsOnPageFooter = false
        };
        var applicationConfiguration = DispatchProxy.Create<ITwApplicationConfigurationRepository, DisplayConfigurationProxy>();
        var applicationOperations = DispatchProxy.Create<ITwApplicationOperationsRepository, DisplayOperationsProxy>();
        var renderer = new MediaWikiWikitextRenderer(repository, configuration);
        var controller = new MediaWikiPageDisplayTestAction(
            pageRepository,
            null!,
            repository,
            renderer,
            applicationOperations,
            applicationConfiguration,
            configuration);
        var services = new ServiceCollection()
            .AddSingleton<ITwApplicationPageRepository>(pageRepository)
            .BuildServiceProvider();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = services }
        };
        controller.TempData = new TempDataDictionary(controller.HttpContext, new EmptyTempDataProvider());
        typeof(TwSessionState)
            .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller.SessionState, services);
        controller.SessionState.WikiConfiguration = configuration;
        controller.SessionState.Permissions =
        [
            new TwApparentPermission
            {
                Permission = TightWiki.Plugin.TwPermission.Read.ToString(),
                PermissionDisposition = TightWiki.Plugin.TwPermissionDisposition.Allow.ToString(),
                PageId = "10"
            }
        ];
        TwSignedUrl.Initialize(new EphemeralDataProtectionProvider());

        var result = await controller.Display("Home", null);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PageDisplayViewModel>(view.Model);
        var stylesheetUrl = Assert.Single(model.StylesheetUrls);
        Assert.StartsWith("/Page/TemplateStyles?t=", stylesheetUrl, StringComparison.Ordinal);
        Assert.NotEmpty(model.Body);
        Assert.DoesNotContain("<templatestyles", model.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sourceCss, model.Body, StringComparison.Ordinal);
    }

    private static PageController CreateController(
        ITwMediaWikiImportRepository? importRepository = null,
        ITwApplicationPageRepository? pageRepository = null,
        MediaWikiWikitextRenderer? mediaWikiRenderer = null)
    {
        var renderer = mediaWikiRenderer
            ?? (importRepository is null ? null : new MediaWikiWikitextRenderer(importRepository, new TwConfiguration()));
        return new(
            NullLogger<ITwEngine>.Instance,
            null!,
            null!,
            pageRepository ?? null!,
            null!,
            importRepository ?? null!,
            renderer!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new TwConfiguration(),
            null!);
    }

    private static void PrepareSession(
        PageController controller,
        ITwApplicationPageRepository pageRepository,
        bool administrator,
        IReadOnlyList<TwApparentPermission>? permissions = null)
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var services = new ServiceCollection()
            .AddSingleton(pageRepository)
            .BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        typeof(TwSessionState)
            .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller.SessionState, services);
        controller.SessionState.IsAdministrator = administrator;
        controller.SessionState.Permissions = permissions?.ToList() ?? [];
    }

    private static string CreateAssetToken(
        string sourceHash,
        string sanitizedHash,
        DateTime? timestamp,
        string sourceRevisionId = "style-rev")
    {
        TwSignedUrl.Initialize(new EphemeralDataProtectionProvider());
        return TwSignedUrl.Protect(new MediaWikiStylesheetAssetPayload(
            7,
            "Template:Example/styles.css",
            "style-page",
            sourceRevisionId,
            1,
            timestamp,
            sourceHash,
            MediaWikiTemplateStylesCompatibility.SanitizerPolicyVersion,
            sanitizedHash), TimeSpan.FromMinutes(5));
    }

    private sealed class FakeImportRepository(
        string css,
        DateTime? timestamp,
        TwMediaWikiRenderSource? renderSource = null) : ITwMediaWikiImportRepository
    {
        public DateTime? Timestamp { get; } = timestamp;
        public TwMediaWikiRenderSource RenderSource { get; } = renderSource ?? TwMediaWikiRenderSource.Native;
        public bool PageResolved { get; private set; }
        public bool TemplateResolved { get; private set; }

        public Task<TwMediaWikiRenderSource> GetRenderSourceAsync(int targetPageId, int targetRevision, CancellationToken cancellationToken = default)
            => Task.FromResult(RenderSource);

        public Task<TwMediaWikiPageTarget?> ResolvePageAsync(long importBatchId, string sourceTitle, CancellationToken cancellationToken = default)
        {
            PageResolved = true;
            return Task.FromResult<TwMediaWikiPageTarget?>(new("Template:Example/styles.css", "Template:Example/styles.css", 22));
        }

        public Task<TwMediaWikiMediaTarget?> ResolveMediaAsync(long importBatchId, string sourcePageId, string sourceRevisionId, string sourceFileName, CancellationToken cancellationToken = default)
            => Task.FromResult<TwMediaWikiMediaTarget?>(null);

        public Task<TwMediaWikiTemplateTarget> ResolveTemplateAsync(long importBatchId, string sourceTitle, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
        {
            TemplateResolved = true;
            return Task.FromResult(new TwMediaWikiTemplateTarget(
                TwMediaWikiTemplateResolutionStatus.Resolved,
                "Template:Example/styles.css",
                "style-page",
                "style-rev",
                1,
                css,
                Timestamp,
                "css"));
        }

        public Task<string> GetTemplateCacheVersionAsync(long importBatchId, DateTime? asOfUtc = null, CancellationToken cancellationToken = default)
            => Task.FromResult("fake");
    }

    private class LivePageProxy : DispatchProxy
    {
        public bool ReturnLivePage { get; set; } = true;
        public TwPage? DisplayPage { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ITwApplicationPageRepository.GetPageByIdAsync))
            {
                var requestedId = args?[0] is int id ? id : 0;
                if (DisplayPage is not null && requestedId == DisplayPage.Id)
                    return Task.FromResult<TwPage?>(DisplayPage);
                return Task.FromResult<TwPage?>(ReturnLivePage ? new TwPage { Id = 22, Navigation = "Template:Example/styles.css" } : null);
            }
            if (targetMethod?.Name == nameof(ITwApplicationPageRepository.GetPageByNavigationAsync))
            {
                var requestedNavigation = args?[0]?.ToString();
                if (DisplayPage is not null && string.Equals(requestedNavigation, DisplayPage.Navigation, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<TwPage?>(DisplayPage);
                return Task.FromResult<TwPage?>(new TwPage { Id = 22, Navigation = "Template:Example/styles.css" });
            }
            if (targetMethod?.Name == nameof(ITwApplicationPageRepository.GetProcessingInstructionsAsync))
                return Task.FromResult(new TwProcessingInstructionCollection());
            throw new InvalidOperationException($"Unexpected page repository call: {targetMethod?.Name}");
        }
    }

    private class DisplayConfigurationProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(ITwApplicationConfigurationRepository.GetEntryValueAsync)
                ? Task.FromResult<string?>(null)
                : throw new InvalidOperationException($"Unexpected configuration call: {targetMethod?.Name}");
    }

    private class DisplayOperationsProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(ITwApplicationOperationsRepository.IncrementPageViewCountAsync)
                ? Task.FromResult(0)
                : throw new InvalidOperationException($"Unexpected operations call: {targetMethod?.Name}");
    }

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }

    private sealed class SingleControllerFeatureProvider(Type controllerType)
        : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(controllerType.GetTypeInfo());
        }
    }

    private sealed class TemplateStylesHttpHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public HttpClient Client { get; } = client;

        public static async Task<TemplateStylesHttpHost> StartAsync(
            ITwMediaWikiImportRepository importRepository,
            ITwApplicationPageRepository pageRepository)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(MediaWikiTemplateStylesHttpController).Assembly.GetName().Name,
                EnvironmentName = "Testing"
            });
            builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");

            var configuration = new TwConfiguration();
            var renderer = new MediaWikiWikitextRenderer(importRepository, configuration);
            var dataProtection = new EphemeralDataProtectionProvider();

            builder.Services.AddSingleton<ITwApplicationPageRepository>(pageRepository);
            builder.Services.AddSingleton<ITwMediaWikiImportRepository>(importRepository);
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(renderer);
            builder.Services.AddSingleton<IDataProtectionProvider>(dataProtection);
            builder.Services.AddTransient<MediaWikiTemplateStylesHttpController>(_ =>
                new MediaWikiTemplateStylesHttpController(
                    pageRepository,
                    importRepository,
                    renderer,
                    configuration));
            builder.Services.AddControllersWithViews()
                .AddApplicationPart(typeof(MediaWikiTemplateStylesHttpController).Assembly)
                .ConfigureApplicationPartManager(manager =>
                    manager.FeatureProviders.Add(new SingleControllerFeatureProvider(typeof(MediaWikiTemplateStylesHttpController))));

            var app = builder.Build();
            TwSignedUrl.Initialize(dataProtection);
            app.UseDeveloperExceptionPage();
            app.UseRouting();
            app.MapControllers();
            await app.StartAsync();

            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            return new TemplateStylesHttpHost(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}

/// <summary>
/// Test-only MVC host controller. It routes the production PageController
/// action while replacing only the base controller's account hydration with a
/// request-scoped permission fixture. The production controller and evaluator
/// remain the code under test.
/// </summary>
public sealed class MediaWikiTemplateStylesHttpController(
    ITwApplicationPageRepository pageRepository,
    ITwMediaWikiImportRepository importRepository,
    MediaWikiWikitextRenderer renderer,
    TwConfiguration configuration)
    : PageController(
        NullLogger<ITwEngine>.Instance,
        null!,
        null!,
        pageRepository,
        null!,
        importRepository,
        renderer,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        configuration,
        null!)
{
    public override async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        typeof(TwSessionState)
            .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(SessionState, context.HttpContext.RequestServices);

        var userId = Guid.NewGuid();
        if (Guid.TryParse(context.HttpContext.Request.Headers["X-CW-Test-User"].FirstOrDefault(), out var requestedUserId))
            userId = requestedUserId;

        SessionState.IsAdministrator = false;
        SessionState.Profile = new TwAccountProfile { UserId = userId };
        SessionState.Permissions = string.Equals(
                context.HttpContext.Request.Headers["X-CW-Test-Read"].FirstOrDefault(),
                "allow",
                StringComparison.Ordinal)
            ? [new TwApparentPermission
            {
                Permission = TightWiki.Plugin.TwPermission.Read.ToString(),
                PermissionDisposition = TightWiki.Plugin.TwPermissionDisposition.Allow.ToString(),
                PageId = "22"
            }]
            : [];

        await next();
    }
}

/// <summary>
/// Test-only controller for exercising the real PageController display model
/// handoff with an imported MediaWiki page and disposable repository doubles.
/// </summary>
public sealed class MediaWikiPageDisplayTestAction(
    ITwApplicationPageRepository pageRepository,
    ITwApplicationPageSupportRepository pageSupportRepository,
    ITwMediaWikiImportRepository importRepository,
    MediaWikiWikitextRenderer renderer,
    ITwApplicationOperationsRepository operationsRepository,
    ITwApplicationConfigurationRepository configurationRepository,
    TwConfiguration configuration)
    : PageController(
        NullLogger<ITwEngine>.Instance,
        null!,
        null!,
        pageRepository,
        pageSupportRepository,
        importRepository,
        renderer,
        operationsRepository,
        configurationRepository,
        null!,
        null!,
        null!,
        null!,
        configuration,
        null!)
{
}
