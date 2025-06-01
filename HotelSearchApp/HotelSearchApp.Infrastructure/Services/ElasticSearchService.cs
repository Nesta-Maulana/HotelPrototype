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

        // Metode untuk mendeteksi apakah query adalah kode hotel
        private bool IsHotelCodeQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return false;

            // Hotel code biasanya mengikuti format: 2 huruf diikuti angka (misalnya SG10000142, AL10000267)
            return System.Text.RegularExpressions.Regex.IsMatch(query, @"^[A-Z]{2}\d+$");
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

            // Jika kode hotel diisi dan terlihat seperti kode hotel
            if (
                !string.IsNullOrWhiteSpace(searchParams.HotelCode)
                && IsHotelCodeQuery(searchParams.HotelCode)
            )
            {
                // Gunakan pencarian yang mengutamakan kecocokan persis
                shouldClauses.Add(
                    new TermQuery
                    {
                        Field = "hotelcode.keyword",
                        Value = searchParams.HotelCode,
                        Boost = 1000.0, // Prioritas sangat tinggi
                    }
                );

                // Sebagai backup, gunakan fuzzy/prefix tapi dengan boost jauh lebih rendah
                shouldClauses.Add(
                    new MatchQuery
                    {
                        Field = "hotelcode",
                        Query = searchParams.HotelCode,
                        Fuzziness = Fuzziness.Auto,
                        PrefixLength = 1,
                        MaxExpansions = 50,
                        Boost = 5.0,
                    }
                );
            }
            // Jika tidak terlihat seperti kode hotel, gunakan fuzzy search normal
            else if (!string.IsNullOrWhiteSpace(searchParams.HotelCode))
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

            // Untuk pencarian kode hotel, kita perlu filter tambahan untuk kecocokan persis
            if (
                !string.IsNullOrWhiteSpace(searchParams.HotelCode)
                && IsHotelCodeQuery(searchParams.HotelCode)
            )
            {
                // Cari kecocokan persis dari hasil
                var exactMatch = searchResponse.Documents.FirstOrDefault(h =>
                    h.HotelCode != null
                    && h.HotelCode.Equals(
                        searchParams.HotelCode,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

                // Jika ada kecocokan persis, hanya kembalikan hotel tersebut
                if (exactMatch != null)
                {
                    return new ElasticSearchResponse<Hotel>
                    {
                        Items = new List<Hotel> { exactMatch },
                        TotalHits = 1,
                        ElapsedTime = stopwatch.Elapsed,
                        PageNumber = searchParams.PageNumber,
                        PageSize = searchParams.PageSize,
                    };
                }
            }

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

            // Jika kode hotel diisi dan terlihat seperti kode hotel
            if (
                !string.IsNullOrWhiteSpace(searchParams.HotelCode)
                && IsHotelCodeQuery(searchParams.HotelCode)
            )
            {
                // Gunakan pencarian yang mengutamakan kecocokan persis
                shouldClauses.Add(
                    new TermQuery
                    {
                        Field = "hotelcode.keyword",
                        Value = searchParams.HotelCode,
                        Boost = 1000.0, // Prioritas sangat tinggi
                    }
                );

                // Sebagai backup, gunakan n-gram tapi dengan boost jauh lebih rendah
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
            // Jika tidak terlihat seperti kode hotel, gunakan n-gram search normal
            else if (!string.IsNullOrWhiteSpace(searchParams.HotelCode))
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

            // Untuk pencarian kode hotel, kita perlu filter tambahan untuk kecocokan persis
            if (
                !string.IsNullOrWhiteSpace(searchParams.HotelCode)
                && IsHotelCodeQuery(searchParams.HotelCode)
            )
            {
                // Cari kecocokan persis dari hasil
                var exactMatch = searchResponse.Documents.FirstOrDefault(h =>
                    h.HotelCode != null
                    && h.HotelCode.Equals(
                        searchParams.HotelCode,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

                // Jika ada kecocokan persis, hanya kembalikan hotel tersebut
                if (exactMatch != null)
                {
                    return new ElasticSearchResponse<Hotel>
                    {
                        Items = new List<Hotel> { exactMatch },
                        TotalHits = 1,
                        ElapsedTime = stopwatch.Elapsed,
                        PageNumber = searchParams.PageNumber,
                        PageSize = searchParams.PageSize,
                    };
                }
            }

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
            if (IsHotelCodeSearch(searchQuery))
            {
                var codeResults = await SearchHotelsByCode(searchQuery, pageSize);

                stopwatch.Stop();

                return new ElasticSearchResponse<Hotel>
                {
                    Items = codeResults,
                    TotalHits = codeResults.Count,
                    ElapsedTime = stopwatch.Elapsed,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    IsHotelCodeSearch = true, // Add this property to ElasticSearchResponse class
                };
            }

            if (IsCountrySearch(searchQuery))
            {
                var (topCities, topHotels) = await GetCountrySearchResults(searchQuery, pageSize);

                stopwatch.Stop();

                var response = new ElasticSearchResponse<Hotel>
                {
                    Items = topHotels,
                    TotalHits = topHotels.Count,
                    ElapsedTime = stopwatch.Elapsed,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TopCities = topCities, // Add this property to ElasticSearchResponse class
                    IsCountrySearch = true, // Add this property to ElasticSearchResponse class
                };

                return response;
            }

            // Cek jika query adalah kode hotel lengkap (dengan awalan huruf)
            bool isHotelCode = IsHotelCodeQuery(searchQuery);

            // Cek jika query adalah kode numerik saja (tanpa awalan huruf)
            bool isNumericCode = IsNumericCodeQuery(searchQuery);

            // Prioritaskan pencarian kode hotel lengkap
            if (isHotelCode)
            {
                // [Kode yang sudah ada untuk pencarian kode hotel]
                var hotelParams = new HotelSearchParameters
                {
                    HotelCode = searchQuery,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                };

                // Coba pencarian pada indeks normal dulu
                var exactResult = await SearchHotelsAsync(hotelParams);

                // Jika ada kecocokan persis, kembalikan
                if (
                    exactResult.Items.Any()
                    && exactResult.Items.Any(h =>
                        h.HotelCode != null
                        && h.HotelCode.Equals(searchQuery, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    stopwatch.Stop();
                    return exactResult;
                }

                // Jika tidak ada kecocokan di indeks normal, coba di indeks n-gram
                var ngramResult = await SearchHotelsNGramAsync(hotelParams);

                // Jika ada kecocokan persis di n-gram, kembalikan
                if (
                    ngramResult.Items.Any()
                    && ngramResult.Items.Any(h =>
                        h.HotelCode != null
                        && h.HotelCode.Equals(searchQuery, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    stopwatch.Stop();
                    return ngramResult;
                }
            }
            // Jika query adalah kode numerik, cari hotel dengan bagian numerik yang cocok
            else if (isNumericCode)
            {
                var results = await SearchHotelsByNumericCode(searchQuery, pageSize);

                stopwatch.Stop();

                return new ElasticSearchResponse<Hotel>
                {
                    Items = results,
                    TotalHits = results.Count(),
                    ElapsedTime = stopwatch.Elapsed,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                };
            }

            // [Kode yang sudah ada untuk pencarian normal]
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

        // PERBAIKAN: DetectSearchIntent yang lebih cerdas
        private (SearchIntent intent, bool isExact) DetectSearchIntent(string normalizedQuery)
        {
            int wordCount = normalizedQuery
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Length;
            bool containsSpecificPhrase = ContainsSpecificPhrases(normalizedQuery);
            bool looksLikeHotelCode = IsLikelyHotelCode(normalizedQuery);

            // Tambahkan deteksi untuk nama hotel spesifik
            bool looksLikeSpecificHotel = IsLikelySpecificHotelName(normalizedQuery);

            if (looksLikeHotelCode)
            {
                return (SearchIntent.HotelCode, true);
            }
            // PERBAIKAN: Prioritaskan specific hotel detection
            else if (looksLikeSpecificHotel || (wordCount >= 2 && containsSpecificPhrase))
            {
                return (SearchIntent.SpecificHotel, true);
            }
            else if (wordCount >= 3 && containsSpecificPhrase)
            {
                return (SearchIntent.SpecificHotel, true);
            }
            else if (wordCount <= 2 && !looksLikeSpecificHotel)
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

        // Method baru untuk mendeteksi nama hotel spesifik
        private bool IsLikelySpecificHotelName(string query)
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Hotel name indicators - kata-kata yang menunjukkan nama hotel spesifik
            var hotelNameIndicators = new[]
            {
                "hotel",
                "resort",
                "residence",
                "residences",
                "palace",
                "grand",
                "royal",
                "plaza",
                "suites",
                "inn",
                "lodge",
                "villa",
                "manor",
                "tower",
                "towers",
                "court",
                "garden",
                "gardens",
                "spa",
                "boutique",
                "luxury",
                "premium",
                "executive",
                "business",
                "international",
                "continental",
                "imperial",
                "heritage",
                "landmark",
                "signature",
                "collection",
                "by",
                "lorin",
                "marriott",
                "hilton",
                "hyatt",
                "sheraton",
                "westin",
                "intercontinental",
            };

            // Location indicators yang menunjukkan ini mungkin nama spesifik + lokasi
            var locationIndicators = new[]
            {
                "jakarta",
                "bandung",
                "surabaya",
                "bali",
                "yogyakarta",
                "medan",
                "semarang",
                "makassar",
                "palembang",
                "batam",
                "malang",
                "solo",
                "bogor",
                "depok",
                "tangerang",
                "bekasi",
                "city",
                "centre",
                "center",
                "downtown",
                "airport",
                "beach",
                "mountain",
                "hill",
                "lake",
                "river",
            };

            // Jika query mengandung hotel name indicators
            bool hasHotelIndicator = hotelNameIndicators.Any(indicator =>
                query.Contains(indicator, StringComparison.OrdinalIgnoreCase)
            );

            // Jika ada 2+ kata dan salah satunya adalah hotel indicator
            if (terms.Length >= 2 && hasHotelIndicator)
            {
                return true;
            }

            // Jika ada kombinasi nama + hotel indicator (misal: "Mangkuluhur Residences")
            if (terms.Length == 2)
            {
                var firstWord = terms[0].ToLowerInvariant();
                var secondWord = terms[1].ToLowerInvariant();

                // Jika kata kedua adalah hotel indicator dan kata pertama bukan location
                if (
                    hotelNameIndicators.Contains(secondWord)
                    && !locationIndicators.Contains(firstWord)
                )
                {
                    return true;
                }
            }

            return false;
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
                        Boost = 1000.0, // Sangat tinggi untuk memastikan ini selalu di atas
                    },
                    // Sebagai backup jika persis tidak ditemukan, gunakan fuzzy dan prefix
                    // tapi dengan boost yang jauh lebih rendah
                    new FuzzyQuery
                    {
                        Field = "hotelcode",
                        Value = code,
                        Fuzziness = Fuzziness.Auto,
                        Boost = 5.0,
                    },
                    new PrefixQuery
                    {
                        Field = "hotelcode",
                        Value = code,
                        Boost = 3.0,
                    },
                },
                MinimumShouldMatch = 1,
            };
        }

        // PERBAIKAN: BuildSpecificHotelQuery untuk prioritas exact match
        private QueryContainer BuildSpecificHotelQuery(string query)
        {
            return new BoolQuery
            {
                Should = new List<QueryContainer>
                {
                    // PRIORITAS TERTINGGI: Exact phrase match
                    new MatchPhraseQuery
                    {
                        Field = "hotelname.keyword",
                        Query = query,
                        Boost = 100.0, // Boost sangat tinggi untuk exact match
                    },
                    // PRIORITAS KEDUA: Phrase match dengan analyzer standard
                    new MatchPhraseQuery
                    {
                        Field = "hotelname",
                        Query = query,
                        Boost = 80.0,
                    },
                    // PRIORITAS KETIGA: Multi-field exact term match
                    (QueryContainer)
                        new BoolQuery
                        {
                            Must = query
                                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                .Select(term =>
                                    (QueryContainer)
                                        new BoolQuery
                                        {
                                            Should = new List<QueryContainer>
                                            {
                                                new TermQuery
                                                {
                                                    Field = "hotelname",
                                                    Value = term,
                                                    Boost = 3.0,
                                                },
                                                new MatchQuery
                                                {
                                                    Field = "hotelname",
                                                    Query = term,
                                                    Boost = 2.0,
                                                },
                                            },
                                            MinimumShouldMatch = 1,
                                        }
                                )
                                .ToList(),
                            Boost = 60.0,
                        },
                    // PRIORITAS KEEMPAT: Fuzzy match untuk typo tolerance
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = query,
                        Fuzziness = Fuzziness.EditDistance(1), // Perbaikan: gunakan EditDistance(1)
                        PrefixLength = 2, // Preserve 2 karakter pertama
                        MinimumShouldMatch = "90%", // Lebih strict
                        Boost = 40.0,
                    },
                    // PRIORITAS TERENDAH: Fallback untuk partial match
                    new MatchQuery
                    {
                        Field = "hotelname",
                        Query = query,
                        MinimumShouldMatch = "80%",
                        Boost = 20.0,
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
                    return ProcessHotelCodeResults(searchResponse, query);

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

        private IEnumerable<Hotel> ProcessHotelCodeResults(
            ISearchResponse<Hotel> searchResponse,
            string code
        )
        {
            // Untuk hotel codes, kita hanya ingin kecocokan eksak
            if (searchResponse.Hits.Count > 0)
            {
                // Cari apakah ada hotel yang kodenya persis sama dengan pencarian
                var exactMatch = searchResponse.Documents.FirstOrDefault(h =>
                    h.HotelCode != null
                    && h.HotelCode.Equals(code, StringComparison.OrdinalIgnoreCase)
                );

                // Jika ada kecocokan eksak, hanya kembalikan hotel tersebut
                if (exactMatch != null)
                {
                    return new List<Hotel> { exactMatch };
                }

                // Jika kita memiliki hasil dengan skor sangat tinggi, mungkin ini kecocokan yang baik
                if (IsHighConfidenceMatch(searchResponse.Hits))
                {
                    return searchResponse.Documents.Take(1);
                }
            }

            return searchResponse.Documents;
        }

        // PERBAIKAN: ProcessSpecificHotelResults
        private IEnumerable<Hotel> ProcessSpecificHotelResults(
            ISearchResponse<Hotel> searchResponse,
            string query
        )
        {
            if (!searchResponse.IsValid || searchResponse.Documents.Count() == 0)
            {
                return Enumerable.Empty<Hotel>();
            }

            var results = searchResponse.Documents.ToList();
            var normalizedQuery = query.ToLowerInvariant().Trim();

            // Cari exact match terlebih dahulu
            var exactMatches = results
                .Where(h =>
                    h.HotelName != null
                    && h.HotelName.Equals(query, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();

            if (exactMatches.Any())
            {
                return exactMatches.Take(1); // Return hanya exact match
            }

            // Cari near-exact match (phrase match)
            var phraseMatches = results
                .Where(h =>
                    h.HotelName != null
                    && h.HotelName.Contains(query, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();

            if (phraseMatches.Any())
            {
                // Urutkan berdasarkan similarity dan ambil yang paling mirip
                var bestMatches = phraseMatches
                    .OrderByDescending(h =>
                        h.HotelName != null ? CalculateNameSimilarity(h.HotelName, query) : 0
                    )
                    .ThenByDescending(h =>
                        searchResponse.Hits.First(hit => hit.Source.Id == h.Id).Score ?? 0
                    )
                    .ToList();

                // Jika similarity > 0.8, kembalikan hanya top result
                var topMatch = bestMatches.First();
                var similarity =
                    topMatch.HotelName != null
                        ? CalculateNameSimilarity(topMatch.HotelName, query)
                        : 0;

                if (similarity > 0.8)
                {
                    return new List<Hotel> { topMatch };
                }
            }

            // Jika tidak ada exact/near-exact match, filter hasil berdasarkan relevance score
            if (searchResponse.Hits.Count > 1)
            {
                var topScore = searchResponse.Hits.First().Score ?? 0;
                var secondScore = searchResponse.Hits.Skip(1).First().Score ?? 0;

                // Jika top score jauh lebih tinggi, kembalikan hanya top result
                if (topScore > 0 && secondScore > 0 && (topScore / secondScore) > 2.0)
                {
                    return new List<Hotel> { results.First() };
                }
            }

            // Fallback: kembalikan max 3 hasil terbaik
            return results.Take(1);
        }

        // Method helper untuk menghitung similarity nama hotel
        private double CalculateNameSimilarity(string? hotelName, string query)
        {
            if (string.IsNullOrEmpty(hotelName) || string.IsNullOrEmpty(query))
                return 0;

            var name = hotelName.ToLowerInvariant();
            var q = query.ToLowerInvariant();

            // Exact match
            if (name == q)
                return 1.0;

            // Contains query
            if (name.Contains(q))
                return 0.9;

            // Split into words and check overlap
            var nameWords = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var queryWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

            var intersection = nameWords.Intersect(queryWords).Count();
            var union = nameWords.Union(queryWords).Count();

            return union > 0 ? (double)intersection / union : 0;
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

        private bool IsNumericCodeQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return false;

            // Cek apakah query hanya berisi angka
            return System.Text.RegularExpressions.Regex.IsMatch(query, @"^\d+$");
        }

        private async Task<IEnumerable<Hotel>> SearchHotelsByNumericCode(
            string numericCode,
            int maxResults = 10
        )
        {
            // Buat query untuk mencari hotel dengan kode yang mengandung bagian numerik yang dicari
            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelIndexName) // Cari di indeks utama dulu
                .Size(maxResults)
                .Query(q =>
                    // Cari hotel yang kodenya mengandung angka yang dicari
                    q.Wildcard(w => w.Field(f => f.HotelCode).Value($"*{numericCode}*"))
                );

            var searchResponse = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

            if (!searchResponse.IsValid || !searchResponse.Documents.Any())
            {
                // Jika tidak ada hasil di indeks utama, coba cari di indeks ngram
                searchDescriptor = new SearchDescriptor<Hotel>()
                    .Index(HotelNGramIndexName)
                    .Size(maxResults)
                    .Query(q =>
                        q.Wildcard(w => w.Field(f => f.HotelCode).Value($"*{numericCode}*"))
                    );

                searchResponse = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);
            }

            return searchResponse.Documents.Take(maxResults);
        }

        private bool IsCountrySearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return false;

            // Common countries list - can be expanded
            var commonCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "andorra",
                "united arab emirates",
                "antigua and barbuda",
                "anguilla (uk)",
                "albania",
                "armenia",
                "netherlands antilles (netherlands)",
                "angola",
                "argentina",
                "american samoa",
                "austria",
                "australia",
                "aruba (netherlands)",
                "azerbaijan",
                "bosnia and herzegovina",
                "barbados",
                "bangladesh",
                "belgium",
                "burkina faso",
                "bulgaria",
                "bahrain",
                "burundi",
                "benin",
                "saint barthelemy",
                "bermuda",
                "brunei",
                "bolivia",
                "brazil",
                "bahamas",
                "bhutan",
                "botswana",
                "belize",
                "canada",
                "democratic republic of congo",
                "people s republic of congo",
                "switzerland",
                "cote d ivoire",
                "cook islands",
                "chile",
                "cameroon",
                "china",
                "colombia",
                "costa rica",
                "cuba",
                "cape verde",
                "curacao",
                "cyprus",
                "czech republic",
                "germany",
                "djibouti",
                "denmark",
                "dominican republic",
                "algeria",
                "ecuador",
                "estonia",
                "egypt",
                "eritrea",
                "spain",
                "ethiopia",
                "finland",
                "fiji",
                "micronesia",
                "faroe islands",
                "france",
                "gabon",
                "united kingdom",
                "grenada",
                "georgia",
                "french guiana",
                "ghana",
                "gibraltar",
                "greenland (denmark)",
                "gambia",
                "guinea",
                "guadeloupe (france)",
                "equatorial guinea",
                "greece",
                "guatemala",
                "guam",
                "guinea-bissau",
                "guyana",
                "hong kong",
                "honduras",
                "croatia",
                "haiti",
                "hungary",
                "indonesia",
                "ireland",
                "israel",
                "india",
                "iraq",
                "iran",
                "iceland",
                "italy",
                "jamaica",
                "jordan",
                "japan",
                "kenya",
                "kyrgyzstan",
                "cambodia",
                "comoros",
                "saint kitts and nevis",
                "south korea",
                "kuwait",
                "cayman islands (uk)",
                "kazakhstan",
                "laos",
                "lebanon",
                "saint lucia",
                "liechtenstein",
                "sri lanka",
                "liberia",
                "lesotho",
                "lithuania",
                "luxembourg",
                "latvia",
                "libya arab jamahiriya",
                "morocco",
                "monaco",
                "moldova",
                "montenegro",
                "madagascar",
                "macedonia",
                "mali",
                "myanmar",
                "mongolia",
                "macau",
                "northern mariana islands",
                "martinique (france)",
                "mauritania",
                "malta",
                "mauritius",
                "maldives",
                "malawi",
                "mexico",
                "malaysia",
                "mozambique",
                "namibia",
                "new caledonia",
                "niger",
                "nigeria",
                "nicaragua",
                "netherlands",
                "norway",
                "nepal",
                "niue",
                "new zealand",
                "oman",
                "panama",
                "peru",
                "french polynesia",
                "papua new guinea",
                "philippines",
                "pakistan",
                "poland",
                "puerto rico (usa)",
                "palestine",
                "portugal",
                "palau",
                "paraguay",
                "qatar",
                "reunion",
                "romania",
                "serbia",
                "rwanda",
                "saudi arabia",
                "solomon islands",
                "seychelles",
                "sudan",
                "sweden",
                "singapore",
                "slovenia",
                "slovakia",
                "sierra leone",
                "san marino",
                "senegal",
                "suriname",
                "sao tome and principe",
                "el salvador",
                "saint martin",
                "swaziland",
                "turks and caicos islands",
                "chad",
                "thailand",
                "tajikistan",
                "timor-leste",
                "tunisia",
                "tonga",
                "turkey",
                "trinidad and tobago",
                "taiwan",
                "tanzania",
                "uganda",
                "united states of america",
                "uruguay",
                "uzbekistan",
                "saint vincent and the grenadines",
                "venezuela",
                "british vergin islands",
                "u.s. virgin islands (usa)",
                "vietnam",
                "vanuatu",
                "samoa",
                "kosovo",
                "south africa",
                "zambia",
                "zimbabwe",
            };

            return commonCountries.Contains(query.Trim());
        }

        private async Task<(List<string> TopCities, List<Hotel> TopHotels)> GetCountrySearchResults(
            string countryName,
            int pageSize
        )
        {
            // Get all hotels in the country
            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelIndexName)
                .Size(100) // Get more results to extract top cities and hotels
                .Query(q =>
                    q.Match(m =>
                        m.Field(f => f.Country).Query(countryName).Fuzziness(Fuzziness.Auto)
                    )
                );

            var searchResponse = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

            if (!searchResponse.IsValid || !searchResponse.Documents.Any())
                return (new List<string>(), new List<Hotel>());

            var hotels = searchResponse.Documents.ToList();

            // Group by city and get top 5 cities - filter out null values
            var topCities = hotels
                .Where(h => !string.IsNullOrEmpty(h.CityName))
                .GroupBy(h => h.CityName)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key!) // Use ! to tell compiler this won't be null (we filtered nulls above)
                .ToList();

            // Get top 10 hotels based on relevance or any other criteria
            var topHotels = hotels.Take(10).ToList();

            return (topCities, topHotels);
        }

        private bool IsHotelCodeSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return false;

            // Check if the query starts with a numeric sequence (potential hotel code)
            // This regex matches a string that starts with at least 5 digits
            return System.Text.RegularExpressions.Regex.IsMatch(query, @"^\d{5,}");
        }

        private async Task<List<Hotel>> SearchHotelsByCode(string query, int maxResults = 10)
        {
            // Extract the numeric part from the beginning of the query
            string numericPart = new string(query.TakeWhile(char.IsDigit).ToArray());

            // Extract the text part after the numeric part (if any)
            string textPart = query.Substring(numericPart.Length).Trim();

            // Build a query that searches for hotels with the numeric code part
            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelIndexName)
                .Size(maxResults)
                .Query(q =>
                {
                    // Base query - search for the numeric part in the hotel code
                    QueryContainer codeQuery = q.Wildcard(w =>
                        w.Field(f => f.HotelCode).Value($"*{numericPart}*").Boost(10.0)
                    );

                    // If there's additional text, use it to refine the search
                    if (!string.IsNullOrWhiteSpace(textPart))
                    {
                        // Combine code search with text search
                        return codeQuery
                            && q.Bool(b =>
                                b.Should(
                                    // Match hotel name
                                    q.Match(m =>
                                        m.Field(f => f.HotelName)
                                            .Query(textPart)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(3.0)
                                    ),
                                    // Match city name
                                    q.Match(m =>
                                        m.Field(f => f.CityName)
                                            .Query(textPart)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(2.0)
                                    ),
                                    // Match country
                                    q.Match(m =>
                                        m.Field(f => f.Country)
                                            .Query(textPart)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(1.0)
                                    )
                                )
                            );
                    }

                    return codeQuery;
                });

            var searchResponse = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

            if (!searchResponse.IsValid || !searchResponse.Documents.Any())
            {
                // Try searching in the n-gram index if no results found
                searchDescriptor.Index(HotelNGramIndexName);
                searchResponse = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);
            }

            return searchResponse.Documents.ToList();
        }

        public async Task<IEnumerable<CitySuggestionDto>> GetCitySuggestionsAsync(
            string query,
            int maxSuggestions = 5
        )
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            {
                return new List<CitySuggestionDto>();
            }

            var normalizedQuery = query.ToLowerInvariant().Trim();

            try
            {
                // First try exact prefix matches
                var prefixMatches = await GetPrefixCityMatches(normalizedQuery, maxSuggestions);
                if (prefixMatches.Any())
                {
                    return prefixMatches;
                }

                // If no prefix matches, try fuzzy matching
                var fuzzyMatches = await GetFuzzyCityMatches(normalizedQuery, maxSuggestions);
                return fuzzyMatches;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting city suggestions: {ex.Message}");
                return new List<CitySuggestionDto>();
            }
        }

        private async Task<List<CitySuggestionDto>> GetPrefixCityMatches(
            string query,
            int maxSuggestions
        )
        {
            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelIndexName)
                .Size(0)
                .Query(q => q.Prefix(p => p.Field(f => f.CityName.Suffix("keyword")).Value(query)))
                .Aggregations(aggs =>
                    aggs.Terms(
                        "cities",
                        t => t.Field(f => f.CityName.Suffix("keyword")).Size(maxSuggestions * 2)
                    )
                );

            var response = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

            if (!response.IsValid || !response.Aggregations.ContainsKey("cities"))
            {
                return new List<CitySuggestionDto>();
            }

            var citiesAgg = response.Aggregations.Terms("cities");
            var results = new List<CitySuggestionDto>();

            foreach (var bucket in citiesAgg.Buckets)
            {
                if (
                    string.IsNullOrEmpty(bucket.Key)
                    || !bucket.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                results.Add(
                    new CitySuggestionDto
                    {
                        CityName = bucket.Key,
                        Country = "Indonesia", // Default for now
                        HotelCount = (int)(bucket.DocCount ?? 0),
                        Similarity = 1.0,
                    }
                );

                if (results.Count >= maxSuggestions)
                    break;
            }

            return results;
        }

        private async Task<List<CitySuggestionDto>> GetFuzzyCityMatches(
            string query,
            int maxSuggestions
        )
        {
            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelIndexName)
                .Size(0)
                .Query(q =>
                    q.Match(m =>
                        m.Field(f => f.CityName)
                            .Query(query)
                            .Fuzziness(Fuzziness.Auto)
                            .PrefixLength(1)
                    )
                )
                .Aggregations(aggs =>
                    aggs.Terms(
                        "cities",
                        t => t.Field(f => f.CityName.Suffix("keyword")).Size(maxSuggestions * 3)
                    )
                );

            var response = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

            if (!response.IsValid || !response.Aggregations.ContainsKey("cities"))
            {
                return new List<CitySuggestionDto>();
            }

            var citiesAgg = response.Aggregations.Terms("cities");
            var suggestions = new List<CitySuggestionDto>();

            foreach (var bucket in citiesAgg.Buckets)
            {
                if (string.IsNullOrEmpty(bucket.Key))
                    continue;

                var similarity = CalculateSimilarity(query, bucket.Key);
                if (similarity <= 0.3)
                    continue;

                suggestions.Add(
                    new CitySuggestionDto
                    {
                        CityName = bucket.Key,
                        Country = "Indonesia", // Default for now
                        HotelCount = (int)(bucket.DocCount ?? 0),
                        Similarity = similarity,
                    }
                );
            }

            return suggestions
                .OrderByDescending(x => x.Similarity)
                .ThenByDescending(x => x.HotelCount)
                .Take(maxSuggestions)
                .ToList();
        }

        private double CalculateSimilarity(string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
                return 0;

            source = source.ToLowerInvariant();
            target = target.ToLowerInvariant();

            // Exact match
            if (source == target)
                return 1.0;

            // Prefix match
            if (target.StartsWith(source))
                return 0.9;

            // Calculate Levenshtein distance
            int distance = LevenshteinDistance(source, target);
            int maxLength = Math.Max(source.Length, target.Length);

            if (maxLength == 0)
                return 1.0;

            double similarity = 1.0 - (double)distance / maxLength;

            // Boost similarity if target contains source as substring
            if (target.Contains(source))
                similarity = Math.Max(similarity, 0.8);

            return similarity;
        }

        private int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source))
                return string.IsNullOrEmpty(target) ? 0 : target.Length;

            if (string.IsNullOrEmpty(target))
                return source.Length;

            int sourceLength = source.Length;
            int targetLength = target.Length;
            int[,] matrix = new int[sourceLength + 1, targetLength + 1];

            // Initialize the matrix
            for (int i = 0; i <= sourceLength; i++)
                matrix[i, 0] = i;

            for (int j = 0; j <= targetLength; j++)
                matrix[0, j] = j;

            // Fill the matrix
            for (int i = 1; i <= sourceLength; i++)
            {
                for (int j = 1; j <= targetLength; j++)
                {
                    int cost = source[i - 1] == target[j - 1] ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost
                    );
                }
            }

            return matrix[sourceLength, targetLength];
        }

        public async Task<IEnumerable<Hotel>> GetHotelsByCityAsync(
            string cityName,
            int maxHotels = 10
        )
        {
            if (string.IsNullOrWhiteSpace(cityName))
            {
                return new List<Hotel>();
            }

            try
            {
                var searchDescriptor = new SearchDescriptor<Hotel>()
                    .Index(HotelIndexName)
                    .Size(maxHotels)
                    .Query(q =>
                        q.Bool(b =>
                            b.Should(
                                    // Exact match on city name (highest priority)
                                    q.Term(t =>
                                        t.Field(f => f.CityName.Suffix("keyword"))
                                            .Value(cityName)
                                            .Boost(10.0)
                                    ),
                                    // Fuzzy match for typo tolerance
                                    q.Match(m =>
                                        m.Field(f => f.CityName)
                                            .Query(cityName)
                                            .Fuzziness(Fuzziness.Auto)
                                            .Boost(5.0)
                                    )
                                )
                                .MinimumShouldMatch(1)
                        )
                    )
                    .Sort(s =>
                        s.Descending(SortSpecialField.Score) // Sort by relevance score first
                            .Ascending(f => f.HotelName.Suffix("keyword")) // Then by hotel name alphabetically
                    );

                var response = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

                if (!response.IsValid || !response.Documents.Any())
                {
                    return new List<Hotel>();
                }

                return response.Documents.ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting hotels by city: {ex.Message}");
                return new List<Hotel>();
            }
        }
    }
}
