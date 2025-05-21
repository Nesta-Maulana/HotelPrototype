using System;
using System.Diagnostics;
using System.Threading.Tasks;
using HotelSearchApp.Core.Interfaces;
using HotelSearchApp.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace HotelSearchApp.Web.Controllers
{
    public class UnifiedSearchController : Controller
    {
        private readonly IElasticSearchService _elasticSearchService;

        public UnifiedSearchController(IElasticSearchService elasticSearchService)
        {
            _elasticSearchService = elasticSearchService;
        }

        public IActionResult Index()
        {
            return View(new UnifiedSearchViewModel());
        }

        [HttpGet]
        public async Task<IActionResult> Search(UnifiedSearchViewModel viewModel)
        {
            var stopwatch = Stopwatch.StartNew();

            var result = await _elasticSearchService.UnifiedSearchAsync(
                viewModel.SearchQuery,
                viewModel.PageNumber,
                viewModel.PageSize
            );

            stopwatch.Stop();

            viewModel.SearchResults = result;
            viewModel.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

            // Jika request AJAX, kembalikan partial view untuk hasil pencarian
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("_SearchResults", viewModel);
            }

            return View("Index", viewModel);
        }
    }
}
