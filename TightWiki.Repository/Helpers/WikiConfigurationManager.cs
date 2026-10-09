using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using TightWiki.Library.Caching;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using static TightWiki.Plugin.TwConstants;

namespace TightWiki.Repository.Helpers
{
    public class WikiConfigurationManager
    {
        private readonly Func<bool, int, Task<List<TwEmoji>>> _reloadEmojis;
        private readonly ITwApplicationConfigurationRepository _configurationRepository;
        public TwConfiguration WikiConfiguration { get; private set; }

        public WikiConfigurationManager(IConfiguration configuration, DatabaseManager databaseManager, ITwApplicationConfigurationRepository configurationRepository)
            : this(configuration, configurationRepository, (preload, height) => databaseManager.EmojiRepository.ReloadEmojis(preload, height))
        {
        }

        public WikiConfigurationManager(IConfiguration configuration, ITwApplicationConfigurationRepository configurationRepository, Func<bool, int, Task<List<TwEmoji>>> reloadEmojis)
        {
            _configurationRepository = configurationRepository;
            _reloadEmojis = reloadEmojis;
            WikiConfiguration = new TwConfiguration()
            {
                BasePath = configuration.GetValue<string?>("BasePath") ?? string.Empty,
                ApplicationName = configuration.GetValue<string?>("Application:Name") ?? "ContinuumWiki.NET",
                OrganizationName = configuration.GetValue<string?>("Application:Organization") ?? "Continuum Universes Studio",
                ApplicationDescription = configuration.GetValue<string?>("Application:Description") ?? "A purpose-built wiki, lore database, and worldbuilding platform for Continuum Universes.",
                ProductWebsite = configuration.GetValue<string?>("Application:Website") ?? string.Empty,
                ApplicationVersion = configuration.GetValue<string?>("Application:Version")
                    ?? typeof(WikiConfigurationManager).Assembly.GetName().Version?.ToString(3)
                    ?? string.Empty,
                FaviconPath = configuration.GetValue<string?>("Application:FaviconPath") ?? "/favicon.ico"
            };

            if (string.IsNullOrWhiteSpace(WikiConfiguration.ApplicationVersion))
            {
                WikiConfiguration.ApplicationVersion = typeof(WikiConfigurationManager).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
            }

            ReloadAll().Wait();
        }

        public async Task ReloadAll()
        {
            MemCache.Clear();

            WikiConfiguration.IsDebug = Debugger.IsAttached;

            var performanceConfig = new TwConfigurationEntries((await _configurationRepository.GetEntriesByGroupNameAsync(TwConfigGroup.Performance)).ToList());
            WikiConfiguration.PageCacheSeconds = performanceConfig.Value<int>("Page Cache Time (Seconds)");
            WikiConfiguration.RecordCompilationMetrics = performanceConfig.Value<bool>("Record Compilation Metrics");
            WikiConfiguration.CacheMemoryLimitMB = performanceConfig.Value<int>("Cache Memory Limit MB");

            MemCache.Initialize(WikiConfiguration.CacheMemoryLimitMB, TimeSpan.FromSeconds(WikiConfiguration.PageCacheSeconds));

            var basicConfig = new TwConfigurationEntries((await _configurationRepository.GetEntriesByGroupNameAsync(TwConfigGroup.Basic)).ToList());
            var customizationConfig = new TwConfigurationEntries((await _configurationRepository.GetEntriesByGroupNameAsync(TwConfigGroup.Customization)).ToList());
            var htmlConfig = new TwConfigurationEntries((await _configurationRepository.GetEntriesByGroupNameAsync(TwConfigGroup.HTMLLayout)).ToList());
            var functionalityConfig = new TwConfigurationEntries((await _configurationRepository.GetEntriesByGroupNameAsync(TwConfigGroup.Functionality)).ToList());
            var membershipConfig = new TwConfigurationEntries((await _configurationRepository.GetEntriesByGroupNameAsync(TwConfigGroup.Membership)).ToList());
            var searchConfig = new TwConfigurationEntries((await _configurationRepository.GetEntriesByGroupNameAsync(TwConfigGroup.Search)).ToList());
            var filesAndAttachmentsConfig = new TwConfigurationEntries((await _configurationRepository.GetEntriesByGroupNameAsync(TwConfigGroup.FilesAndAttachments)).ToList());
            var ldapAuthentication = new TwConfigurationEntries((await _configurationRepository.GetEntriesByGroupNameAsync(TwConfigGroup.LDAPAuthentication)).ToList());
            WikiConfiguration.EnableLDAPAuthentication = ldapAuthentication.Value("LDAP : Enable LDAP Authentication", false);

            WikiConfiguration.Address = basicConfig?.Value<string>("Address") ?? string.Empty;
            WikiConfiguration.Name = basicConfig?.Value<string>("Name") ?? string.Empty;
            WikiConfiguration.Copyright = basicConfig?.Value<string>("Copyright") ?? string.Empty;

            var themeName = customizationConfig.Value("Theme", "Light");

            WikiConfiguration.FixedMenuPosition = customizationConfig.Value("Fixed Header Menu Position", false);
            WikiConfiguration.AllowSignup = membershipConfig.Value("Allow Signup", false);
            WikiConfiguration.DefaultProfileRecentlyModifiedCount = performanceConfig.Value<int>("Default Profile Recently Modified Count");
            WikiConfiguration.PreLoadAnimatedEmojis = performanceConfig.Value<bool>("Pre-Load Animated Emojis");
            WikiConfiguration.SystemTheme = (await _configurationRepository.GetThemesAsync()).Single(o => o.Name == themeName);
            WikiConfiguration.DefaultEmojiHeight = customizationConfig.Value<int>("Default Emoji Height");
            WikiConfiguration.PaginationSize = customizationConfig.Value<int>("Pagination Size");

            WikiConfiguration.DefaultTimeZone = customizationConfig?.Value<string>("Default TimeZone") ?? string.Empty;
            WikiConfiguration.IncludeWikiDescriptionInMeta = functionalityConfig.Value<bool>("Include wiki Description in Meta");
            WikiConfiguration.IncludeWikiTagsInMeta = functionalityConfig.Value<bool>("Include wiki Tags in Meta");
            WikiConfiguration.EnablePageComments = functionalityConfig.Value<bool>("Enable Page Comments");
            WikiConfiguration.EnablePublicProfiles = functionalityConfig.Value<bool>("Enable Public Profiles");
            WikiConfiguration.ShowCommentsOnPageFooter = functionalityConfig.Value<bool>("Show Comments on Page Footer");
            WikiConfiguration.ShowChangeSummaryWhenEditing = functionalityConfig.Value<bool>("Show Change Summary when Editing");
            WikiConfiguration.RequireChangeSummaryWhenEditing = functionalityConfig.Value<bool>("Require Change Summary when Editing");
            WikiConfiguration.ShowLastModifiedOnPageFooter = functionalityConfig.Value<bool>("Show Last Modified on Page Footer");
            WikiConfiguration.IncludeSearchOnNavbar = searchConfig.Value<bool>("Include Search on Navbar");
            WikiConfiguration.HTMLHeader = htmlConfig?.Value<string>("Header") ?? string.Empty;
            WikiConfiguration.HTMLFooter = htmlConfig?.Value<string>("Footer") ?? string.Empty;
            WikiConfiguration.HTMLPreBody = htmlConfig?.Value<string>("Pre-Body") ?? string.Empty;
            WikiConfiguration.HTMLPostBody = htmlConfig?.Value<string>("Post-Body") ?? string.Empty;
            WikiConfiguration.BrandImageSmall = customizationConfig?.Value<string>("Brand Image (Small)") ?? string.Empty;
            WikiConfiguration.FooterBlurb = customizationConfig?.Value<string>("FooterBlurb") ?? string.Empty;
            WikiConfiguration.EnableSidebar = customizationConfig?.Value("Enable Sidebar", false) ?? false;
            WikiConfiguration.SidebarPage = customizationConfig?.Value("Sidebar Page", string.Empty) ?? string.Empty;
            WikiConfiguration.MaxAvatarFileSize = filesAndAttachmentsConfig.Value<int>("Max Avatar File Size");
            WikiConfiguration.MaxAttachmentFileSize = filesAndAttachmentsConfig.Value<int>("Max Attachment File Size");
            WikiConfiguration.MaxEmojiFileSize = filesAndAttachmentsConfig.Value<int>("Max Emoji File Size");

            await ReloadMenu();
            await ReloadEmojis();
        }

        public async Task ReloadMenu()
        {
            WikiConfiguration.MenuItems = (await _configurationRepository.GetMenuItemsAsync()).ToList();
        }

        public async Task ReloadEmojis()
        {
            WikiConfiguration.Emojis = await _reloadEmojis(
                WikiConfiguration.PreLoadAnimatedEmojis, WikiConfiguration.DefaultEmojiHeight);
        }
    }
}
