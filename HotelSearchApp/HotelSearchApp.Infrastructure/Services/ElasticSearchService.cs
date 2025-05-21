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

        // Keep all existing methods from original ElasticSearchService.cs
        // This includes SearchHotelsAsync, IndexHotelAsync, IndexHotelsAsync, etc.

        // For brevity, only showing the new/modified methods

        public async Task<ElasticSearchResponse<Hotel>> SearchHotelsAsync(
            HotelSearchParameters searchParams
        )
        {
            var stopwatch = Stopwatch.StartNew();

            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelIndexName)
                .From((searchParams.PageNumber - 1) * searchParams.PageSize)
                .Size(searchParams.PageSize)
                .RequestCache(false) // No caching
                .TrackScores(true); // Track scores for relevancy

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
                        PrefixLength = 1, // Preserve the first character for more accurate matches
                        MaxExpansions = 50, // Increase expansion for broader matching
                        Boost = 3.0, // Increased priority for hotel code match
                    }
                );

                // Add exact match with higher boost for precise searches
                shouldClauses.Add(
                    new TermQuery
                    {
                        Field = "hotelcode",
                        Value = searchParams.HotelCode,
                        Boost = 4.0, // Highest priority for exact match
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
                        Boost = 2.0, // Increased from 1.5
                    }
                );

                // Add a prefix query to handle partial city name inputs
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
                        Boost = 2.5, // Increased from 1.8
                    }
                );

                // Add a prefix query to handle partial hotel name inputs
                shouldClauses.Add(
                    new PrefixQuery
                    {
                        Field = "hotelname",
                        Value = searchParams.HotelName.ToLowerInvariant(),
                        Boost = 2.0,
                    }
                );

                // Add matching by phrase to prioritize exact phrases in names
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
                        Boost = 1.5, // Increased from 1.0
                    }
                );

                // Add exact phrase matching for addresses
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
                        MinimumShouldMatch = "60%", // Increased from 40%
                        Boost = 2.0, // Increased from 1.5
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
                        MinimumShouldMatch = "70%", // Increased from 60%
                        Boost = 1.5, // Increased from 1.0
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
                        MinimumShouldMatch = "70%", // Increased from 60%
                        Boost = 1.8, // Increased from 1.0
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
                        MinimumShouldMatch = "70%", // Increased from 60%
                        Boost = 1.5, // Increased from 1.0
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
                if (topScore > 0 && secondScore > 0 && (topScore / secondScore) > 1.8) // Increased from 1.5
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

            // Normalize query for better matching
            var normalizedQuery = NormalizeSearchQuery(searchQuery);
            var correctedQuery = CorrectCommonTypos(normalizedQuery);

            // Detect search type
            bool isHotelCode = IsHotelCode(normalizedQuery);
            bool isCityName = IsCityName(normalizedQuery);
            bool isCountryName = IsCountryName(normalizedQuery);
            bool isHotelBrand = IsKnownHotelBrand(normalizedQuery);
            bool isExactHotelName = IsExactHotelName(normalizedQuery);

            // Request more results for filtering
            var requestPageSize = pageSize * 10;

            // Build search based on detected type
            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelNGramIndexName)
                .Size(requestPageSize)
                .RequestCache(false)
                .TrackScores(true);

            QueryContainer queryContainer;

            // Building query strategies based on search type
            if (isHotelCode)
            {
                // Hotel Code search (highest priority)
                queryContainer = BuildHotelCodeQuery(normalizedQuery);
            }
            else if (isExactHotelName)
            {
                // Exact hotel name search
                queryContainer = BuildExactHotelNameQuery(normalizedQuery, correctedQuery);
            }
            else if (isHotelBrand)
            {
                // Hotel brand search
                queryContainer = BuildHotelBrandQuery(normalizedQuery, correctedQuery);
            }
            else if (isCityName)
            {
                // City name search
                queryContainer = BuildCityNameQuery(normalizedQuery, correctedQuery);
            }
            else if (isCountryName)
            {
                // Country name search
                queryContainer = BuildCountryNameQuery(normalizedQuery, correctedQuery);
            }
            else
            {
                // General search (fallback case)
                queryContainer = BuildGeneralSearchQuery(normalizedQuery, correctedQuery);
            }

            // Apply the query
            searchDescriptor = searchDescriptor.Query(q => queryContainer);

            // Execute search
            var searchResponse = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

            stopwatch.Stop();

            // Process results based on search type
            List<Hotel> processedResults;

            if (isHotelCode)
            {
                processedResults = ProcessHotelCodeResults(
                    searchResponse.Documents.ToList(),
                    normalizedQuery
                );
            }
            else if (isExactHotelName)
            {
                processedResults = ProcessExactHotelNameResults(
                    searchResponse.Documents.ToList(),
                    normalizedQuery
                );
            }
            else if (isHotelBrand)
            {
                processedResults = ProcessHotelBrandResults(
                    searchResponse.Documents.ToList(),
                    normalizedQuery
                );
            }
            else if (isCityName)
            {
                processedResults = ProcessCityNameResults(
                    searchResponse.Documents.ToList(),
                    normalizedQuery
                );
            }
            else if (isCountryName)
            {
                processedResults = ProcessCountryNameResults(
                    searchResponse.Documents.ToList(),
                    normalizedQuery
                );
            }
            else
            {
                processedResults = ProcessGeneralResults(
                    searchResponse.Documents.ToList(),
                    normalizedQuery
                );
            }

            // Apply pagination to processed results
            int totalResults = processedResults.Count;
            var paginatedResults = processedResults
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return new ElasticSearchResponse<Hotel>
            {
                Items = paginatedResults,
                TotalHits = totalResults,
                ElapsedTime = stopwatch.Elapsed,
                PageNumber = pageNumber,
                PageSize = pageSize,
            };
        }

        // Query building methods

        private QueryContainer BuildHotelCodeQuery(string normalizedQuery)
        {
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Exact match on hotel code (highest priority)
                    new TermQuery
                    {
                        Field = "hotelcode.keyword",
                        Value = normalizedQuery,
                        Boost = 50.0,
                    },
                    // Fuzzy match on hotel code for typo tolerance
                    new FuzzyQuery
                    {
                        Field = "hotelcode",
                        Value = normalizedQuery,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 30.0,
                    },
                    // Prefix match on hotel code for partial codes
                    new PrefixQuery
                    {
                        Field = "hotelcode",
                        Value = normalizedQuery,
                        Boost = 20.0,
                    },
                    // Edge n-gram match for better partial matching
                    new MatchQuery
                    {
                        Field = "hotelcode.edge",
                        Query = normalizedQuery,
                        Boost = 10.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        private QueryContainer BuildExactHotelNameQuery(
            string normalizedQuery,
            string correctedQuery
        )
        {
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Exact match on hotel name
                    new TermQuery
                    {
                        Field = "hotelname.keyword",
                        Value = normalizedQuery,
                        Boost = 50.0,
                    },
                    // Try with corrected query if available
                    new TermQuery
                    {
                        Field = "hotelname.keyword",
                        Value = correctedQuery,
                        Boost = 48.0,
                    },
                    // Match phrase for exact phrase matching
                    new MatchPhraseQuery
                    {
                        Field = "hotelname",
                        Query = normalizedQuery,
                        Boost = 40.0,
                    },
                    // Fuzzy match for typo tolerance
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = normalizedQuery,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 30.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        private QueryContainer BuildHotelBrandQuery(string normalizedQuery, string correctedQuery)
        {
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Match phrases containing the brand in hotel name
                    new MatchPhraseQuery
                    {
                        Field = "hotelname",
                        Query = normalizedQuery,
                        Boost = 40.0,
                    },
                    // Match the brand name using fuzzy matching
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = normalizedQuery,
                        Fuzziness = Fuzziness.Auto,
                        MinimumShouldMatch = "70%",
                        Boost = 30.0,
                    },
                    // Prefix match for partial brand names
                    new PrefixQuery
                    {
                        Field = "hotelname",
                        Value = normalizedQuery,
                        Boost = 20.0,
                    },
                    // Edge n-gram for better prefix matching
                    new MatchQuery
                    {
                        Field = "hotelname.edge",
                        Query = normalizedQuery,
                        Boost = 15.0,
                    },
                    // Try with corrected brand if available
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = correctedQuery,
                        Fuzziness = Fuzziness.Auto,
                        MinimumShouldMatch = "70%",
                        Boost = 30.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        private QueryContainer BuildCityNameQuery(string normalizedQuery, string correctedQuery)
        {
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Exact match on city name
                    new TermQuery
                    {
                        Field = "cityname.keyword",
                        Value = normalizedQuery,
                        Boost = 50.0,
                    },
                    // Try with corrected city name
                    new TermQuery
                    {
                        Field = "cityname.keyword",
                        Value = correctedQuery,
                        Boost = 48.0,
                    },
                    // Match phrase for exact phrase matching
                    new MatchPhraseQuery
                    {
                        Field = "cityname",
                        Query = normalizedQuery,
                        Boost = 40.0,
                    },
                    // Fuzzy match for typo tolerance
                    new MatchQuery
                    {
                        Field = "cityname",
                        Query = normalizedQuery,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 30.0,
                    },
                    // Prefix match for partial city name inputs
                    new PrefixQuery
                    {
                        Field = "cityname",
                        Value = normalizedQuery,
                        Boost = 20.0,
                    },
                    // Edge n-gram for better prefix matching
                    new MatchQuery
                    {
                        Field = "cityname.edge",
                        Query = normalizedQuery,
                        Boost = 15.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        private QueryContainer BuildCountryNameQuery(string normalizedQuery, string correctedQuery)
        {
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Exact match on country name
                    new TermQuery
                    {
                        Field = "country.keyword",
                        Value = normalizedQuery,
                        Boost = 50.0,
                    },
                    // Try with corrected country name
                    new TermQuery
                    {
                        Field = "country.keyword",
                        Value = correctedQuery,
                        Boost = 48.0,
                    },
                    // Fuzzy match for typo tolerance
                    new MatchQuery
                    {
                        Field = "country",
                        Query = normalizedQuery,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 30.0,
                    },
                    // Edge n-gram for better prefix matching
                    new MatchQuery
                    {
                        Field = "country.edge",
                        Query = normalizedQuery,
                        Boost = 15.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        private QueryContainer BuildGeneralSearchQuery(
            string normalizedQuery,
            string correctedQuery
        )
        {
            // First, check if this is a brand+city query
            bool isBrandCityQuery = false;
            string brandTerm = null;
            string cityTerm = null;

            // Split query into terms and check if it has a known hotel brand and city
            var terms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length >= 2)
            {
                foreach (var term in terms)
                {
                    if (IsKnownHotelBrand(term))
                    {
                        isBrandCityQuery = true;
                        brandTerm = term;
                    }
                    else if (IsCityName(term))
                    {
                        isBrandCityQuery = true;
                        cityTerm = term;
                    }
                }
            }

            // If this is a brand+city query, adjust the boosting significantly
            if (isBrandCityQuery && brandTerm != null && cityTerm != null)
            {
                return new BoolQuery
                {
                    Should = new List<QueryContainer>
                    {
                        // Highest priority: Exact match on brand+city in hotel name
                        new BoolQuery
                        {
                            Must = new List<QueryContainer>
                            {
                                new MatchPhraseQuery
                                {
                                    Field = "hotelname",
                                    Query = brandTerm,
                                    Boost = 20.0,
                                },
                                new MatchPhraseQuery
                                {
                                    Field = "cityname",
                                    Query = cityTerm,
                                    Boost = 20.0,
                                },
                            },
                            Boost = 100.0,
                        },
                        // Second priority: Brand in hotel name + city match
                        new BoolQuery
                        {
                            Must = new List<QueryContainer>
                            {
                                new MatchQuery
                                {
                                    Field = "hotelname",
                                    Query = brandTerm,
                                    Boost = 10.0,
                                },
                                new MatchQuery
                                {
                                    Field = "cityname",
                                    Query = cityTerm,
                                    Boost = 10.0,
                                },
                            },
                            Boost = 80.0,
                        },
                        // Third priority: Just brand match in hotel name
                        new MatchPhraseQuery
                        {
                            Field = "hotelname",
                            Query = brandTerm,
                            Boost = 60.0,
                        },
                        // Fourth priority: Just city match
                        new MatchPhraseQuery
                        {
                            Field = "cityname",
                            Query = cityTerm,
                            Boost = 40.0,
                        },

                        // Rest of your normal search logic with lower boost values
                        // ...existing query clauses with lower boost values...
                    },
                    MinimumShouldMatch = 1,
                };
            }

            // For regular queries, use the existing logic
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // Your existing query logic...
                },
                MinimumShouldMatch = 1,
            };
        }

        // Result processing methods

        private List<Hotel> ProcessHotelCodeResults(List<Hotel> results, string query)
        {
            if (!results.Any())
                return new List<Hotel>();

            // For hotel codes, don't limit by city grouping - just return exact matches first
            var exactMatches = results
                .Where(h =>
                    h.HotelCode != null
                    && NormalizeSearchQuery(h.HotelCode)
                        .Equals(query, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();

            if (exactMatches.Any())
                return exactMatches.Take(10).ToList();

            // Then partial matches
            var partialMatches = results
                .Where(h =>
                    h.HotelCode != null
                    && (
                        NormalizeSearchQuery(h.HotelCode).Contains(query)
                        || query.Contains(NormalizeSearchQuery(h.HotelCode))
                    )
                )
                .Take(10)
                .ToList();

            return partialMatches.Any() ? partialMatches : results.Take(10).ToList();
        }

        private List<Hotel> ProcessExactHotelNameResults(List<Hotel> results, string query)
        {
            if (!results.Any())
                return new List<Hotel>();

            // For exact hotel names, return perfect matches first
            var exactMatches = results
                .Where(h =>
                    h.HotelName != null
                    && NormalizeSearchQuery(h.HotelName)
                        .Equals(query, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();

            if (exactMatches.Any())
                return exactMatches.Take(10).ToList();

            // Then closest matches
            return results.Take(10).ToList();
        }

        private List<Hotel> ProcessHotelBrandResults(List<Hotel> results, string query)
        {
            if (!results.Any())
                return new List<Hotel>();

            // For hotel brands, don't group by city - just take top matches
            // This ensures all Ritz Carlton hotels show up regardless of city
            return results.Take(10).ToList();
        }

        private List<Hotel> ProcessCityNameResults(List<Hotel> results, string query)
        {
            if (!results.Any())
                return new List<Hotel>();

            // For city names, get hotels from that city, but ensure max 10 hotels
            var cityHotels = results
                .Where(h =>
                    h.CityName != null
                    && NormalizeSearchQuery(h.CityName)
                        .Equals(query, StringComparison.OrdinalIgnoreCase)
                )
                .Take(10)
                .ToList();

            return cityHotels.Any() ? cityHotels : results.Take(10).ToList();
        }

        private List<Hotel> ProcessCountryNameResults(List<Hotel> results, string query)
        {
            if (!results.Any())
                return new List<Hotel>();

            // For country names, get top 5 cities from that country and max 10 hotels distributed among them
            var countryHotels = results
                .Where(h =>
                    h.Country != null
                    && NormalizeSearchQuery(h.Country)
                        .Equals(query, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();

            if (!countryHotels.Any())
                return results.Take(10).ToList();

            var cityGroups = countryHotels
                .GroupBy(h => h.CityName)
                .Where(g => g.Key != null)
                .Take(5) // Max 5 cities
                .ToList();

            if (!cityGroups.Any())
                return countryHotels.Take(10).ToList();

            var distributedResults = new List<Hotel>();
            int hotelsPerCity = Math.Max(1, 10 / cityGroups.Count);

            foreach (var city in cityGroups)
            {
                distributedResults.AddRange(city.Take(hotelsPerCity));
            }

            // Fill remaining slots
            int remaining = 10 - distributedResults.Count;
            if (remaining > 0)
            {
                foreach (var city in cityGroups)
                {
                    distributedResults.AddRange(city.Skip(hotelsPerCity).Take(remaining));
                    remaining -= Math.Max(0, city.Count() - hotelsPerCity);
                    if (remaining <= 0)
                        break;
                }
            }

            return distributedResults.Take(10).ToList();
        }

        private List<Hotel> ProcessGeneralResults(List<Hotel> results, string query)
        {
            if (!results.Any())
                return new List<Hotel>();

            // General search - max 5 cities, max 10 hotels total
            var cityGroups = results
                .GroupBy(h => h.CityName)
                .Where(g => g.Key != null)
                .Take(5) // Max 5 cities
                .ToList();

            if (!cityGroups.Any())
                return results.Take(10).ToList();

            var distributedResults = new List<Hotel>();
            int hotelsPerCity = Math.Max(1, 10 / cityGroups.Count);

            foreach (var city in cityGroups)
            {
                distributedResults.AddRange(city.Take(hotelsPerCity));
            }

            // Fill remaining slots
            int remaining = 10 - distributedResults.Count;
            if (remaining > 0)
            {
                foreach (var city in cityGroups)
                {
                    distributedResults.AddRange(city.Skip(hotelsPerCity).Take(remaining));
                    remaining -= Math.Max(0, city.Count() - hotelsPerCity);
                    if (remaining <= 0)
                        break;
                }
            }

            return distributedResults.Take(10).ToList();
        }

        // Utility methods

        private bool IsHotelCode(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return false;

            // Numeric-only hotel code (like 10000121)
            if (query.All(char.IsDigit) && query.Length >= 7 && query.Length <= 10)
                return true;

            // Alphanumeric hotel code (like AL10000267)
            if (
                query.Length >= 8
                && query.Length <= 12
                && query.Any(char.IsLetter)
                && query.Any(char.IsDigit)
            )
                return true;

            return false;
        }

        private bool IsCityName(string query)
        {
            // List of known cities (can be expanded)
            var knownCities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "jakarta",
                "surabaya",
                "bandung",
                "bali",
                "singapore",
                "manila",
                "kuala lumpur",
                "bangkok",
                "tokyo",
                "shanghai",
                "hong kong",
                "new york",
                "london",
                "paris",
                "madrid",
                "rome",
                "berlin",
                "sydney",
                "melbourne",
                "dubai",
                "sao paulo",
                "saint petersburg",
            };

            var normalizedQuery = NormalizeSearchQuery(query);

            // Direct match with known city
            if (knownCities.Contains(normalizedQuery))
                return true;

            // Check if query contains a known city
            if (
                knownCities.Any(city =>
                    normalizedQuery.Contains(city) || city.Contains(normalizedQuery)
                )
            )
                return true;

            // Check for common city suffixes
            if (
                normalizedQuery.EndsWith(" city")
                || normalizedQuery.EndsWith(" island")
                || normalizedQuery.EndsWith(" town")
                || normalizedQuery.EndsWith(" province")
            )
                return true;

            return false;
        }

        private bool IsCountryName(string query)
        {
            // List of known countries (can be expanded)
            var knownCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "indonesia",
                "malaysia",
                "singapore",
                "philippines",
                "thailand",
                "vietnam",
                "china",
                "japan",
                "south korea",
                "india",
                "saudi arabia",
                "united arab emirates",
                "united states",
                "united kingdom",
                "france",
                "spain",
                "italy",
                "germany",
                "australia",
                "new zealand",
                "brazil",
            };

            var normalizedQuery = NormalizeSearchQuery(query);

            // Direct match with known country
            if (knownCountries.Contains(normalizedQuery))
                return true;

            // Check if query contains a known country
            if (
                knownCountries.Any(country =>
                    normalizedQuery.Contains(country) || country.Contains(normalizedQuery)
                )
            )
                return true;

            return false;
        }

        private bool IsKnownHotelBrand(string query)
        {
            var normalizedQuery = NormalizeSearchQuery(query);

            // List of known hotel brands
            var hotelBrands = new Dictionary<string, List<string>>
            {
                {
                    "marriott",
                    new List<string> { "jw marriott", "marriott hotel", "marriott resort" }
                },
                {
                    "ritz carlton",
                    new List<string> { "ritz-carlton", "the ritz carlton" }
                },
                {
                    "hilton",
                    new List<string> { "hilton hotel", "hilton garden", "hilton resort" }
                },
                {
                    "hyatt",
                    new List<string> { "grand hyatt", "park hyatt", "hyatt regency" }
                },
                {
                    "sheraton",
                    new List<string> { "sheraton hotel", "sheraton resort" }
                },
                {
                    "westin",
                    new List<string> { "the westin", "westin hotel", "westin resort" }
                },
                {
                    "intercontinental",
                    new List<string> { "intercontinental hotel", "ic hotel" }
                },
                {
                    "movenpick",
                    new List<string> { "movenpick hotel", "movenpick resort" }
                },
                {
                    "grandhika",
                    new List<string> { "grandhika iskandarsyah" }
                },
            };

            // Check if query contains a hotel brand
            foreach (var brand in hotelBrands)
            {
                if (normalizedQuery.Contains(brand.Key))
                    return true;

                if (brand.Value.Any(variation => normalizedQuery.Contains(variation)))
                    return true;
            }

            return false;
        }

        private bool IsExactHotelName(string query)
        {
            // This would check for exact hotel name matches
            // For demonstration, check a few specific hotel names
            var exactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "grandhika iskandarsyah jakarta",
                "jw marriott surabaya",
                "the ritz carlton jakarta",
            };

            return exactNames.Contains(NormalizeSearchQuery(query));
        }

        private string NormalizeSearchQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return string.Empty;

            // Convert to lowercase
            string result = query.ToLowerInvariant();

            // Remove diacritics (accents)
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
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");

            // Trim leading and trailing spaces
            result = result.Trim();

            // Remove common punctuation
            result = System.Text.RegularExpressions.Regex.Replace(result, @"[^\w\s]", " ");

            // Normalize spaces again
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();

            return result;
        }

        private string CorrectCommonTypos(string query)
        {
            var normalizedQuery = NormalizeSearchQuery(query);

            // Dictionary mapping common typos to corrections
            var commonTypos = new Dictionary<string, string>
            {
                { "jakrata", "jakarta" },
                { "jakata", "jakarta" },
                { "surrabaya", "surabaya" },
                { "surabay", "surabaya" },
                { "bandong", "bandung" },
                { "singapur", "singapore" },
                { "singapura", "singapore" },
                { "manilas", "manila" },
                { "mariot", "marriott" },
                { "marriot", "marriott" },
                { "ritzcarlton", "ritz carlton" },
                { "ritz-carlton", "ritz carlton" },
                { "hiltn", "hilton" },
                { "movenpik", "movenpick" },
                { "radison", "radisson" },
            };

            foreach (var typo in commonTypos)
            {
                if (normalizedQuery.Contains(typo.Key))
                {
                    normalizedQuery = normalizedQuery.Replace(typo.Key, typo.Value);
                }
            }

            return normalizedQuery;
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
                                    a.Analyzers(an =>
                                        an.Standard("standard", sa => sa.StopWords("_english_"))
                                    )
                                )
                                .Setting("index.max_ngram_diff", 4) // Allow larger n-gram difference
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
            // Cek apakah index sudah ada dan hapus jika ada
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
                                    a.TokenFilters(tf =>
                                            tf.NGram(
                                                    "ngram_filter",
                                                    ng =>
                                                        ng.MinGram(1) // Smaller min gram for better partial matching
                                                            .MaxGram(4) // Increased max gram for longer terms
                                                )
                                                .EdgeNGram(
                                                    "edge_ngram_filter",
                                                    eng =>
                                                        eng.MinGram(1)
                                                            .MaxGram(20) // Support longer terms
                                                            .Side(EdgeNGramSide.Front) // Focus on front of words
                                                )
                                        )
                                        .Analyzers(an =>
                                            an.Custom(
                                                    "ngram_analyzer",
                                                    ca =>
                                                        ca.Tokenizer("standard")
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
                                                            .Filters("lowercase", "asciifolding")
                                                )
                                        )
                                )
                                .Setting("index.max_ngram_diff", 20) // Allow larger n-gram differences
                        )
                        .Map<Hotel>(m =>
                            m.Properties(p =>
                                p.Keyword(k => k.Name(n => n.Id))
                                    .Text(t =>
                                        t // Changed from Keyword to Text for better matching
                                        .Name(n => n.HotelCode)
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
