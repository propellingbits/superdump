﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sakura.AspNetCore;
using SuperDump.Models;
using SuperDumpService.Helpers;
using SuperDumpService.Models;
using SuperDumpService.Services;
using SuperDumpService.ViewModels;

namespace SuperDumpService.Controllers {
	[AutoValidateAntiforgeryToken]
	[Authorize(Policy = LdapCookieAuthenticationExtension.ViewerPolicy)]
	public class HomeController : Controller {
		private IHostingEnvironment environment;
		public SuperDumpRepository superDumpRepo;
		public BundleRepository bundleRepo;
		public DumpRepository dumpRepo;
		public IDumpStorage dumpStorage;
		public SuperDumpSettings settings;
		private readonly PathHelper pathHelper;
		private readonly RelationshipRepository relationshipRepo;
		private readonly SimilarityService similarityService;
		private readonly ElasticSearchService elasticService;
		private readonly ILogger<HomeController> logger;
		private readonly IAuthorizationHelper authorizationHelper;
		private readonly JiraIssueRepository jiraIssueRepository;

		public HomeController(IHostingEnvironment environment, 
				SuperDumpRepository superDumpRepo, 
				BundleRepository bundleRepo, 
				DumpRepository dumpRepo,
				IDumpStorage dumpStorage, 
				IOptions<SuperDumpSettings> settings, 
				PathHelper pathHelper, 
				RelationshipRepository relationshipRepo, 
				SimilarityService similarityService,
				ElasticSearchService elasticService,
				ILoggerFactory loggerFactory, 
				IAuthorizationHelper authorizationHelper,
				JiraIssueRepository jiraIssueRepository) {
			this.environment = environment;
			this.superDumpRepo = superDumpRepo;
			this.bundleRepo = bundleRepo;
			this.dumpRepo = dumpRepo;
			this.dumpStorage = dumpStorage;
			this.settings = settings.Value;
			this.pathHelper = pathHelper;
			this.relationshipRepo = relationshipRepo;
			this.similarityService = similarityService;
			this.elasticService = elasticService;
			logger = loggerFactory.CreateLogger<HomeController>();
			this.authorizationHelper = authorizationHelper;
			this.jiraIssueRepository = jiraIssueRepository;
		}

		public IActionResult Index() {
			return RedirectToAction("Create");
		}

		public IActionResult About() {
			ViewData["Message"] = "SuperDump";
			return View();
		}

		[HttpGet]
		public IActionResult Create() {
			ViewData["Message"] = "New Analysis";
			return View();
		}

		[HttpPost]
		public IActionResult Create(DumpAnalysisInput input) {
			pathHelper.PrepareDirectories();

			if (ModelState.IsValid) {
				System.Diagnostics.Debug.WriteLine(input.Url);

				string filename = input.UrlFilename;
				if (Utility.ValidateUrl(input.Url, ref filename)) {
					if (filename == null && Utility.IsLocalFile(input.Url)) {
						filename = Path.GetFileName(input.Url);
					}
					string bundleId = superDumpRepo.ProcessInputfile(filename, input);
					logger.LogFileUpload("Upload", HttpContext, bundleId, input.CustomProperties, input.Url);
					// return list of file paths from zip
					return RedirectToAction("BundleCreated", "Home", new { bundleId = bundleId });
				} else {
					logger.LogNotFound("Upload", HttpContext, "Url", input.Url);
					return BadRequest("Provided URI is invalid or cannot be reached.");
				}
			} else {
				return View();
			}
		}

		public async Task<IActionResult> BundleCreated(string bundleId) {
			if (bundleRepo.ContainsBundle(bundleId)) {
				return View(new BundleViewModel(bundleRepo.Get(bundleId), await GetDumpListViewModels(bundleId)));
			}
			throw new Exception($"bundleid '{bundleId}' does not exist in repository");
		}

		private async Task<IEnumerable<DumpListViewModel>> GetDumpListViewModels(string bundleId) {
			if (relationshipRepo.IsPopulated) {
				return await Task.WhenAll(dumpRepo.Get(bundleId).Select(async x => new DumpListViewModel(x, new Similarities(await similarityService.GetSimilarities(x.Id)))));
			}
			return dumpRepo.Get(bundleId).Select(x => new DumpListViewModel(x));
		}

		[HttpPost]
		public async Task<IActionResult> Upload(IFormFile file, string refurl, string note) {
			if (ModelState.IsValid) {
				pathHelper.PrepareDirectories();
				if (file.Length > 0) {
					var tempDir = new DirectoryInfo(pathHelper.GetTempDir());
					tempDir.Create();
					var filePath = new FileInfo(Path.Combine(tempDir.FullName, file.FileName));
					using (var fileStream = new FileStream(filePath.FullName, FileMode.Create)) {
						await file.CopyToAsync(fileStream);
					}
					var bundle = new DumpAnalysisInput(filePath.FullName, new Tuple<string, string>("ref", refurl), new Tuple<string, string>("note", note));
					return Create(bundle);
				}
				return View("UploadError", new Error("No filename was provided.", ""));
			} else {
				return View("UploadError", new Error("Invalid model", "Invalid model"));
			}
		}

		public async Task<IActionResult> Overview(int page = 1, int pagesize = 50, string searchFilter = null, bool includeEmptyBundles = false, string elasticSearchFilter = null) {
			logger.LogDefault("Overview", HttpContext);

			if (!string.IsNullOrEmpty(elasticSearchFilter)) {
				var searchResults = elasticService.SearchDumpsByJson(elasticSearchFilter).ToList();

				// TODO CN: I need to change this to just a list of dumps, not a list of bundles

				var bundleInfos = searchResults.Select(x => x.BundleId).Distinct().Where(bundleId => bundleId != null).Select(x => bundleRepo.Get(x)).Where(x => x != null);
				var foundBundles = (await Task.WhenAll(bundleInfos.Select(async x => new BundleViewModel(x, await GetDumpListViewModels(x.BundleId))))).OrderByDescending(b => b.Created);
				// TODO CN: we now show all dumps of bundles that have been found. needs fixing.

				ViewData["elasticSearchFilter"] = elasticSearchFilter;
				return View(new OverviewViewModel {
					All = foundBundles,
					Filtered = foundBundles, // filtered is wrong here
					Paged = foundBundles.ToPagedList(pagesize, page),
					KibanaUrl = KibanaUrl(),
					IsPopulated = bundleRepo.IsPopulated,
					IsRelationshipsPopulated = relationshipRepo.IsPopulated || !settings.SimilarityDetectionEnabled,
					IsJiraIssuesPopulated = jiraIssueRepository.IsPopulated || !settings.UseJiraIntegration
				});
			} else {
				var bundles = (await Task.WhenAll(bundleRepo.GetAll().Select(async r => new BundleViewModel(r, await GetDumpListViewModels(r.BundleId))))).OrderByDescending(b => b.Created);

				var filtered = Search(searchFilter, bundles);
				filtered = ExcludeEmptyBundles(includeEmptyBundles, filtered);

				ViewData["searchFilter"] = searchFilter;
				return View(new OverviewViewModel {
					All = bundles,
					Filtered = filtered,
					Paged = filtered.ToPagedList(pagesize, page),
					KibanaUrl = KibanaUrl(),
					IsPopulated = bundleRepo.IsPopulated,
					IsRelationshipsPopulated = relationshipRepo.IsPopulated || !settings.SimilarityDetectionEnabled,
					IsJiraIssuesPopulated = jiraIssueRepository.IsPopulated || !settings.UseJiraIntegration
				});
			}
		}

		[HttpGet(Name = "Elastic")]
		public IActionResult Elastic() {
			logger.LogDefault("ElasticSearch", HttpContext);
			return Redirect(KibanaUrl());
		}

		private string KibanaUrl() {
			string portlessUrl = settings.ElasticSearchHost;
			if (portlessUrl.Contains(':')) {
				int colon = portlessUrl.LastIndexOf(':');
				portlessUrl = portlessUrl.Substring(0, colon);
			}
			return portlessUrl + ":5601";
		}

		private IEnumerable<BundleViewModel> ExcludeEmptyBundles(bool includeEmptyBundles, IEnumerable<BundleViewModel> bundles) {
			if (includeEmptyBundles) return bundles;

			return bundles.Where(b => b.DumpInfos.Count() > 0);
		}

		private IEnumerable<BundleViewModel> Search(string searchFilter, IEnumerable<BundleViewModel> bundles) {
			if (searchFilter == null) return bundles;

			logger.LogSearch("Search", HttpContext, searchFilter);
			return bundles.Where(b =>
				b.BundleId.Contains(searchFilter, StringComparison.OrdinalIgnoreCase)
				|| b.CustomProperties.Any(cp => cp.Value != null && cp.Value.Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
				|| b.DumpInfos.Any(d =>
					d.DumpInfo.DumpId.Contains(searchFilter, StringComparison.OrdinalIgnoreCase)
					|| (d.DumpInfo.DumpFileName != null && d.DumpInfo.DumpFileName.Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
				));
		}

		public IActionResult GetReport() {
			ViewData["Message"] = "Get Report";
			return View();
		}

		[Authorize(Policy = LdapCookieAuthenticationExtension.UserPolicy)]
		[HttpGet(Name = "Interactive")]
		public IActionResult Interactive(string bundleId, string dumpId, string cmd) {
			var bundleInfo = superDumpRepo.GetBundle(bundleId);
			if (bundleInfo == null) {
				logger.LogNotFound("Interactive Mode: Bundle not found", HttpContext, "BundleId", bundleId);
				return View(null);
			}

			logger.LogDumpAccess("Start Interactive Mode", HttpContext, bundleInfo, dumpId);
			var id = DumpIdentifier.Create(bundleId, dumpId);
			return View(new InteractiveViewModel() { Id = id, DumpInfo = dumpRepo.Get(id), Command = cmd });
		}

		[HttpGet(Name = "Report")]
		public async Task<IActionResult> Report(string bundleId, string dumpId) {
			ViewData["Message"] = "Get Report";
			var id = DumpIdentifier.Create(bundleId, dumpId);

			var bundleInfo = superDumpRepo.GetBundle(bundleId);
			if (bundleInfo == null) {
				logger.LogNotFound("Report: Bundle not found", HttpContext, "BundleId", bundleId);
				return View(null);
			}

			var dumpInfo = superDumpRepo.GetDump(id);
			if (dumpInfo == null) {
				logger.LogNotFound("Report: Dump not found", HttpContext, "Id", id.ToString());
				return View(null);
			}

			logger.LogDumpAccess("Report", HttpContext, bundleInfo, dumpId);

			string sdReadError = string.Empty;
			SDResult res = null;
			try {
				res = await superDumpRepo.GetResultAndThrow(id);
			} catch (Exception e) {
				sdReadError = e.ToString();
			}

			// don't add relationships when the repo is not ready yet. it might take some time with large amounts.
			IEnumerable<KeyValuePair<DumpMetainfo, double>> similarDumps =
				!relationshipRepo.IsPopulated ? Enumerable.Empty<KeyValuePair<DumpMetainfo, double>>() :
				(await relationshipRepo.GetRelationShips(DumpIdentifier.Create(bundleId, dumpId)))
					.Select(x => new KeyValuePair<DumpMetainfo, double>(dumpRepo.Get(x.Key), x.Value)).Where(dump => dump.Key != null);

			return base.View(new ReportViewModel(id) {
				BundleFileName = bundleInfo.BundleFileName,
				DumpFileName = dumpInfo.DumpFileName,
				Result = res,
				CustomProperties = Utility.Sanitize(bundleInfo.CustomProperties),
				TimeStamp = dumpInfo.Created,
				Files = dumpRepo.GetFileNames(id),
				AnalysisError = dumpInfo.ErrorMessage,
				ThreadTags = res != null ? res.GetThreadTags() : new HashSet<SDTag>(),
				PointerSize = res == null ? 8 : (res.SystemContext?.ProcessArchitecture == "X86" ? 8 : 12),
				CustomTextResult = await ReadCustomTextResult(dumpInfo),
				SDResultReadError = sdReadError,
				DumpType = dumpInfo.DumpType,
				RepositoryUrl = settings.RepositoryUrl,
				InteractiveGdbHost = settings.InteractiveGdbHost,
				SimilarityDetectionEnabled = settings.SimilarityDetectionEnabled,
				Similarities = similarDumps,
				IsDumpAvailable = dumpRepo.IsPrimaryDumpAvailable(id),
				MainBundleJiraIssues = !settings.UseJiraIntegration || !jiraIssueRepository.IsPopulated ? Enumerable.Empty<JiraIssueModel>() : await jiraIssueRepository.GetAllIssuesByBundleIdWithoutWait(bundleId),
				SimilarDumpIssues = !settings.UseJiraIntegration || !jiraIssueRepository.IsPopulated ? new Dictionary<string, IEnumerable<JiraIssueModel>>() : await jiraIssueRepository.GetAllIssuesByBundleIdsWithoutWait(similarDumps.Select(dump => dump.Key.BundleId)),
				UseJiraIntegration = settings.UseJiraIntegration,
				DumpStatus = dumpInfo.Status,
				IsRelationshipsPopulated = relationshipRepo.IsPopulated || !settings.SimilarityDetectionEnabled,
				IsJiraIssuesPopulated = jiraIssueRepository.IsPopulated || !settings.UseJiraIntegration
			});
		}

		private async Task<string> ReadCustomTextResult(DumpMetainfo dumpInfo) {
			SDFileEntry customResultFile = dumpInfo.Files.FirstOrDefault(x => x.Type == SDFileType.CustomTextResult);
			if (customResultFile == null) return null;
			FileInfo file = dumpStorage.GetFile(dumpInfo.Id, customResultFile.FileName);
			if (file == null || !file.Exists) return null;
			return await System.IO.File.ReadAllTextAsync(file.FullName);
		}

		public IActionResult UploadError() {
			return View();
		}

		public IActionResult Error() {
			return View();
		}

		public IActionResult DownloadFile(string bundleId, string dumpId, string filename) {
			if (!(authorizationHelper.CheckPolicy(HttpContext.User, LdapCookieAuthenticationExtension.UserPolicy) ||
				settings.LdapAuthenticationSettings.ViewerDownloadableFiles.Any(f => f == filename) &&
				authorizationHelper.CheckPolicy(HttpContext.User, LdapCookieAuthenticationExtension.ViewerPolicy))) {
				return Forbid();
			}

			var bundleInfo = superDumpRepo.GetBundle(bundleId);
			if (bundleInfo == null) {
				logger.LogNotFound("DownloadFile: Bundle not found", HttpContext, "BundleId", bundleId);
				return View(null);
			}
			var file = dumpStorage.GetFile(DumpIdentifier.Create(bundleId, dumpId), filename);
			if (file == null) {
				logger.LogNotFound("DownloadFile: File not found", HttpContext, "Filename", filename);
				throw new ArgumentException("could not find file");
			}
			logger.LogFileAccess("DownloadFile", HttpContext, bundleInfo, dumpId, filename);
			if (file.Extension == ".txt"
				|| file.Extension == ".log"
				|| file.Extension == ".json") {
				return ContentWithFilename(System.IO.File.ReadAllText(file.FullName), file.Name);
			}
			return File(System.IO.File.OpenRead(file.FullName), "application/octet-stream", file.Name);
		}

		/// <summary>
		/// Adds Filename to Content-Disposition Headers, so that "Save As..." in the browser uses the correct file name.
		/// When normally requesting this, the content is direclty shown in the browser.
		/// </summary>
		private IActionResult ContentWithFilename(string content, string filename) {
			var cd = new ContentDisposition {
				FileName = filename,
				Inline = true  // false = prompt the user for downloading;  true = browser to try to show the file inline
			};
			Response.Headers.Add("Content-Disposition", cd.ToString());
			Response.Headers.Add("X-Content-Type-Options", "nosniff");
			return Content(content);
		}

		[Authorize(Policy = LdapCookieAuthenticationExtension.UserPolicy)]
		[HttpPost]
		public IActionResult Rerun(string bundleId, string dumpId) {
			var bundleInfo = superDumpRepo.GetBundle(bundleId);
			if (bundleInfo == null) {
				logger.LogNotFound("Rerun: Bundle not found", HttpContext, "BundleId", bundleId);
				return View(null);
			}
			logger.LogDumpAccess("Rerun", HttpContext, bundleInfo, dumpId);
			var id = DumpIdentifier.Create(bundleId, dumpId);
			superDumpRepo.RerunAnalysis(id);
			return View(new ReportViewModel(id));
		}
	}
}
