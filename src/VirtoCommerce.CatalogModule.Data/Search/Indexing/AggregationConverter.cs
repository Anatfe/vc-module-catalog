using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VirtoCommerce.CatalogModule.Core;
using VirtoCommerce.CatalogModule.Core.Common;
using VirtoCommerce.CatalogModule.Core.Model.Search;
using VirtoCommerce.CatalogModule.Core.Search;
using VirtoCommerce.CatalogModule.Core.Services;
using VirtoCommerce.CatalogModule.Data.Search.BrowseFilters;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SearchModule.Core.Extenstions;
using VirtoCommerce.SearchModule.Core.Model;
using RangeFilter = VirtoCommerce.CatalogModule.Data.Search.BrowseFilters.RangeFilter;
using RangeFilterValue = VirtoCommerce.CatalogModule.Data.Search.BrowseFilters.RangeFilterValue;

namespace VirtoCommerce.CatalogModule.Data.Search.Indexing
{
    public class AggregationConverter : IAggregationConverter
    {
        private readonly IBrowseFilterService _browseFilterService;
        private readonly IPropertyService _propertyService;
        private readonly IPropertyDictionaryItemSearchService _propDictItemsSearchService;

        public AggregationConverter(IBrowseFilterService browseFilterService, IPropertyService propertyService, IPropertyDictionaryItemSearchService propDictItemsSearchService)
        {
            _browseFilterService = browseFilterService;
            _propertyService = propertyService;
            _propDictItemsSearchService = propDictItemsSearchService;
        }

        #region Request converter

        public async Task<IList<AggregationRequest>> GetAggregationRequestsAsync(ProductIndexedSearchCriteria criteria, FiltersContainer allFilters)
        {
            var result = new List<AggregationRequest>();

            var browseFilters = await _browseFilterService.GetBrowseFiltersAsync(criteria);
            if (browseFilters != null)
            {
                foreach (var filter in browseFilters)
                {
                    var existingFilters = allFilters.GetFiltersExceptSpecified(filter.Key);

                    AggregationRequest aggregationRequest = null;
                    IList<AggregationRequest> aggregationRequests = null;

                    switch (filter)
                    {
                        case AttributeFilter attributeFilter:
                            aggregationRequest = GetAttributeFilterAggregationRequest(attributeFilter, existingFilters);
                            break;
                        case RangeFilter rangeFilter:
                            aggregationRequests = GetRangeFilterAggregationRequests(rangeFilter, existingFilters);
                            break;
                        case PriceRangeFilter priceRangeFilter when priceRangeFilter.Currency.EqualsInvariant(criteria.Currency):
                            aggregationRequests = GetPriceRangeFilterAggregationRequests(priceRangeFilter, criteria, existingFilters);
                            break;
                    }

                    if (aggregationRequest != null)
                    {
                        result.Add(aggregationRequest);
                    }

                    if (aggregationRequests != null)
                    {
                        result.AddRange(aggregationRequests.Where(f => f != null));
                    }
                }
            }

            return result;
        }


        protected virtual AggregationRequest GetAttributeFilterAggregationRequest(AttributeFilter attributeFilter, IEnumerable<IFilter> existingFilters)
        {
            return new TermAggregationRequest
            {
                FieldName = attributeFilter.Key,
                Values = !attributeFilter.Values.IsNullOrEmpty() ? attributeFilter.Values.Select(v => v.Id).ToArray() : null,
                Filter = existingFilters.And(),
                Size = attributeFilter.FacetSize,
            };
        }

        protected virtual IList<AggregationRequest> GetRangeFilterAggregationRequests(RangeFilter rangeFilter, IList<IFilter> existingFilters)
        {
            var result = rangeFilter.Values?.Select(v => GetRangeFilterValueAggregationRequest(rangeFilter.Key, v, existingFilters)).ToList();
            return result;
        }

        protected virtual AggregationRequest GetRangeFilterValueAggregationRequest(string fieldName, RangeFilterValue value, IEnumerable<IFilter> existingFilters)
        {
            var valueFilter = FiltersHelper.CreateRangeFilter(fieldName, value.Lower, value.Upper, value.IncludeLower, value.IncludeUpper);

            var result = new TermAggregationRequest
            {
                Id = $"{fieldName}-{value.Id}",
                Filter = existingFilters.And(valueFilter)
            };

            return result;
        }

        protected virtual IList<AggregationRequest> GetPriceRangeFilterAggregationRequests(PriceRangeFilter priceRangeFilter, ProductIndexedSearchCriteria criteria, IList<IFilter> existingFilters)
        {
            var priceRangeFilters = priceRangeFilter.Values?.Select(v => GetPriceRangeFilterValueAggregationRequest(priceRangeFilter, v, existingFilters, criteria.Pricelists)).ToList();
            if (priceRangeFilters == null)
            {
                return new List<AggregationRequest>();
            }

            var commonFieldName = StringsHelper.JoinNonEmptyStrings("_", "price", priceRangeFilter.Currency).ToLowerInvariant();
            var ranges = priceRangeFilters.OfType<RangeAggregationRequest>().SelectMany(x => x.Values).ToList();
            var ids = string.Join('-', ranges.Select(x => x.Id));

            var result = new RangeAggregationRequest
            {
                Id = $"{priceRangeFilter.Key}-{ids}",
                Filter = existingFilters.And(),
                FieldName = commonFieldName,
                Values = ranges,
            };

            return new List<AggregationRequest> { result };
        }

        protected virtual AggregationRequest GetPriceRangeFilterValueAggregationRequest(PriceRangeFilter priceRangeFilter, RangeFilterValue value, IEnumerable<IFilter> existingFilters, IList<string> pricelists)
        {
            var result = new RangeAggregationRequest
            {
                Values = new List<RangeAggregationRequestValue>
                {
                    new RangeAggregationRequestValue
                    {
                        Id = value.Id,
                        IncludeLower = value.IncludeLower,
                        IncludeUpper = value.IncludeUpper,
                        Lower = value.Lower,
                        Upper = value.Upper,
                    },
                },
            };

            return result;
        }

        #endregion

        #region Response converter

        public virtual async Task<Aggregation[]> ConvertAggregationsAsync(IList<AggregationResponse> aggregationResponses, ProductIndexedSearchCriteria criteria)
        {
            var result = new List<Aggregation>();

            var browseFilters = await _browseFilterService.GetBrowseFiltersAsync(criteria);
            if (browseFilters != null && aggregationResponses?.Any() == true)
            {
                foreach (var filter in browseFilters)
                {
                    Aggregation aggregation = null;

                    switch (filter)
                    {
                        case AttributeFilter attributeFilter:
                            aggregation = GetAttributeAggregation(attributeFilter, aggregationResponses);
                            break;
                        case RangeFilter rangeFilter:
                            aggregation = GetRangeAggregation(rangeFilter, aggregationResponses);
                            break;
                        case PriceRangeFilter priceRangeFilter:
                            aggregation = GetPriceRangeAggregation(priceRangeFilter, aggregationResponses);
                            break;
                    }

                    if (aggregation?.Items?.Any() == true)
                    {
                        result.Add(aggregation);
                    }
                }
            }

            // Add localized labels for names and values
            if (result.Any())
            {
                await AddLabelsAsync(result, criteria.CatalogId);
            }

            return result.ToArray();
        }

        protected virtual Aggregation GetAttributeAggregation(AttributeFilter attributeFilter, IList<AggregationResponse> aggregationResponses)
        {
            Aggregation result = null;

            var fieldName = attributeFilter.Key;
            var aggregationResponse = aggregationResponses.FirstOrDefault(a => a.Id.EqualsInvariant(fieldName));

            if (aggregationResponse != null)
            {
                IList<AggregationResponseValue> aggregationResponseValues;

                if (attributeFilter.Values.IsNullOrEmpty())
                {
                    // Return all values
                    aggregationResponseValues = aggregationResponse.Values;
                }
                else
                {
                    // Return predefined values
                    aggregationResponseValues = attributeFilter.Values
                        .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(g => aggregationResponse.Values.FirstOrDefault(v => v.Id.EqualsInvariant(g.Key)))
                        .Where(v => v != null)
                        .ToArray();
                }

                if (aggregationResponseValues.Any())
                {
                    result = new Aggregation
                    {
                        AggregationType = "attr",
                        Field = fieldName,
                        Items = GetAttributeAggregationItems(aggregationResponseValues).ToArray(),
                    };
                }
            }

            return result;
        }

        protected virtual Aggregation GetRangeAggregation(RangeFilter rangeFilter, IList<AggregationResponse> aggregationResponses)
        {
            var result = new Aggregation
            {
                AggregationType = "range",
                Field = rangeFilter.Key,
                Items = GetRangeAggregationItems(rangeFilter.Key, rangeFilter.Values, aggregationResponses).ToArray(),
            };

            return result;
        }

        protected virtual Aggregation GetPriceRangeAggregation(PriceRangeFilter priceRangeFilter, IList<AggregationResponse> aggregationResponses)
        {
            var result = new Aggregation
            {
                AggregationType = "pricerange",
                Field = priceRangeFilter.Key,
                Items = GetRangeAggregationItems(priceRangeFilter.Key, priceRangeFilter.Values, aggregationResponses).ToArray(),
            };


            return result;
        }

        protected virtual IList<AggregationItem> GetAttributeAggregationItems(IList<AggregationResponseValue> aggregationResponseValues)
        {
            var result = aggregationResponseValues
                .Select(v =>
                {
                    if (v.Id.Contains(ModuleConstants.OutlineDelimiter))
                    {
                        var aggregationIdName = v.Id.Split(ModuleConstants.OutlineDelimiter);
                        if (aggregationIdName.Length > 1)
                        {
                            return new AggregationItem
                            {
                                Value = aggregationIdName[0],
                                Count = (int)v.Count,
                                Labels = new[] { new AggregationLabel { Label = aggregationIdName[1] } }
                            };
                        }
                    }

                    return new AggregationItem
                    {
                        Value = v.Id,
                        Count = (int)v.Count
                    };
                })
                .ToList();

            return result;
        }

        protected virtual IList<AggregationItem> GetRangeAggregationItems(string aggregationId, IList<RangeFilterValue> values, IList<AggregationResponse> aggregationResponses)
        {
            var result = new List<AggregationItem>();

            if (values != null)
            {
                foreach (var group in values.GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase))
                {
                    var valueId = group.Key;
                    var aggregationValueId = $"{aggregationId}-{valueId}";
                    var aggregationResponse = aggregationResponses.FirstOrDefault(v => v.Id.EqualsInvariant(aggregationValueId));

                    if (aggregationResponse?.Values?.Any() == true)
                    {
                        var value = aggregationResponse.Values.First();
                        var rangeValue = group.First();

                        var aggregationItem = new AggregationItem
                        {
                            Value = valueId,
                            Count = (int)value.Count,
                            RequestedLowerBound = !string.IsNullOrEmpty(rangeValue.Lower) ? rangeValue.Lower : null,
                            RequestedUpperBound = !string.IsNullOrEmpty(rangeValue.Upper) ? rangeValue.Upper : null,
                            IncludeLower = rangeValue.IncludeLower,
                            IncludeUpper = rangeValue.IncludeUpper,
                        };

                        result.Add(aggregationItem);
                    }
                }
            }

            return result;
        }

        protected virtual async Task AddLabelsAsync(IList<Aggregation> aggregations, string catalogId)
        {
            var allProperties = await _propertyService.GetAllCatalogPropertiesAsync(catalogId);

            foreach (var aggregation in aggregations)
            {
                // There can be many properties with the same name
                var properties = allProperties.Where(p => p.Name.EqualsInvariant(aggregation.Field)).ToArray();

                if (!properties.Any())
                {
                    continue;
                }

                var allPropertyLabels = properties.SelectMany(p => p.DisplayNames)
                    .Select(n => new AggregationLabel { Language = n.LanguageCode, Label = n.Name })
                    .ToArray();

                aggregation.Labels = GetFirstLabelForEachLanguage(allPropertyLabels);

                var dictionaryItemsSearchResult = await _propDictItemsSearchService.SearchAsync(
                    new PropertyDictionaryItemSearchCriteria { PropertyIds = properties.Select(x => x.Id).ToArray(), Take = int.MaxValue });
                var allDictItemsMap = dictionaryItemsSearchResult.Results.GroupBy(x => x.Alias)
                    .ToDictionary(x => x.Key,
                        x => x.SelectMany(dictItem => dictItem.LocalizedValues)
                            .Select(localizedValue => new AggregationLabel { Language = localizedValue.LanguageCode, Label = localizedValue.Value })
                            .ToArray(),
                        StringComparer.OrdinalIgnoreCase);

                foreach (var aggregationItem in aggregation.Items)
                {
                    var alias = aggregationItem.Value?.ToString();
                    if (string.IsNullOrEmpty(alias) || !allDictItemsMap.ContainsKey(alias))
                    {
                        continue;
                    }

                    var pair = allDictItemsMap.First(x => allDictItemsMap.Comparer.Equals(x.Key, alias));
                    aggregationItem.Value = pair.Key;
                    aggregationItem.Labels = GetFirstLabelForEachLanguage(pair.Value);
                }
            }
        }

        private static AggregationLabel[] GetFirstLabelForEachLanguage(AggregationLabel[] labels)
        {
            var result = labels
                .Where(x => !string.IsNullOrEmpty(x.Language) && !string.IsNullOrEmpty(x.Label))
                .GroupBy(x => x.Language, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.FirstOrDefault())
                .OrderBy(x => x?.Language)
                .ThenBy(x => x?.Label)
                .ToArray();

            return result.Any() ? result : null;
        }

        #endregion
    }
}
