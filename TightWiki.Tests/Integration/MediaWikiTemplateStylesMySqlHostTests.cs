using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Dapper;
using MediaWikiImport;
using MediaWikiInventory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using TightWiki.Controllers;
using TightWiki.Engine.MediaWiki;
using TightWiki.Library;
using TightWiki.Library.Database;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Repository.Helpers;
using TightWiki.Translations;

namespace TightWiki.Tests.Integration;

/// <summary>
/// Real cookie and persisted-permission coverage. It is intentionally not
/// runnable unless an operator supplies the separate M5-003 authorization
/// marker and an explicitly disposable cw_m5_003_* database target.
/// </summary>
public sealed class MediaWikiTemplateStylesMySqlHostTests
{
    [M5HostFact]
    public async Task RealCookiePageRenderStylesheetAuthorizationAndRevocationAreEnforced()
    {
        var sourceConnection = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION")!;
        var databaseName = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_003_TARGET_DATABASE")!;
        var targetBuilder = new MySqlConnectionStringBuilder(sourceConnection) { Database = databaseName };

        await using (var runtimeConnection = new MySqlConnection(targetBuilder.ConnectionString))
        {
            await runtimeConnection.OpenAsync();
            var runtime = await runtimeConnection.QuerySingleAsync<MySqlHostRuntimeEvidence>("SELECT DATABASE() AS DatabaseName, VERSION() AS ServerVersion, @@version_comment AS ServerComment;");
            Assert.Equal(databaseName, runtime.DatabaseName);
            Assert.StartsWith("8.4.", runtime.ServerVersion, StringComparison.Ordinal);
            Assert.DoesNotContain("MariaDB", runtime.ServerVersion, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("MySQL", runtime.ServerComment, StringComparison.OrdinalIgnoreCase);
        }

        var fixturePath = Path.Combine(Path.GetTempPath(), $"cw-m5-003-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(fixturePath, FixtureXml);
            var factory = new MySqlDatabaseConnectionFactory(targetBuilder.ConnectionString);
            await new MySqlSchemaInitializer().InitializeAsync(factory);
            var target = new MediaWikiImportTargetConfiguration(targetBuilder.ConnectionString, databaseName, "disposable-mysql");
            var importExecutor = new MediaWikiImportExecutor(factory, target);
            var plan = MediaWikiImportPlanner.Create(fixturePath, NamespaceMappingConfiguration.Defaults);
            var imported = await importExecutor.ImportAsync(plan, fixturePath, NamespaceMappingConfiguration.Defaults);
            var importRepository = new MySqlMediaWikiImportRepository(factory);
            var article = (await importRepository.ResolvePageAsync(imported.ImportBatchId, "Article"))!;
            var stylePage = (await importRepository.ResolvePageAsync(imported.ImportBatchId, "Template:Example/styles.css"))!;

            await using var host = await MySqlTemplateStylesHost.StartAsync(targetBuilder.ConnectionString);
            var identity = host.Services.GetRequiredService<ITwApplicationIdentityRepository>();
            var permissionId = await identity.EnsurePermissionAsync(TwPermission.Read.ToString());
            var authorized = await host.CreateUserAsync("authorized");
            var unauthorized = await host.CreateUserAsync("unauthorized");
            var articlePermissionRowId = await identity.AddAccountPermissionAsync(
                authorized.UserId,
                permissionId,
                TwPermissionDisposition.Allow.ToString(),
                pageId: article.TargetPageId.ToString());
            var stylesheetPermissionRowId = await identity.AddAccountPermissionAsync(
                authorized.UserId,
                permissionId,
                TwPermissionDisposition.Allow.ToString(),
                pageId: stylePage.TargetPageId.ToString());

            using var authorizedClient = host.CreateClient();
            using var unauthorizedClient = host.CreateClient();
            await host.SignInAsync(authorizedClient, authorized.UserName, authorized.Password);
            await host.SignInAsync(unauthorizedClient, unauthorized.UserName, unauthorized.Password);

            using var pageResponse = await authorizedClient.GetAsync("/__m5-003/render/Article");
            Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
            var page = await pageResponse.Content.ReadFromJsonAsync<RenderedPage>();
            Assert.NotNull(page);
            Assert.Contains(".wiki-page-body", page!.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("color: red", page.Html, StringComparison.Ordinal);
            Assert.NotNull(page.StylesheetUrl);
            var stylesheetUrl = page.StylesheetUrl!;
            Assert.StartsWith("/Page/TemplateStyles?t=", stylesheetUrl, StringComparison.Ordinal);

            using var cssResponse = await authorizedClient.GetAsync(stylesheetUrl);
            Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
            Assert.Equal("text/css", cssResponse.Content.Headers.ContentType?.MediaType);
            var cssBody = await cssResponse.Content.ReadAsStringAsync();
            Assert.Contains("red", cssBody, StringComparison.Ordinal);
            Assert.DoesNotContain("blue", cssBody, StringComparison.Ordinal);
            Assert.True(cssResponse.Headers.TryGetValues("X-Content-Type-Options", out var nosniff));
            Assert.Equal("nosniff", nosniff.Single());
            Assert.True(cssResponse.Headers.CacheControl?.NoStore);
            Assert.True(cssResponse.Headers.CacheControl?.Private);
            Assert.NotEqual(HttpStatusCode.NotModified, (await ConditionalAsync(authorizedClient, stylesheetUrl)).StatusCode);

            using var logoutResponse = await authorizedClient.PostAsync("/__m5-003/sign-out", content: null);
            Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);
            using var afterLogout = await authorizedClient.GetAsync(stylesheetUrl);
            Assert.Equal(HttpStatusCode.NotFound, afterLogout.StatusCode);
            await host.SignInAsync(authorizedClient, authorized.UserName, authorized.Password);

            var concurrentResponses = await Task.WhenAll(
                authorizedClient.GetAsync(stylesheetUrl),
                unauthorizedClient.GetAsync(stylesheetUrl));
            try
            {
                Assert.Equal(HttpStatusCode.OK, concurrentResponses[0].StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, concurrentResponses[1].StatusCode);
            }
            finally
            {
                foreach (var response in concurrentResponses) response.Dispose();
            }

            using var deniedCss = await unauthorizedClient.GetAsync(stylesheetUrl);
            Assert.Equal(HttpStatusCode.NotFound, deniedCss.StatusCode);

            await using (var suppressionSession = await factory.OpenSessionAsync())
            {
                await suppressionSession.Connection.ExecuteAsync("""
                    UPDATE MediaWikiImportRevision R
                    INNER JOIN MediaWikiImportPage P
                        ON P.ImportBatchId = R.ImportBatchId AND P.SourcePageId = R.SourcePageId
                    SET R.IsSuppressed = TRUE
                    WHERE R.ImportBatchId = @ImportBatchId
                      AND P.OriginalTitle = 'Template:Example/styles.css';
                    """, new { ImportBatchId = imported.ImportBatchId });
            }
            using var suppressedCss = await authorizedClient.GetAsync(stylesheetUrl);
            Assert.Equal(HttpStatusCode.NotFound, suppressedCss.StatusCode);

            await using (var unsuppressSession = await factory.OpenSessionAsync())
            {
                await unsuppressSession.Connection.ExecuteAsync("""
                    UPDATE MediaWikiImportRevision R
                    INNER JOIN MediaWikiImportPage P
                        ON P.ImportBatchId = R.ImportBatchId AND P.SourcePageId = R.SourcePageId
                    SET R.IsSuppressed = FALSE
                    WHERE R.ImportBatchId = @ImportBatchId
                      AND P.OriginalTitle = 'Template:Example/styles.css';
                    """, new { ImportBatchId = imported.ImportBatchId });
            }

            await identity.RemoveAccountPermissionAsync(articlePermissionRowId);
            await identity.RemoveAccountPermissionAsync(stylesheetPermissionRowId);
            using var revokedCss = await authorizedClient.GetAsync(stylesheetUrl);
            Assert.Equal(HttpStatusCode.NotFound, revokedCss.StatusCode);
            using var revokedConditional = await ConditionalAsync(authorizedClient, stylesheetUrl);
            Assert.Equal(HttpStatusCode.NotFound, revokedConditional.StatusCode);

            // A separate authorized identity proves source deletion independently
            // of the revoked user's permission-cache key.
            var deletionUser = await host.CreateUserAsync("deletion");
            await identity.AddAccountPermissionAsync(
                deletionUser.UserId,
                permissionId,
                TwPermissionDisposition.Allow.ToString(),
                pageId: article.TargetPageId.ToString());
            await identity.AddAccountPermissionAsync(
                deletionUser.UserId,
                permissionId,
                TwPermissionDisposition.Allow.ToString(),
                pageId: stylePage.TargetPageId.ToString());
            using var deletionClient = host.CreateClient();
            await host.SignInAsync(deletionClient, deletionUser.UserName, deletionUser.Password);
            var deletionPage = await (await deletionClient.GetAsync("/__m5-003/render/Article")).Content.ReadFromJsonAsync<RenderedPage>();
            Assert.NotNull(deletionPage?.StylesheetUrl);
            Assert.Equal(HttpStatusCode.OK, (await deletionClient.GetAsync(deletionPage!.StylesheetUrl!)).StatusCode);
            var pages = host.Services.GetRequiredService<ITwApplicationPageRepository>();
            var currentStylePage = await pages.GetPageByIdAsync(stylePage.TargetPageId);
            Assert.NotNull(currentStylePage);
            Assert.True(await pages.DeletePageAsync(stylePage.TargetPageId, currentStylePage!.Revision, deletionUser.UserId));
            using var deletedCss = await deletionClient.GetAsync(deletionPage.StylesheetUrl);
            Assert.Equal(HttpStatusCode.NotFound, deletedCss.StatusCode);
        }
        finally
        {
            try { File.Delete(fixturePath); } catch { }
        }
    }

    private static async Task<HttpResponseMessage> ConditionalAsync(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"m5-003-test\"");
        return await client.SendAsync(request);
    }

    private const string FixtureXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <mediawiki version="0.11" xmlns="http://www.mediawiki.org/xml/export-0.11/">
          <siteinfo><sitename>CW M5-003 disposable host fixture</sitename><base>https://synthetic.invalid/wiki</base><generator>MediaWiki 1.45.3</generator><case>first-letter</case>
            <namespaces><namespace key="0" case="first-letter" /><namespace key="10" case="first-letter">Template</namespace></namespaces>
          </siteinfo>
          <page><title>Article</title><ns>0</ns><id>5001</id>
            <revision><id>5101</id><timestamp>2026-01-01T00:00:00Z</timestamp><contributor><username>fixture</username><id>1</id></contributor><model>wikitext</model><format>text/x-wiki</format><text xml:space="preserve"><templatestyles src="Template:Example/styles.css" />
        <div class="example">Historical secure fixture</div></text></revision>
            <revision><id>5102</id><timestamp>2026-01-02T00:00:00Z</timestamp><contributor><username>fixture</username><id>1</id></contributor><model>wikitext</model><format>text/x-wiki</format><text xml:space="preserve"><templatestyles src="Template:Example/styles.css" />
        <div class="example">Current secure fixture</div></text></revision>
          </page>
          <page><title>Example/styles.css</title><ns>10</ns><id>5002</id>
            <revision><id>5201</id><timestamp>2026-01-01T00:00:00Z</timestamp><contributor><username>fixture</username><id>1</id></contributor><model>css</model><format>text/css</format><text xml:space="preserve">.example { color: red; }</text></revision>
            <revision><id>5202</id><timestamp>2026-01-03T00:00:00Z</timestamp><contributor><username>fixture</username><id>1</id></contributor><model>css</model><format>text/css</format><text xml:space="preserve">.example { color: blue; }</text></revision>
          </page>
        </mediawiki>
        """;

}

internal sealed record RenderedPage(string Html, string? StylesheetUrl);
internal sealed record MySqlHostRuntimeEvidence(string DatabaseName, string ServerVersion, string ServerComment);

internal sealed class M5HostFactAttribute : FactAttribute
{
    public M5HostFactAttribute()
    {
        var decision = M5HostCertificationGate.Evaluate(
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_RUN_MYSQL_TESTS"),
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION"),
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_003_HOST_AUTHORIZED"),
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_003_TARGET_DATABASE"),
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_003_TARGET_IDENTITY"),
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_003_APPROVAL_PATH"));
        if (!decision.IsAuthorized) Skip = decision.Reason;
    }
}

internal sealed class MySqlTemplateStylesHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MySqlTemplateStylesHost(WebApplication app)
    {
        _app = app;
    }

    public IServiceProvider Services => _app.Services;

    public static async Task<MySqlTemplateStylesHost> StartAsync(string connectionString)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(MediaWikiTemplateStylesHostController).Assembly.GetName().Name,
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("ConnectionStrings:MySql", connectionString)]);
        builder.Services.AddMySqlApplicationPersistence(builder.Configuration);
        builder.Services.AddMySqlIdentity(connectionString, options =>
        {
            options.SignIn.RequireConfirmedAccount = false;
            options.User.RequireUniqueEmail = false;
        });
        builder.Services.AddSingleton<ITwMediaWikiImportRepository, MySqlMediaWikiImportRepository>();
        builder.Services.AddSingleton(new TwConfiguration
        {
            BasePath = string.Empty,
            EnableSidebar = false,
            EnablePageComments = false,
            ShowCommentsOnPageFooter = false,
            PageCacheSeconds = 0
        });
        builder.Services.AddSingleton<MediaWikiWikitextRenderer>();
        builder.Services.AddDataProtection();
        builder.Services.AddLocalization();
        builder.Services.AddSingleton<IStringLocalizerFactory, TranslationStringLocalizerFactory>();
        builder.Services.AddScoped<ITwSharedLocalizationText, SharedLocalizationText>();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(MediaWikiTemplateStylesHostController).Assembly)
            .ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new SingleControllerFeatureProvider(typeof(MediaWikiTemplateStylesHostController))));

        var app = builder.Build();
        TwSignedUrl.Initialize(app.Services.GetRequiredService<IDataProtectionProvider>());
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapPost("/__m5-003/sign-in", async (SignInRequest request, SignInManager<IdentityUser> signInManager) =>
        {
            var result = await signInManager.PasswordSignInAsync(request.UserName, request.Password, false, false);
            return result.Succeeded ? Results.Ok() : Results.Unauthorized();
        });
        app.MapPost("/__m5-003/sign-out", async (SignInManager<IdentityUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Ok();
        });
        await app.StartAsync();
        return new(app);
    }

    public HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        return new HttpClient(handler) { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    public async Task<TestUser> CreateUserAsync(string label)
    {
        using var scope = Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var users = scope.ServiceProvider.GetRequiredService<ITwApplicationIdentityRepository>();
        var username = $"m5-003-{label}-{Guid.NewGuid():N}";
        var password = "M5-003-Disposable!2026";
        var user = new IdentityUser { UserName = username, Email = $"{username}@example.invalid" };
        var creation = await manager.CreateAsync(user, password);
        Assert.True(creation.Succeeded, string.Join("; ", creation.Errors.Select(error => error.Description)));
        var userId = Guid.Parse(user.Id);
        await users.CreateProfileAsync(userId, username);
        return new(userId, username, password);
    }

    public async Task SignInAsync(HttpClient client, string username, string password)
    {
        using var response = await client.PostAsJsonAsync("/__m5-003/sign-in", new SignInRequest(username, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed record SignInRequest(string UserName, string Password);
    public sealed record TestUser(Guid UserId, string UserName, string Password);

    private sealed class SingleControllerFeatureProvider(Type controllerType)
        : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(controllerType.GetTypeInfo());
        }
    }
}

[Microsoft.AspNetCore.Mvc.Route("")]
public sealed class MediaWikiTemplateStylesHostController : PageController
{
    private readonly ITwApplicationPageRepository _pageRepository;
    private readonly ITwMediaWikiImportRepository _importRepository;
    private readonly MediaWikiWikitextRenderer _renderer;

    public MediaWikiTemplateStylesHostController(
        ITwApplicationPageRepository pageRepository,
        ITwMediaWikiImportRepository importRepository,
        MediaWikiWikitextRenderer renderer,
        ITwApplicationOperationsRepository operationsRepository,
        ITwApplicationConfigurationRepository configurationRepository,
        ITwApplicationIdentityRepository identityRepository,
        ITwSharedLocalizationText localizer,
        SignInManager<IdentityUser> signInManager,
        TwConfiguration configuration,
        UserManager<IdentityUser> userManager)
        : base(
            NullLogger<ITwEngine>.Instance,
            null!,
            null!,
            pageRepository,
            null!,
            importRepository,
            renderer,
            operationsRepository,
            configurationRepository,
            null!,
            localizer,
            identityRepository,
            signInManager,
            configuration,
            userManager)
    {
        _pageRepository = pageRepository;
        _importRepository = importRepository;
        _renderer = renderer;
    }

    [Microsoft.AspNetCore.Mvc.HttpGet("/__m5-003/render/{navigation}")]
    public async Task<IActionResult> Render(string navigation)
    {
        if (!await SessionState.HoldsPermission(navigation, TwPermission.Read))
            return NotFound();

        var page = await _pageRepository.GetPageByNavigationAsync(navigation);
        if (page is null) return NotFound();
        var source = await _importRepository.GetRenderSourceAsync(page.Id, page.Revision, HttpContext.RequestAborted);
        var result = await _renderer.RenderAsync(page.Body, source, HttpContext.RequestAborted);
        var stylesheet = Assert.Single(result.Stylesheets ?? []);
        var payload = new MediaWikiStylesheetAssetPayload(
            stylesheet.ImportBatchId!.Value,
            stylesheet.SourceTitle,
            stylesheet.SourcePageId,
            stylesheet.SourceRevisionId,
            stylesheet.TargetRevision,
            stylesheet.SourceTimestampUtc,
            stylesheet.SourceSha256,
            stylesheet.SanitizerPolicyVersion,
            stylesheet.SanitizedCssSha256!);
        return Json(new RenderedPage(result.HtmlResult, $"/Page/TemplateStyles?t={TwSignedUrl.Protect(payload, TimeSpan.FromHours(1))}"));
    }
}
