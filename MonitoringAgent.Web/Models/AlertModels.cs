using System;
using System.Collections.Generic;

namespace MonitoringAgent.Web.Models
{
    public class GroupedAlertDto
    {
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Recommendation { get; set; } = string.Empty;
        // Extended payload fields
        public string Problem { get; set; } = string.Empty;
        public string RootCause { get; set; } = string.Empty;
        public double ImpactScore { get; set; }
        public double CurrentValue { get; set; }
        public double ThresholdValue { get; set; }
        public string Metric { get; set; } = string.Empty;
        public string AlertId { get; set; } = string.Empty;
        public DateTime LatestRaisedAt { get; set; }
        public int OccurrenceCount { get; set; }
        public List<int> AlertIds { get; set; } = new();
        public bool IsAnyAcknowledged { get; set; }
    }

    public class PaginatedList<T>
    {
        public List<T> Items { get; set; } = new();
        public int PageIndex { get; set; }
        public int TotalPages { get; set; }
        public bool HasPreviousPage => PageIndex > 1;
        public bool HasNextPage => PageIndex < TotalPages;

        public PaginatedList(List<T> items, int count, int pageIndex, int pageSize)
        {
            PageIndex = pageIndex;
            TotalPages = (int)Math.Ceiling(count / (double)pageSize);
            Items = items;
        }
    }
}
