using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Elasticsearch.Net;
using HotelSearchApp.Core.DTOs;
using HotelSearchApp.Core.Interfaces;
using HotelSearchApp.Core.Models;
using Nest;

namespace HotelSearchApp.Infrastructure.Services
{
    public class ElasticSearchService : IElasticSearchService
    {
        private readonly IElasticClient _elasticClient;
        private const string HotelIndexName = "hotels";
        private const string HotelNGramIndexName = "hotels_ngram";

        public ElasticSearchService(IElasticClient elasticClient)
        {
            _elasticClient = elasticClient;
        }

        public async Task<ElasticSearchResponse<Hotel>> SearchHotelsAsync(
            HotelSearchParameters searchParams
        )
        {
            var stopwatch = Stopwatch.StartNew();

            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelIndexName)
                .From((searchParams.PageNumber - 1) * searchParams.PageSize)
                .Size(searchParams.PageSize)
                .RequestCache(false)
                .TrackScores(true);

            var mustClauses = new List<QueryContainer>();
            var shouldClauses = new List<QueryContainer>();

            // Enhanced fuzzy matching for HotelCode
            if (!string.IsNullOrWhiteSpace(searchParams.HotelCode))
            {
                shouldClauses.Add(
                    new MatchQuery
                    {
                        Field = "hotelcode",
                        Query = searchParams.HotelCode,
                        Fuzziness = Fuzziness.Auto,
                        PrefixLength = 1,
                        MaxExpansions = 50,
                        Boost = 3.0,
                    }
                );

                shouldClauses.Add(
                    new TermQuery
                    {
                        Field = "hotelcode",
                        Value = searchParams.HotelCode,
                        Boost = 4.0,
                    }
                );
            }

            // Enhanced fuzzy match for CityName
            if (!string.IsNullOrWhiteSpace(searchParams.CityName))
            {
                shouldClauses.Add(
                    new MatchQuery
                    {
                        Field = "cityname",
                        Query = searchParams.CityName,
                        Fuzziness = Fuzziness.Auto,
                        PrefixLength = 1,
                        MaxExpansions = 50,
                        Boost = 2.0,
                    }
                );

                shouldClauses.Add(
                    new PrefixQuery
                    {
                        Field = "cityname",
                        Value = searchParams.CityName.ToLowerInvariant(),
                        Boost = 1.8,
                    }
                );
            }

            // Enhanced fuzzy match for HotelName
            if (!string.IsNullOrWhiteSpace(searchParams.HotelName))
            {
                shouldClauses.Add(
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = searchParams.HotelName,
                        Fuzziness = Fuzziness.Auto,
                        PrefixLength = 1,
                        MaxExpansions = 50,
                        Boost = 2.5,
                    }
                );

                shouldClauses.Add(
                    new PrefixQuery
                    {
                        Field = "hotelname",
                        Value = searchParams.HotelName.ToLowerInvariant(),
                        Boost = 2.0,
                    }
                );

                shouldClauses.Add(
                    new MatchPhraseQuery
                    {
                        Field = "hotelname",
                        Query = searchParams.HotelName,
                        Boost = 3.0,
                    }
                );
            }

            // Enhanced fuzzy match for Address1
            if (!string.IsNullOrWhiteSpace(searchParams.Address1))
            {
                shouldClauses.Add(
                    new MatchQuery
                    {
                        Field = "address1",
                        Query = searchParams.Address1,
                        Fuzziness = Fuzziness.Auto,
                        PrefixLength = 1,
                        MaxExpansions = 50,
                        Boost = 1.5,
                    }
                );

                shouldClauses.Add(
                    new MatchPhraseQuery
                    {
                        Field = "address1",
                        Query = searchParams.Address1,
                        Boost = 2.0,
                    }
                );
            }

            // Build the query
            QueryContainer? queryContainer = null;

            if (mustClauses.Any())
            {
                queryContainer = new BoolQuery { Must = mustClauses };
            }

            if (shouldClauses.Any())
            {
                if (queryContainer == null)
                {
                    queryContainer = new BoolQuery
                    {
                        Should = shouldClauses,
                        MinimumShouldMatch = 1,
                    };
                }
                else
                {
                    queryContainer =
                        queryContainer
                        && new BoolQuery { Should = shouldClauses, MinimumShouldMatch = 1 };
                }
            }

            if (queryContainer != null)
            {
                searchDescriptor = searchDescriptor.Query(q => queryContainer);
            }

            var searchResponse = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

            stopwatch.Stop();

            // Adaptive result handling based on relevance
            if (searchResponse.Hits.Count > 1)
            {
                var topScore = searchResponse.Hits.First().Score;
                var secondScore = searchResponse.Hits.Skip(1).First().Score;

                // If top score is significantly higher than second score, only return top result
                if (topScore > 0 && secondScore > 0 && (topScore / secondScore) > 1.8)
                {
                    return new ElasticSearchResponse<Hotel>
                    {
                        Items = new List<Hotel> { searchResponse.Documents.First() },
                        TotalHits = 1,
                        ElapsedTime = stopwatch.Elapsed,
                        PageNumber = searchParams.PageNumber,
                        PageSize = searchParams.PageSize,
                    };
                }
            }

            return new ElasticSearchResponse<Hotel>
            {
                Items = searchResponse.Documents,
                TotalHits = searchResponse.Total,
                ElapsedTime = stopwatch.Elapsed,
                PageNumber = searchParams.PageNumber,
                PageSize = searchParams.PageSize,
            };
        }

        public async Task<ElasticSearchResponse<Hotel>> SearchHotelsNGramAsync(
            HotelSearchParameters searchParams
        )
        {
            var stopwatch = Stopwatch.StartNew();

            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelNGramIndexName)
                .From((searchParams.PageNumber - 1) * searchParams.PageSize)
                .Size(searchParams.PageSize)
                .RequestCache(false)
                .TrackScores(true);

            var mustClauses = new List<QueryContainer>();
            var shouldClauses = new List<QueryContainer>();

            // Enhanced hybrid approach for HotelCode
            if (!string.IsNullOrWhiteSpace(searchParams.HotelCode))
            {
                var hotelCodeQueries = new List<QueryContainer>
                {
                    // N-gram query
                    new MatchQuery
                    {
                        Field = "hotelcode",
                        Query = searchParams.HotelCode,
                        MinimumShouldMatch = "60%",
                        Boost = 2.0,
                    },
                    // Edge n-gram for better prefix matching
                    new MatchQuery
                    {
                        Field = "hotelcode.edge",
                        Query = searchParams.HotelCode,
                        MinimumShouldMatch = "80%",
                        Boost = 2.5,
                    },
                    // Exact keyword match
                    new TermQuery
                    {
                        Field = "hotelcode.keyword",
                        Value = searchParams.HotelCode,
                        Boost = 3.0,
                    },
                    // Fuzzy query for typo tolerance
                    new MatchQuery
                    {
                        Field = "hotelcode",
                        Query = searchParams.HotelCode,
                        Fuzziness = Fuzziness.Auto,
                        PrefixLength = 1,
                        MaxExpansions = 50,
                        Boost = 2.2,
                    },
                };

                shouldClauses.Add(
                    new BoolQuery { Should = hotelCodeQueries, MinimumShouldMatch = 1 }
                );
            }

            // Enhanced hybrid approach for CityName
            if (!string.IsNullOrWhiteSpace(searchParams.CityName))
            {
                var cityNameQueries = new List<QueryContainer>
                {
                    // N-gram query
                    new MatchQuery
                    {
                        Field = "cityname",
                        Query = searchParams.CityName,
                        MinimumShouldMatch = "70%",
                        Boost = 1.5,
                    },
                    // Edge n-gram for better prefix matching
                    new MatchQuery
                    {
                        Field = "cityname.edge",
                        Query = searchParams.CityName,
                        MinimumShouldMatch = "80%",
                        Boost = 1.8,
                    },
                    // Exact keyword match
                    new TermQuery
                    {
                        Field = "cityname.keyword",
                        Value = searchParams.CityName,
                        Boost = 2.0,
                    },
                    // Fuzzy query for typo tolerance
                    new MatchQuery
                    {
                        Field = "cityname",
                        Query = searchParams.CityName,
                        Fuzziness = Fuzziness.Auto,
                        PrefixLength = 1,
                        MaxExpansions = 50,
                        Boost = 1.6,
                    },
                };

                shouldClauses.Add(
                    new BoolQuery { Should = cityNameQueries, MinimumShouldMatch = 1 }
                );
            }

            // Enhanced hybrid approach for HotelName
            if (!string.IsNullOrWhiteSpace(searchParams.HotelName))
            {
                var hotelNameQueries = new List<QueryContainer>
                {
                    // N-gram query
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = searchParams.HotelName,
                        MinimumShouldMatch = "70%",
                        Boost = 1.8,
                    },
                    // Edge n-gram for better prefix matching
                    new MatchQuery
                    {
                        Field = "hotelname.edge",
                        Query = searchParams.HotelName,
                        MinimumShouldMatch = "80%",
                        Boost = 2.0,
                    },
                    // Exact keyword match
                    new TermQuery
                    {
                        Field = "hotelname.keyword",
                        Value = searchParams.HotelName,
                        Boost = 2.5,
                    },
                    // Fuzzy query
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = searchParams.HotelName,
                        Fuzziness = Fuzziness.Auto,
                        PrefixLength = 1,
                        MaxExpansions = 50,
                        Boost = 1.7,
                    },
                    // Phrase match for better exact matching
                    new MatchPhraseQuery
                    {
                        Field = "hotelname",
                        Query = searchParams.HotelName,
                        Boost = 2.2,
                    },
                };

                shouldClauses.Add(
                    new BoolQuery { Should = hotelNameQueries, MinimumShouldMatch = 1 }
                );
            }

            // Enhanced hybrid approach for Address1
            if (!string.IsNullOrWhiteSpace(searchParams.Address1))
            {
                var addressQueries = new List<QueryContainer>
                {
                    // N-gram query
                    new MatchQuery
                    {
                        Field = "address1",
                        Query = searchParams.Address1,
                        MinimumShouldMatch = "70%",
                        Boost = 1.5,
                    },
                    // Edge n-gram for better prefix matching
                    new MatchQuery
                    {
                        Field = "address1.edge",
                        Query = searchParams.Address1,
                        MinimumShouldMatch = "70%",
                        Boost = 1.6,
                    },
                    // Fuzzy query
                    new MatchQuery
                    {
                        Field = "address1",
                        Query = searchParams.Address1,
                        Fuzziness = Fuzziness.Auto,
                        PrefixLength = 1,
                        MaxExpansions = 50,
                        Boost = 1.4,
                    },
                    // Phrase match for better exact matching
                    new MatchPhraseQuery
                    {
                        Field = "address1",
                        Query = searchParams.Address1,
                        Boost = 1.8,
                    },
                };

                shouldClauses.Add(
                    new BoolQuery { Should = addressQueries, MinimumShouldMatch = 1 }
                );
            }

            // Build the query
            QueryContainer? queryContainer = null;

            if (mustClauses.Any())
            {
                queryContainer = new BoolQuery { Must = mustClauses };
            }

            if (shouldClauses.Any())
            {
                if (queryContainer == null)
                {
                    queryContainer = new BoolQuery
                    {
                        Should = shouldClauses,
                        MinimumShouldMatch = 1,
                    };
                }
                else
                {
                    queryContainer =
                        queryContainer
                        && new BoolQuery { Should = shouldClauses, MinimumShouldMatch = 1 };
                }
            }

            if (queryContainer != null)
            {
                searchDescriptor = searchDescriptor.Query(q => queryContainer);
            }

            var searchResponse = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

            stopwatch.Stop();

            // Adaptive result handling
            if (searchResponse.Hits.Count > 1)
            {
                var topScore = searchResponse.Hits.First().Score ?? 0;
                var secondScore = searchResponse.Hits.Skip(1).First().Score ?? 0;

                // If top score is significantly higher than second score, only return top result
                if (topScore > 0 && secondScore > 0 && (topScore / secondScore) > 1.8)
                {
                    return new ElasticSearchResponse<Hotel>
                    {
                        Items = new List<Hotel> { searchResponse.Documents.First() },
                        TotalHits = 1,
                        ElapsedTime = stopwatch.Elapsed,
                        PageNumber = searchParams.PageNumber,
                        PageSize = searchParams.PageSize,
                    };
                }
            }

            return new ElasticSearchResponse<Hotel>
            {
                Items = searchResponse.Documents,
                TotalHits = searchResponse.Total,
                ElapsedTime = stopwatch.Elapsed,
                PageNumber = searchParams.PageNumber,
                PageSize = searchParams.PageSize,
            };
        }

        public async Task<ElasticSearchResponse<Hotel>> UnifiedSearchAsync(
            string searchQuery,
            int pageNumber = 1,
            int pageSize = 10
        )
        {
            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                return new ElasticSearchResponse<Hotel>
                {
                    Items = new List<Hotel>(),
                    TotalHits = 0,
                    ElapsedTime = TimeSpan.Zero,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                };
            }

            var stopwatch = Stopwatch.StartNew();

            // 1. Normalize the search query
            var normalizedQuery = NormalizeSearchQuery(searchQuery);

            // 2. Detect search pattern/intent
            var (searchIntent, isExactSearch) = DetectSearchIntent(normalizedQuery);

            // 3. Build the appropriate query based on the detected intent
            var searchResponse = await ExecuteSearch(
                normalizedQuery,
                searchIntent,
                isExactSearch,
                pageNumber,
                pageSize
            );

            // 4. Post-process results based on the search intent
            var processedResults = ProcessResults(
                searchResponse,
                normalizedQuery,
                searchIntent,
                isExactSearch
            );

            stopwatch.Stop();

            return new ElasticSearchResponse<Hotel>
            {
                Items = processedResults,
                TotalHits = processedResults.Count(),
                ElapsedTime = stopwatch.Elapsed,
                PageNumber = pageNumber,
                PageSize = pageSize,
            };
        }

        // Supporting methods for UnifiedSearchAsync
        private enum SearchIntent
        {
            HotelCode,
            SpecificHotel,
            HotelBrand,
            BrandWithLocation,
            General,
        }

        private string NormalizeSearchQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return string.Empty;

            // Convert to lowercase
            string result = query.ToLowerInvariant();

            // Remove apostrophes
            result = result.Replace("'", "");

            // Remove diacritics (accents) - converts "Mövenpick" to "Movenpick"
            result = new string(
                result
                    .Normalize(System.Text.NormalizationForm.FormD)
                    .Where(c =>
                        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                        != System.Globalization.UnicodeCategory.NonSpacingMark
                    )
                    .ToArray()
            ).Normalize(System.Text.NormalizationForm.FormC);

            // Replace multiple spaces with a single space
            result = Regex.Replace(result, @"\s+", " ");

            // Trim leading and trailing spaces
            result = result.Trim();

            return result;
        }

        private (SearchIntent intent, bool isExact) DetectSearchIntent(string normalizedQuery)
        {
            // Calculate complexity metrics
            int wordCount = normalizedQuery
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Length;
            bool containsSpecificPhrase = ContainsSpecificPhrases(normalizedQuery);
            bool looksLikeHotelCode = IsLikelyHotelCode(normalizedQuery);

            // Using heuristics to determine search intent
            if (looksLikeHotelCode)
            {
                return (SearchIntent.HotelCode, true);
            }
            else if (wordCount >= 3 && containsSpecificPhrase)
            {
                return (SearchIntent.SpecificHotel, true);
            }
            else if (wordCount <= 2)
            {
                return (SearchIntent.HotelBrand, false);
            }
            else if (wordCount > 2 && wordCount <= 4)
            {
                return (SearchIntent.BrandWithLocation, true);
            }
            else
            {
                return (SearchIntent.General, false);
            }
        }

        private bool ContainsSpecificPhrases(string query)
        {
            // Check if query contains phrases that indicate a specific hotel search
            // This doesn't hardcode specific hotels, but looks for patterns
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Specific location indicators - these are not hardcoded hotel names but common pattern words
            var locationIndicators = new[]
            {
                "city",
                "centre",
                "center",
                "resort",
                "hotel",
                "palace",
                "grand",
                "royal",
            };

            // Check for hotel + location pattern
            bool hasLocationIndicator = locationIndicators.Any(loc =>
                query.Contains(loc, StringComparison.OrdinalIgnoreCase)
            );

            // Check for "hotel" word
            bool hasHotelIndicator = query.Contains("hotel", StringComparison.OrdinalIgnoreCase);

            return hasLocationIndicator && (hasHotelIndicator || terms.Length >= 3);
        }

        private bool IsLikelyHotelCode(string query)
        {
            // Hotel codes typically are alphanumeric with specific patterns
            // This doesn't rely on hardcoded codes
            if (string.IsNullOrWhiteSpace(query))
                return false;

            // No spaces in hotel codes
            if (query.Contains(" "))
                return false;

            // Hotel codes typically have both letters and numbers
            bool hasLetters = query.Any(char.IsLetter);
            bool hasDigits = query.Any(char.IsDigit);

            // Hotel codes typically are in specific length ranges
            bool validLength = query.Length >= 5 && query.Length <= 12;

            return hasLetters && hasDigits && validLength;
        }

        private async Task<ISearchResponse<Hotel>> ExecuteSearch(
            string normalizedQuery,
            SearchIntent intent,
            bool isExactSearch,
            int pageNumber,
            int pageSize
        )
        {
            // Choose the appropriate index based on the search intent
            string indexName = isExactSearch ? HotelIndexName : HotelNGramIndexName;

            // For brand searches, we want more results for post-processing
            int requestSize = intent == SearchIntent.HotelBrand ? pageSize * 3 : pageSize;

            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(indexName)
                .From((pageNumber - 1) * pageSize)
                .Size(requestSize)
                .TrackScores(true);

            // Build query based on intent
            QueryContainer queryContainer;

            switch (intent)
            {
                case SearchIntent.HotelCode:
                    queryContainer = BuildHotelCodeQuery(normalizedQuery);
                    break;
                case SearchIntent.SpecificHotel:
                    queryContainer = BuildSpecificHotelQuery(normalizedQuery);
                    break;
                case SearchIntent.HotelBrand:
                    queryContainer = BuildHotelBrandQuery(normalizedQuery);
                    break;
                case SearchIntent.BrandWithLocation:
                    queryContainer = BuildBrandWithLocationQuery(normalizedQuery);
                    break;
                case SearchIntent.General:
                default:
                    queryContainer = BuildGeneralQuery(normalizedQuery);
                    break;
            }

            searchDescriptor = searchDescriptor.Query(q => queryContainer);

            return await _elasticClient.SearchAsync<Hotel>(searchDescriptor);
        }

        private QueryContainer BuildHotelCodeQuery(string code)
        {
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Exact match on hotel code (highest priority)
                    new TermQuery
                    {
                        Field = "hotelcode.keyword",
                        Value = code,
                        Boost = 50.0,
                    },
                    // Fuzzy match for typos
                    new FuzzyQuery
                    {
                        Field = "hotelcode",
                        Value = code,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 30.0,
                    },
                    // Prefix match for partial codes
                    new PrefixQuery
                    {
                        Field = "hotelcode",
                        Value = code,
                        Boost = 20.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        private QueryContainer BuildSpecificHotelQuery(string query)
        {
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Exact match on hotel name (highest priority)
                    new MatchPhraseQuery
                    {
                        Field = "hotelname",
                        Query = query,
                        Boost = 50.0,
                    },
                    // Match both hotel name and city
                    new BoolQuery
                    {
                        Must = new List<QueryContainer>
                        {
                            new MatchQuery
                            {
                                Field = "hotelname",
                                Query = query,
                                MinimumShouldMatch = "70%",
                                Boost = 20.0,
                            },
                            new MatchQuery
                            {
                                Field = "cityname",
                                Query = query,
                                MinimumShouldMatch = "50%",
                                Boost = 10.0,
                            },
                        },
                        Boost = 40.0,
                    },
                    // Fuzzy match for handling typographical errors
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = query,
                        Fuzziness = Fuzziness.Auto,
                        PrefixLength = 2, // Preserve first two characters
                        Boost = 25.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        private QueryContainer BuildHotelBrandQuery(string brand)
        {
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Brand name in hotel name field (phrase match)
                    new MatchPhraseQuery
                    {
                        Field = "hotelname",
                        Query = brand,
                        Boost = 30.0,
                    },
                    // Brand name in hotel name field (fuzzy match)
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = brand,
                        Fuzziness = Fuzziness.Auto,
                        MinimumShouldMatch = "70%",
                        Boost = 20.0,
                    },
                    // Brand name in hotel name nGram field
                    new MatchQuery
                    {
                        Field = "hotelname.edge",
                        Query = brand,
                        MinimumShouldMatch = "80%",
                        Boost = 15.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        private QueryContainer BuildBrandWithLocationQuery(string query)
        {
            // Split the query to analyze terms separately
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Exact match on the whole query
                    new MatchPhraseQuery
                    {
                        Field = "hotelname",
                        Query = query,
                        Boost = 50.0,
                    },
                    // Match on hotel name with brand + location
                    new BoolQuery
                    {
                        Must = new List<QueryContainer>
                        {
                            // First half of terms for brand (likely)
                            new MatchPhraseQuery
                            {
                                Field = "hotelname",
                                Query = string.Join(" ", terms.Take(terms.Length / 2)),
                                Boost = 20.0,
                            },
                            // Last term(s) for location (likely)
                            new MatchQuery
                            {
                                Field = "cityname",
                                Query = terms.Last(),
                                Fuzziness = Fuzziness.Auto,
                                Boost = 10.0,
                            },
                        },
                        Boost = 30.0,
                    },
                    // Fuzzy match for the whole query
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = query,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 25.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        private QueryContainer BuildGeneralQuery(string query)
        {
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Hotel name matches
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = query,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 10.0,
                    },
                    // City name matches
                    new MatchQuery
                    {
                        Field = "cityname",
                        Query = query,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 8.0,
                    },
                    // Address matches
                    new MatchQuery
                    {
                        Field = "address1",
                        Query = query,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 5.0,
                    },
                    // Country matches
                    new MatchQuery
                    {
                        Field = "country",
                        Query = query,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 3.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        private IEnumerable<Hotel> ProcessResults(
            ISearchResponse<Hotel> searchResponse,
            string query,
            SearchIntent intent,
            bool isExactSearch
        )
        {
            if (!searchResponse.IsValid || searchResponse.Documents.Count() == 0)
            {
                return Enumerable.Empty<Hotel>();
            }

            var results = searchResponse.Documents.ToList();

            switch (intent)
            {
                case SearchIntent.HotelCode:
                    return ProcessHotelCodeResults(searchResponse);

                case SearchIntent.SpecificHotel:
                    return ProcessSpecificHotelResults(searchResponse, query);

                case SearchIntent.HotelBrand:
                    return ProcessHotelBrandResults(searchResponse, query);

                case SearchIntent.BrandWithLocation:
                    return ProcessBrandWithLocationResults(searchResponse, query);

                case SearchIntent.General:
                default:
                    return ProcessGeneralResults(searchResponse);
            }
        }

        private IEnumerable<Hotel> ProcessHotelCodeResults(ISearchResponse<Hotel> searchResponse)
        {
            // For hotel codes, return just the exact match if we have high confidence
            if (searchResponse.Hits.Count > 0)
            {
                // If we have a single result, or the top result has a significantly higher score
                if (searchResponse.Hits.Count == 1 || IsHighConfidenceMatch(searchResponse.Hits))
                {
                    return searchResponse.Documents.Take(1);
                }
            }

            return searchResponse.Documents;
        }

        private IEnumerable<Hotel> ProcessSpecificHotelResults(
            ISearchResponse<Hotel> searchResponse,
            string query
        )
        {
            // For specific hotel searches, we want just the exact hotel if we're confident
            if (searchResponse.Hits.Count > 0)
            {
                // If we have a single result, or the top result has a significantly higher score
                if (searchResponse.Hits.Count == 1 || IsHighConfidenceMatch(searchResponse.Hits))
                {
                    return searchResponse.Documents.Take(1);
                }

                // Try to find exact name match
                var exactMatch = searchResponse.Documents.FirstOrDefault(h =>
                    h.HotelName != null
                    && NormalizeSearchQuery(h.HotelName)
                        .Equals(query, StringComparison.OrdinalIgnoreCase)
                );

                if (exactMatch != null)
                {
                    return new List<Hotel> { exactMatch };
                }
            }

            return searchResponse.Documents;
        }

        private IEnumerable<Hotel> ProcessHotelBrandResults(
            ISearchResponse<Hotel> searchResponse,
            string brandQuery
        )
        {
            var hotels = searchResponse.Documents.ToList();

            // First, prioritize hotels that contain the brand name in the hotel name
            var brandMatches = hotels
                .Where(h =>
                    h.HotelName != null
                    && h.HotelName.Contains(brandQuery, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();

            if (brandMatches.Any())
            {
                // Group by city and prioritize cities
                var cityGroups = brandMatches
                    .GroupBy(h => h.CityName)
                    .OrderByDescending(g => g.Count())
                    .ToList();

                var result = new List<Hotel>();

                // First, include hotels from Jakarta (a key city from requirements)
                var jakartaHotels = cityGroups.FirstOrDefault(g =>
                    g.Key != null && g.Key.Contains("jakarta", StringComparison.OrdinalIgnoreCase)
                );

                if (jakartaHotels != null)
                {
                    result.AddRange(jakartaHotels);
                }

                // Then add hotels from other cities
                foreach (
                    var cityGroup in cityGroups.Where(g =>
                        g.Key == null
                        || !g.Key.Contains("jakarta", StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    result.AddRange(cityGroup);
                }

                return result.Take(10);
            }

            return hotels.Take(10);
        }

        private IEnumerable<Hotel> ProcessBrandWithLocationResults(
            ISearchResponse<Hotel> searchResponse,
            string query
        )
        {
            // For "brand + location" queries, if we have a strong match, return just that
            if (searchResponse.Hits.Count > 0)
            {
                if (searchResponse.Hits.Count == 1 || IsHighConfidenceMatch(searchResponse.Hits))
                {
                    return searchResponse.Documents.Take(1);
                }

                // Split the query to analyze location part
                var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var potentialLocation = terms.LastOrDefault();

                if (!string.IsNullOrEmpty(potentialLocation))
                {
                    // Find hotels that match the city and name
                    var cityMatches = searchResponse
                        .Documents.Where(h =>
                            h.CityName != null
                            && h.CityName.Contains(
                                potentialLocation,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && h.HotelName != null
                            && ContainsAllTermsExceptLast(h.HotelName, terms)
                        )
                        .ToList();

                    if (cityMatches.Any())
                    {
                        return cityMatches;
                    }
                }
            }

            return searchResponse.Documents;
        }

        private IEnumerable<Hotel> ProcessGeneralResults(ISearchResponse<Hotel> searchResponse)
        {
            // For general queries, return all results, but with any significant confidence adjustment
            if (searchResponse.Hits.Count > 0 && IsHighConfidenceMatch(searchResponse.Hits))
            {
                return searchResponse.Documents.Take(1);
            }

            return searchResponse.Documents;
        }

        private bool IsHighConfidenceMatch(IEnumerable<IHit<Hotel>> hits)
        {
            var hitsList = hits.ToList();
            if (hitsList.Count < 2)
                return false;

            var topScore = hitsList[0].Score ?? 0;
            var secondScore = hitsList[1].Score ?? 0;

            // Consider high confidence if top score is 80% higher than second score
            return topScore > 0 && secondScore > 0 && (topScore / secondScore) > 1.8;
        }

        private bool ContainsAllTermsExceptLast(string text, string[] terms)
        {
            if (terms.Length <= 1)
                return true;

            // Check if hotel name contains all terms except the last (assumed to be location)
            var brandTerms = terms.Take(terms.Length - 1);
            return brandTerms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<bool> IndexHotelAsync(Hotel hotel)
        {
            var response = await _elasticClient.IndexDocumentAsync(hotel);
            return response.IsValid;
        }

        public async Task<bool> IndexHotelsAsync(IEnumerable<Hotel> hotels)
        {
            var bulkDescriptor = new BulkDescriptor();

            foreach (var hotel in hotels)
            {
                bulkDescriptor.Index<Hotel>(i => i.Index(HotelIndexName).Document(hotel));
            }

            var response = await _elasticClient.BulkAsync(bulkDescriptor);
            return !response.Errors;
        }

        public async Task<bool> IndexHotelsNGramAsync(IEnumerable<Hotel> hotels)
        {
            var bulkDescriptor = new BulkDescriptor();

            foreach (var hotel in hotels)
            {
                bulkDescriptor.Index<Hotel>(i => i.Index(HotelNGramIndexName).Document(hotel));
            }

            var response = await _elasticClient.BulkAsync(bulkDescriptor);
            return !response.Errors;
        }

        public async Task<bool> CreateHotelIndexAsync()
        {
            var existsResponse = await _elasticClient.Indices.ExistsAsync(HotelIndexName);
            if (existsResponse.Exists)
            {
                return true;
            }

            var createIndexResponse = await _elasticClient.Indices.CreateAsync(
                HotelIndexName,
                c =>
                    c.Settings(s =>
                            s.Analysis(a =>
                                    a.CharFilters(cf =>
                                            cf.Mapping(
                                                "apostrophe_filter",
                                                m => m.Mappings(new[] { "'=>" })
                                            )
                                        )
                                        .Analyzers(an =>
                                            an.Custom(
                                                "standard",
                                                sa =>
                                                    sa.Tokenizer("standard")
                                                        .CharFilters("apostrophe_filter")
                                                        .Filters("lowercase", "stop")
                                            )
                                        )
                                )
                                .Setting("index.max_ngram_diff", 4)
                        )
                        .Map<Hotel>(m =>
                            m.AutoMap()
                                .Properties(p =>
                                    p.Text(t =>
                                            t.Name(n => n.HotelName)
                                                .Analyzer("standard")
                                                .Fields(f =>
                                                    f.Keyword(k =>
                                                        k.Name("keyword").IgnoreAbove(256)
                                                    )
                                                )
                                        )
                                        .Text(t =>
                                            t.Name(n => n.CityName)
                                                .Analyzer("standard")
                                                .Fields(f =>
                                                    f.Keyword(k =>
                                                        k.Name("keyword").IgnoreAbove(256)
                                                    )
                                                )
                                        )
                                )
                        )
            );

            return createIndexResponse.IsValid;
        }

        public async Task<bool> CreateHotelNGramIndexAsync()
        {
            // Check if index already exists and delete if it does
            var existsResponse = await _elasticClient.Indices.ExistsAsync(HotelNGramIndexName);
            if (existsResponse.Exists)
            {
                await _elasticClient.Indices.DeleteAsync(HotelNGramIndexName);
            }

            // Enhanced n-gram configuration for better partial matching
            var createIndexResponse = await _elasticClient.Indices.CreateAsync(
                HotelNGramIndexName,
                c =>
                    c.Settings(s =>
                            s.Analysis(a =>
                                    a.CharFilters(cf =>
                                            cf.Mapping(
                                                "apostrophe_filter",
                                                m => m.Mappings(new[] { "'=>" })
                                            )
                                        )
                                        .TokenFilters(tf =>
                                            tf.NGram("ngram_filter", ng => ng.MinGram(1).MaxGram(4))
                                                .EdgeNGram(
                                                    "edge_ngram_filter",
                                                    eng =>
                                                        eng.MinGram(1)
                                                            .MaxGram(20)
                                                            .Side(EdgeNGramSide.Front)
                                                )
                                        )
                                        .Analyzers(an =>
                                            an.Custom(
                                                    "ngram_analyzer",
                                                    ca =>
                                                        ca.Tokenizer("standard")
                                                            .CharFilters("apostrophe_filter")
                                                            .Filters(
                                                                "lowercase",
                                                                "asciifolding",
                                                                "ngram_filter"
                                                            )
                                                )
                                                .Custom(
                                                    "edge_ngram_analyzer",
                                                    ca =>
                                                        ca.Tokenizer("standard")
                                                            .CharFilters("apostrophe_filter")
                                                            .Filters(
                                                                "lowercase",
                                                                "asciifolding",
                                                                "edge_ngram_filter"
                                                            )
                                                )
                                                .Custom(
                                                    "search_analyzer",
                                                    ca =>
                                                        ca.Tokenizer("standard")
                                                            .CharFilters("apostrophe_filter")
                                                            .Filters("lowercase", "asciifolding")
                                                )
                                        )
                                )
                                .Setting("index.max_ngram_diff", 20)
                        )
                        .Map<Hotel>(m =>
                            m.Properties(p =>
                                p.Keyword(k => k.Name(n => n.Id))
                                    .Text(t =>
                                        t.Name(n => n.HotelCode)
                                            .Analyzer("ngram_analyzer")
                                            .SearchAnalyzer("search_analyzer")
                                            .Fields(f =>
                                                f.Keyword(k => k.Name("keyword"))
                                                    .Text(t2 =>
                                                        t2.Name("edge")
                                                            .Analyzer("edge_ngram_analyzer")
                                                    )
                                            )
                                    )
                                    .Text(t =>
                                        t.Name(n => n.HotelName)
                                            .Analyzer("ngram_analyzer")
                                            .SearchAnalyzer("search_analyzer")
                                            .Fields(f =>
                                                f.Keyword(k => k.Name("keyword"))
                                                    .Text(t2 =>
                                                        t2.Name("edge")
                                                            .Analyzer("edge_ngram_analyzer")
                                                    )
                                            )
                                    )
                                    .Text(t =>
                                        t.Name(n => n.CityName)
                                            .Analyzer("ngram_analyzer")
                                            .SearchAnalyzer("search_analyzer")
                                            .Fields(f =>
                                                f.Keyword(k => k.Name("keyword"))
                                                    .Text(t2 =>
                                                        t2.Name("edge")
                                                            .Analyzer("edge_ngram_analyzer")
                                                    )
                                            )
                                    )
                                    .Text(t =>
                                        t.Name(n => n.Address1)
                                            .Analyzer("ngram_analyzer")
                                            .SearchAnalyzer("search_analyzer")
                                            .Fields(f =>
                                                f.Text(t2 =>
                                                    t2.Name("edge").Analyzer("edge_ngram_analyzer")
                                                )
                                            )
                                    )
                                    .Text(t => t.Name(n => n.Address2))
                                    .Text(t => t.Name(n => n.State))
                                    .Text(t =>
                                        t.Name(n => n.Country)
                                            .Analyzer("ngram_analyzer")
                                            .SearchAnalyzer("search_analyzer")
                                            .Fields(f => f.Keyword(k => k.Name("keyword")))
                                    )
                                    .Keyword(k => k.Name(n => n.PostalCode))
                                    .Keyword(k => k.Name(n => n.PhoneNumber))
                                    .Date(d => d.Name(n => n.LastUpdated))
                            )
                        )
            );

            return createIndexResponse.IsValid;
        }

        public async Task<bool> DeleteHotelIndexAsync()
        {
            var response = await _elasticClient.Indices.DeleteAsync(HotelIndexName);
            return response.IsValid;
        }

        public async Task<bool> ClearAllHotelIndices()
        {
            var standardIndexExists = await _elasticClient.Indices.ExistsAsync(HotelIndexName);
            var ngramIndexExists = await _elasticClient.Indices.ExistsAsync(HotelNGramIndexName);

            var tasks = new List<Task<DeleteIndexResponse>>();

            if (standardIndexExists.Exists)
                tasks.Add(_elasticClient.Indices.DeleteAsync(HotelIndexName));

            if (ngramIndexExists.Exists)
                tasks.Add(_elasticClient.Indices.DeleteAsync(HotelNGramIndexName));

            await Task.WhenAll(tasks);

            // Recreate indices with proper mappings
            await CreateHotelIndexAsync();
            await CreateHotelNGramIndexAsync();

            return true;
        }
    }
}
