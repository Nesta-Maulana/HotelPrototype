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

            var searchDescriptor = new SearchDescriptor<Hotel>()
                .Index(HotelNGramIndexName)
                .From((pageNumber - 1) * pageSize)
                .Size(pageSize)
                .RequestCache(false)
                .TrackScores(true);

            // Enhanced unified search approach
            searchDescriptor = searchDescriptor.Query(q =>
                q.Bool(b =>
                    b.Should(
                            // Multi-match search across all relevant fields with n-gram
                            s =>
                                s.MultiMatch(mm =>
                                    mm.Query(searchQuery)
                                        .Fields(f =>
                                            f.Field(p => p.HotelName, 2.0)
                                                .Field(p => p.HotelCode, 2.5)
                                                .Field(p => p.CityName, 2.0)
                                                .Field(p => p.Country, 1.0)
                                                .Field(p => p.Address1, 1.0)
                                        )
                                        .Type(TextQueryType.BestFields)
                                        .MinimumShouldMatch("70%") // Increased from 60%
                                        .Boost(1.5) // Increased from 1.0
                                ),
                            // Edge n-gram for better prefix matching
                            s =>
                                s.MultiMatch(mm =>
                                    mm.Query(searchQuery)
                                        .Fields(f =>
                                            f.Field("hotelname.edge", 2.0)
                                                .Field("hotelcode.edge", 2.5)
                                                .Field("cityname.edge", 2.0)
                                                .Field("address1.edge", 1.0)
                                        )
                                        .Type(TextQueryType.BestFields)
                                        .MinimumShouldMatch("80%")
                                        .Boost(1.8)
                                ),
                            // Exact keyword matching
                            s =>
                                s.MultiMatch(mm =>
                                    mm.Query(searchQuery)
                                        .Fields(f =>
                                            f.Field("hotelname.keyword", 2.5)
                                                .Field("hotelcode.keyword", 3.0)
                                                .Field("cityname.keyword", 2.5)
                                                .Field("country.keyword", 1.5)
                                        )
                                        .Type(TextQueryType.BestFields)
                                        .Boost(2.5)
                                ),
                            // Fuzzy search for typo tolerance
                            s =>
                                s.MultiMatch(mm =>
                                    mm.Query(searchQuery)
                                        .Fields(f =>
                                            f.Field(p => p.HotelName, 3.0)
                                                .Field(p => p.HotelCode, 3.5)
                                                .Field(p => p.CityName, 2.5)
                                                .Field(p => p.Country, 1.5)
                                                .Field(p => p.Address1, 1.0)
                                        )
                                        .Type(TextQueryType.BestFields)
                                        .Fuzziness(Fuzziness.Auto)
                                        .PrefixLength(1)
                                        .MaxExpansions(50)
                                        .Boost(2.0) // Increased from 1.5
                                ),
                            // Phrase matching for better exact matching
                            s =>
                                s.MultiMatch(mm =>
                                    mm.Query(searchQuery)
                                        .Fields(f =>
                                            f.Field(p => p.HotelName, 3.5)
                                                .Field(p => p.CityName, 3.0)
                                                .Field(p => p.Address1, 2.0)
                                        )
                                        .Type(TextQueryType.PhrasePrefix)
                                        .Boost(2.5)
                                )
                        )
                        .MinimumShouldMatch(1)
                )
            );

            var searchResponse = await _elasticClient.SearchAsync<Hotel>(searchDescriptor);

            stopwatch.Stop();

            // Enhanced adaptive result handling
            if (searchResponse.Hits.Count > 1)
            {
                var topScore = searchResponse.Hits.First().Score ?? 0;
                var secondScore = searchResponse.Hits.Skip(1).First().Score ?? 0;

                // Dynamic threshold for single result: if score difference is very high
                // (higher than original 1.5 threshold), return only the top result
                if (topScore > 0 && secondScore > 0)
                {
                    double ratio = topScore / secondScore;

                    // If using a very specific search term that strongly matches one result
                    if (ratio > 2.0)
                    {
                        return new ElasticSearchResponse<Hotel>
                        {
                            Items = new List<Hotel> { searchResponse.Documents.First() },
                            TotalHits = 1,
                            ElapsedTime = stopwatch.Elapsed,
                            PageNumber = pageNumber,
                            PageSize = pageSize,
                        };
                    }
                }
            }

            return new ElasticSearchResponse<Hotel>
            {
                Items = searchResponse.Documents,
                TotalHits = searchResponse.Total,
                ElapsedTime = stopwatch.Elapsed,
                PageNumber = pageNumber,
                PageSize = pageSize,
            };
        }
    }
}
