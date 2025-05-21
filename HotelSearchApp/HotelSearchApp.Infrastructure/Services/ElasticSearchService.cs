using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        public async Task<bool> DeleteHotelIndexAsync()
        {
            var response = await _elasticClient.Indices.DeleteAsync(HotelIndexName);
            return response.IsValid;
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

        // Helper method to normalize search query
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

        // Helper method to check if query is likely a hotel code
        private bool IsHotelCode(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return false;

            // Numeric-only hotel code (seperti 10000121)
            if (query.All(char.IsDigit) && query.Length >= 7 && query.Length <= 10)
                return true;

            // Alphanumeric hotel code (seperti AL10000267)
            if (
                query.Length >= 8
                && query.Length <= 12
                && query.Any(char.IsLetter)
                && query.Any(char.IsDigit)
            )
                return true;

            return false;
        }

        // Helper method to check if query is likely a city name
        private bool IsCityName(string query)
        {
            // Daftar kota yang dikenal (bisa diperluas)
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

            // Cek direct match dengan kota yang dikenal
            if (
                knownCities.Any(city =>
                    city.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                )
            )
                return true;

            // Cek partial match dengan kota yang dikenal
            if (
                knownCities.Any(city =>
                    city.Contains(normalizedQuery) || normalizedQuery.Contains(city)
                )
            )
                return true;

            // Mendeteksi suffix umum untuk nama kota
            if (
                normalizedQuery.EndsWith(" city")
                || normalizedQuery.EndsWith(" island")
                || normalizedQuery.EndsWith(" town")
                || normalizedQuery.EndsWith(" province")
            )
                return true;

            return false;
        }

        // Helper method to check if query is likely a country name
        private bool IsCountryName(string query)
        {
            // Daftar negara yang dikenal (bisa diperluas)
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

            // Cek direct match dengan negara yang dikenal
            if (
                knownCountries.Any(country =>
                    country.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                )
            )
                return true;

            // Cek partial match dengan negara yang dikenal
            if (
                knownCountries.Any(country =>
                    country.Contains(normalizedQuery) || normalizedQuery.Contains(country)
                )
            )
                return true;

            return false;
        }

        // Helper method to detect if query is a known hotel brand
        private bool IsKnownHotelBrand(string query)
        {
            var normalizedQuery = NormalizeSearchQuery(query);

            // Daftar brand hotel terkenal
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

            // Cek apakah query mengandung brand hotel
            foreach (var brand in hotelBrands)
            {
                if (normalizedQuery.Contains(brand.Key))
                    return true;

                if (brand.Value.Any(variation => normalizedQuery.Contains(variation)))
                    return true;
            }

            return false;
        }

        // Helper method to correct common typos
        private string CorrectCommonTypos(string query)
        {
            var normalizedQuery = NormalizeSearchQuery(query);

            // Dictionary mapping typo umum ke koreksinya
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

        // Process search results based on criteria
        private List<Hotel> ProcessSearchResults(
            List<Hotel> results,
            string query,
            bool isHotelCode,
            bool isCityName,
            bool isCountryName,
            bool isKnownHotelBrand
        )
        {
            if (results == null || !results.Any())
                return new List<Hotel>();

            var normalizedQuery = NormalizeSearchQuery(query);
            var correctedQuery = CorrectCommonTypos(normalizedQuery);

            // Case 1: Hotel Code (prioritas tertinggi)
            if (isHotelCode)
            {
                var codeMatches = results
                    .Where(h =>
                        NormalizeSearchQuery(h.HotelCode).Contains(normalizedQuery)
                        || normalizedQuery.Contains(NormalizeSearchQuery(h.HotelCode))
                    )
                    .Take(10)
                    .ToList();

                if (codeMatches.Any())
                    return codeMatches;
            }

            // Case 2: Exact Hotel Name
            var exactNameMatches = results
                .Where(h =>
                    NormalizeSearchQuery(h.HotelName)
                        .Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || NormalizeSearchQuery(h.HotelName)
                        .Equals(correctedQuery, StringComparison.OrdinalIgnoreCase)
                )
                .Take(10)
                .ToList();

            if (exactNameMatches.Any())
                return exactNameMatches;

            // Case 3: Known Hotel Brand
            if (isKnownHotelBrand)
            {
                var brandMatches = results
                    .Where(h =>
                        NormalizeSearchQuery(h.HotelName).Contains(normalizedQuery)
                        || normalizedQuery.Contains(NormalizeSearchQuery(h.HotelName))
                    )
                    .Take(10)
                    .ToList();

                if (brandMatches.Any())
                    return brandMatches;
            }

            // Case 4: City Name
            if (isCityName)
            {
                // Group hasil berdasarkan kota
                var cityGroups = results
                    .GroupBy(h => h.CityName)
                    .Where(g =>
                        !string.IsNullOrEmpty(g.Key)
                        && (
                            NormalizeSearchQuery(g.Key).Contains(normalizedQuery)
                            || NormalizeSearchQuery(g.Key).Contains(correctedQuery)
                        )
                    )
                    .Take(5) // Max 5 cities
                    .ToList();

                if (cityGroups.Any())
                {
                    // Untuk setiap kota, ambil top hotels secara proporsional
                    var cityResults = new List<Hotel>();
                    int hotelsPerCity = Math.Max(1, 10 / cityGroups.Count);

                    foreach (var city in cityGroups)
                    {
                        cityResults.AddRange(city.Take(hotelsPerCity));
                    }

                    // Isi slot yang tersisa jika diperlukan
                    if (cityResults.Count < 10)
                    {
                        foreach (var city in cityGroups)
                        {
                            var remaining = 10 - cityResults.Count;
                            cityResults.AddRange(city.Skip(hotelsPerCity).Take(remaining));

                            if (cityResults.Count >= 10)
                                break;
                        }
                    }

                    return cityResults.Take(10).ToList();
                }
            }

            // Case 5: Country Name
            if (isCountryName)
            {
                // Group hasil berdasarkan negara
                var countryGroups = results
                    .GroupBy(h => h.Country)
                    .Where(g =>
                        !string.IsNullOrEmpty(g.Key)
                        && (
                            NormalizeSearchQuery(g.Key).Contains(normalizedQuery)
                            || NormalizeSearchQuery(g.Key).Contains(correctedQuery)
                        )
                    )
                    .Take(5) // Max 5 countries
                    .ToList();

                if (countryGroups.Any())
                {
                    // Untuk setiap negara, ambil top hotels secara proporsional
                    var countryResults = new List<Hotel>();
                    int hotelsPerCountry = Math.Max(1, 10 / countryGroups.Count);

                    foreach (var country in countryGroups)
                    {
                        countryResults.AddRange(country.Take(hotelsPerCountry));
                    }

                    // Isi slot yang tersisa jika diperlukan
                    if (countryResults.Count < 10)
                    {
                        foreach (var country in countryGroups)
                        {
                            var remaining = 10 - countryResults.Count;
                            countryResults.AddRange(country.Skip(hotelsPerCountry).Take(remaining));

                            if (countryResults.Count >= 10)
                                break;
                        }
                    }

                    return countryResults.Take(10).ToList();
                }
            }

            // Case 6: Partial Hotel Name (default case)
            var partialNameMatches = results
                .Where(h =>
                    NormalizeSearchQuery(h.HotelName).Contains(normalizedQuery)
                    || NormalizeSearchQuery(h.HotelName).Contains(correctedQuery)
                    || normalizedQuery.Contains(NormalizeSearchQuery(h.HotelName))
                    || correctedQuery.Contains(NormalizeSearchQuery(h.HotelName))
                )
                .Take(10)
                .ToList();

            if (partialNameMatches.Any())
                return partialNameMatches;

            // Case 7: Default fallback - return top 10 results
            return results.Take(10).ToList();
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

            // Langkah 1: Normalisasi dan koreksi query
            var normalizedQuery = NormalizeSearchQuery(searchQuery);
            var correctedQuery = CorrectCommonTypos(normalizedQuery);

            // Langkah 2: Deteksi tipe pencarian
            bool isHotelCode = IsHotelCode(normalizedQuery);
            bool isCityName = IsCityName(normalizedQuery);
            bool isCountryName = IsCountryName(normalizedQuery);
            bool isKnownHotelBrand = IsKnownHotelBrand(normalizedQuery);

            // Request lebih banyak hasil untuk memungkinkan filtering
            var requestPageSize = pageSize * 5;

            // Langkah 3: Buat query pencarian berdasarkan tipe
            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelNGramIndexName)
                .From(0) // Mulai dari 0 karena kita akan melakukan paginasi kustom
                .Size(requestPageSize)
                .RequestCache(false)
                .TrackScores(true);

            // Hotel Code search (prioritas tertinggi)
            if (isHotelCode)
            {
                searchDescriptor = searchDescriptor.Query(q =>
                    q.Bool(b =>
                        b.Should(
                                // Exact match pada hotel code
                                s =>
                                    s.Term(t =>
                                        t.Field(f => f.HotelCode).Value(normalizedQuery).Boost(50.0)
                                    ),
                                // Fuzzy match pada hotel code
                                s =>
                                    s.Fuzzy(f =>
                                        f.Field(p => p.HotelCode)
                                            .Value(normalizedQuery)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(30.0)
                                    ),
                                // Prefix match pada hotel code
                                s =>
                                    s.Prefix(p =>
                                        p.Field(f => f.HotelCode).Value(normalizedQuery).Boost(20.0)
                                    )
                            )
                            .MinimumShouldMatch(1)
                    )
                );
            }
            // Hotel Brand search
            else if (isKnownHotelBrand)
            {
                searchDescriptor = searchDescriptor.Query(q =>
                    q.Bool(b =>
                        b.Should(
                                // Exact match pada hotel name
                                s =>
                                    s.Term(t =>
                                        t.Field("hotelname.keyword")
                                            .Value(normalizedQuery)
                                            .Boost(50.0)
                                    ),
                                // Match phrase pada hotel name
                                s =>
                                    s.MatchPhrase(mp =>
                                        mp.Field(f => f.HotelName)
                                            .Query(normalizedQuery)
                                            .Boost(40.0)
                                    ),
                                // Fuzzy match pada hotel name
                                s =>
                                    s.Match(m =>
                                        m.Field(f => f.HotelName)
                                            .Query(normalizedQuery)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(30.0)
                                    ),
                                // Edge n-gram match untuk prefiks
                                s =>
                                    s.Match(m =>
                                        m.Field("hotelname.edge").Query(normalizedQuery).Boost(20.0)
                                    )
                            )
                            .MinimumShouldMatch(1)
                    )
                );
            }
            // City search
            else if (isCityName)
            {
                searchDescriptor = searchDescriptor.Query(q =>
                    q.Bool(b =>
                        b.Should(
                                // Exact match pada city name
                                s =>
                                    s.Term(t =>
                                        t.Field("cityname.keyword")
                                            .Value(normalizedQuery)
                                            .Boost(50.0)
                                    ),
                                s =>
                                    s.Term(t =>
                                        t.Field("cityname.keyword")
                                            .Value(correctedQuery)
                                            .Boost(50.0)
                                    ),
                                // Fuzzy match pada city name
                                s =>
                                    s.Match(m =>
                                        m.Field(f => f.CityName)
                                            .Query(normalizedQuery)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(30.0)
                                    ),
                                s =>
                                    s.Match(m =>
                                        m.Field(f => f.CityName)
                                            .Query(correctedQuery)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(30.0)
                                    ),
                                // Prefix match pada city name
                                s =>
                                    s.Prefix(p =>
                                        p.Field(f => f.CityName).Value(normalizedQuery).Boost(20.0)
                                    ),
                                s =>
                                    s.Prefix(p =>
                                        p.Field(f => f.CityName).Value(correctedQuery).Boost(20.0)
                                    )
                            )
                            .MinimumShouldMatch(1)
                    )
                );
            }
            // Country search
            else if (isCountryName)
            {
                searchDescriptor = searchDescriptor.Query(q =>
                    q.Bool(b =>
                        b.Should(
                                // Exact match pada country name
                                s =>
                                    s.Term(t =>
                                        t.Field("country.keyword")
                                            .Value(normalizedQuery)
                                            .Boost(50.0)
                                    ),
                                s =>
                                    s.Term(t =>
                                        t.Field("country.keyword").Value(correctedQuery).Boost(50.0)
                                    ),
                                // Fuzzy match pada country name
                                s =>
                                    s.Match(m =>
                                        m.Field(f => f.Country)
                                            .Query(normalizedQuery)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(30.0)
                                    ),
                                s =>
                                    s.Match(m =>
                                        m.Field(f => f.Country)
                                            .Query(correctedQuery)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(30.0)
                                    )
                            )
                            .MinimumShouldMatch(1)
                    )
                );
            }
            // General search (default case)
            else
            {
                searchDescriptor = searchDescriptor.Query(q =>
                    q.Bool(b =>
                        b.Should(
                                // Hotel name matches (highest priority)
                                s =>
                                    s.Term(t =>
                                        t.Field("hotelname.keyword")
                                            .Value(normalizedQuery)
                                            .Boost(50.0)
                                    ),
                                s =>
                                    s.Term(t =>
                                        t.Field("hotelname.keyword")
                                            .Value(correctedQuery)
                                            .Boost(50.0)
                                    ),
                                s =>
                                    s.MatchPhrase(mp =>
                                        mp.Field(f => f.HotelName)
                                            .Query(normalizedQuery)
                                            .Boost(40.0)
                                    ),
                                s =>
                                    s.MatchPhrase(mp =>
                                        mp.Field(f => f.HotelName).Query(correctedQuery).Boost(40.0)
                                    ),
                                s =>
                                    s.Match(m =>
                                        m.Field(f => f.HotelName)
                                            .Query(normalizedQuery)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(30.0)
                                    ),
                                s =>
                                    s.Match(m =>
                                        m.Field(f => f.HotelName)
                                            .Query(correctedQuery)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(30.0)
                                    ),
                                // City name matches (medium priority)
                                s =>
                                    s.Term(t =>
                                        t.Field("cityname.keyword")
                                            .Value(normalizedQuery)
                                            .Boost(20.0)
                                    ),
                                s =>
                                    s.Match(m =>
                                        m.Field(f => f.CityName)
                                            .Query(normalizedQuery)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(15.0)
                                    ),
                                // Country name matches (lower priority)
                                s =>
                                    s.Term(t =>
                                        t.Field("country.keyword")
                                            .Value(normalizedQuery)
                                            .Boost(10.0)
                                    ),
                                s =>
                                    s.Match(m =>
                                        m.Field(f => f.Country)
                                            .Query(normalizedQuery)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(5.0)
                                    )
                            )
                            .MinimumShouldMatch(1)
                    )
                );
            }

            // Langkah 4: Jalankan pencarian
            var searchResponse = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

            stopwatch.Stop();

            // Langkah 5: Proses hasil berdasarkan tipe pencarian
            var processedResults = ProcessSearchResults(
                searchResponse.Documents.ToList(),
                searchQuery,
                isHotelCode,
                isCityName,
                isCountryName,
                isKnownHotelBrand
            );

            // Langkah 6: Terapkan paginasi
            int total = processedResults.Count;
            var paginatedResults = processedResults
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return new ElasticSearchResponse<Hotel>
            {
                Items = paginatedResults,
                TotalHits = total,
                ElapsedTime = stopwatch.Elapsed,
                PageNumber = pageNumber,
                PageSize = pageSize,
            };
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
