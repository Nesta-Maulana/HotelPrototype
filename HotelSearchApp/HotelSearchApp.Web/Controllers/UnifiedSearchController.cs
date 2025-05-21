using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HotelSearchApp.Core.DTOs;
using HotelSearchApp.Core.Interfaces;
using HotelSearchApp.Core.Models;
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
            if (string.IsNullOrWhiteSpace(viewModel.SearchQuery))
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return PartialView("_SearchResults", viewModel);
                }
                return View("Index", viewModel);
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Get search results using unified search approach
                var searchResult = await _elasticSearchService.UnifiedSearchAsync(
                    viewModel.SearchQuery,
                    viewModel.PageNumber,
                    viewModel.PageSize
                );

                stopwatch.Stop();

                // Update view model with results
                viewModel.SearchResults = searchResult;
                viewModel.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                viewModel.SearchMethod = "Unified";

                // Track search analytics
                viewModel.SearchSuccessful = searchResult.TotalHits > 0;
                viewModel.SearchTermsCount =
                    viewModel
                        .SearchQuery?.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Length ?? 0;

                // Handle AJAX requests for real-time search
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return PartialView("_SearchResults", viewModel);
                }

                return View("Index", viewModel);
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.WriteLine($"Search error: {ex.Message}");

                // Provide error feedback
                viewModel.SearchResults = null;
                viewModel.TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                viewModel.ErrorMessage = "An error occurred while searching. Please try again.";

                // Handle AJAX requests
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    return PartialView("_SearchResults", viewModel);
                }

                return View("Index", viewModel);
            }
        }
    }
}
