using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces;
using TightWiki.Plugin.Models;
using TightWiki.ViewModels.Category;
using TwPermission = TightWiki.Plugin.TwPermission;

namespace TightWiki.Controllers;

/// <summary>Read-only native Category namespace browsing.</summary>
[Route("Category")]
public sealed class CategoryController(
    ILogger<ITwEngine> logger,
    ITwCategoryBrowseService browseService,
    ITwSharedLocalizationText localizer,
    SignInManager<IdentityUser> signInManager,
    TwConfiguration wikiConfiguration,
    UserManager<IdentityUser> userManager)
    : TwController<CategoryController>(logger, signInManager, userManager, localizer, wikiConfiguration)
{
    [AllowAnonymous]
    [HttpGet("{*categoryTitle}")]
    public async Task<ActionResult> Browse(string? categoryTitle)
    {
        var pageNumber = GetQueryValue("page", 1);
        var pageSize = GetQueryValue("pageSize", 50);

        if (TwCategoryIdentity.TryCreateFromNavigation(categoryTitle, out var requestedCategory))
        {
            try
            {
                // Authorize the category namespace before asking the category
                // repository for readiness or membership data.
                await SessionState.RequirePermission(requestedCategory.CanonicalNavigation, TwPermission.Read);
            }
            catch (Exception ex)
            {
                return NotifyOfError(ex.GetBaseException().Message, "/");
            }
        }

        var result = await browseService.BrowseAsync(
            categoryTitle,
            SessionState.Profile?.UserId,
            pageNumber,
            pageSize,
            HttpContext.RequestAborted);

        if (result.Category is not null)
        {
            SessionState.Page.Name = Localize("Category: {0}", result.Category.NormalizedTitle);
        }
        else
        {
            SessionState.Page.Name = Localize("Category");
        }

        return View(CategoryBrowseViewModel.From(result, pageNumber, pageSize));
    }
}
