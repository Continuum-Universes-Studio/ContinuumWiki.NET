using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces;
using TightWiki.Plugin.Models;
using TwPermission = TightWiki.Plugin.TwPermission;

namespace TightWiki.Controllers;

/// <summary>
/// Disabled-by-default administrative category recovery API. The optional
/// service dependency deliberately makes an unregistered API fail closed in
/// the default SQLite startup path.
/// </summary>
[Authorize]
[Route("Admin/CategoryRecovery")]
public sealed class CategoryRecoveryAdminController(
    ILogger<ITwEngine> logger,
    ITwSharedLocalizationText localizer,
    SignInManager<IdentityUser> signInManager,
    TwConfiguration wikiConfiguration,
    UserManager<IdentityUser> userManager,
    ITwCategoryAdministrativeRecoveryApi? recoveryApi = null)
    : TwController<CategoryRecoveryAdminController>(logger, signInManager, userManager, localizer, wikiConfiguration)
{
    private readonly ITwCategoryAdministrativeRecoveryApi? _recoveryApi = recoveryApi;

    [HttpGet("readiness")]
    public ActionResult Readiness()
    {
        if (_recoveryApi is null)
            return NotFound();

        return ToActionResult(_recoveryApi.Readiness(CreateCaller()));
    }

    [HttpPost("preview")]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> Preview(
        [FromBody] TwCategoryAdministrativeRecoveryRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (_recoveryApi is null)
            return NotFound();

        var response = await _recoveryApi.PreviewAsync(
            CreateCaller(),
            request!,
            AuthorizePageAsync,
            cancellationToken);
        return ToActionResult(response);
    }

    [HttpPost("execute")]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> Execute(
        [FromBody] TwCategoryAdministrativeRecoveryRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (_recoveryApi is null)
            return NotFound();

        var response = await _recoveryApi.ExecuteAsync(
            CreateCaller(),
            request!,
            AuthorizePageAsync,
            cancellationToken);
        return ToActionResult(response);
    }

    private TwCategoryAdministrativeCaller CreateCaller()
        => new(
            SessionState.IsAuthenticated,
            SessionState.IsAdministrator,
            SessionState.Profile?.UserId);

    private async Task<bool> AuthorizePageAsync(
        TwCategoryRecoveryPageAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        if (!SessionState.IsAuthenticated || !SessionState.IsAdministrator)
            return false;

        // Keep the existing page permission evaluator in the authorization
        // path. Administrators retain its established bypass behavior, while
        // a future narrower administrative identity cannot skip page checks.
        return await SessionState.HoldsPermission(context.PageNavigation, TwPermission.Edit);
    }

    private static ActionResult ToActionResult(
        TwCategoryAdministrativeReadinessResponse response)
        => Map(response.Disposition, response);

    private static ActionResult ToActionResult(
        TwCategoryAdministrativePlanResponse response)
        => Map(response.Disposition, response);

    private static ActionResult ToActionResult(
        TwCategoryAdministrativeExecutionResponse response)
        => Map(response.Disposition, response);

    private static ActionResult Map(
        TwCategoryAdministrativeApiDisposition disposition,
        object response)
        => disposition switch
        {
            TwCategoryAdministrativeApiDisposition.Disabled => new NotFoundResult(),
            TwCategoryAdministrativeApiDisposition.Unauthenticated => new UnauthorizedResult(),
            TwCategoryAdministrativeApiDisposition.Forbidden => new ForbidResult(),
            TwCategoryAdministrativeApiDisposition.InvalidInput => new BadRequestObjectResult(response),
            TwCategoryAdministrativeApiDisposition.ProviderUnavailable => new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable },
            TwCategoryAdministrativeApiDisposition.IncompleteExpansion or TwCategoryAdministrativeApiDisposition.Unsupported => new UnprocessableEntityObjectResult(response),
            TwCategoryAdministrativeApiDisposition.ConflictingRevision or TwCategoryAdministrativeApiDisposition.InconsistentIndex or TwCategoryAdministrativeApiDisposition.MutationGated or TwCategoryAdministrativeApiDisposition.RecoveryIndeterminate => new ConflictObjectResult(response),
            TwCategoryAdministrativeApiDisposition.Cancellation => new ObjectResult(response) { StatusCode = 499 },
            TwCategoryAdministrativeApiDisposition.PartialResult => new ObjectResult(response) { StatusCode = StatusCodes.Status207MultiStatus },
            TwCategoryAdministrativeApiDisposition.PersistenceFailure => new ObjectResult(response) { StatusCode = StatusCodes.Status500InternalServerError },
            _ => new OkObjectResult(response)
        };
}
