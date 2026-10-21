using System.Collections.Generic;

namespace Ocean_Analyzer_Fresh.Models
{
    // Everything the Trends page plots, built by DashboardController.Trends.
    // Each series list has one entry per time bucket (week or month), in Buckets order.
    public class TrendsData
    {
        public string Grain { get; set; } // "week" or "month"
        public List<TrendBucket> Buckets { get; set; } = new List<TrendBucket>();

        public List<int> Shipments { get; set; } = new List<int>();
        public List<decimal> Revenue { get; set; } = new List<decimal>();
        public List<decimal> Cost { get; set; } = new List<decimal>();
        public List<decimal> GrossProfit { get; set; } = new List<decimal>();
        public List<decimal?> MarginPct { get; set; } = new List<decimal?>();
        public List<decimal> Cbm { get; set; } = new List<decimal>();

        // Shipment counts per bucket for Import, Export, Domestic and Other, in that order.
        public List<TrendSeries> ShipmentsByType { get; set; } = new List<TrendSeries>();
        // Gross profit per bucket for the five largest companies by revenue, plus "Other".
        public List<TrendSeries> CompanyGrossProfit { get; set; } = new List<TrendSeries>();

        public TrendTotals Current { get; set; } = new TrendTotals();
        public TrendTotals Previous { get; set; } = new TrendTotals();
        public string PreviousFrom { get; set; }
        public string PreviousTo { get; set; }
        // True when the previous period starts before the earliest data, so comparisons would mislead.
        public bool PreviousIncomplete { get; set; }

        public List<TrendEntityRow> Customers { get; set; } = new List<TrendEntityRow>();
        public List<TrendEntityRow> SalesPersons { get; set; } = new List<TrendEntityRow>();
    }

    public class TrendBucket
    {
        public string Start { get; set; } // yyyy-MM-dd
        public string Label { get; set; }
        // True when the selected range covers only part of this week or month.
        public bool Partial { get; set; }
    }

    public class TrendSeries
    {
        public string Name { get; set; }
        public List<decimal> Values { get; set; } = new List<decimal>();
    }

    public class TrendTotals : TrendVolume
    {
        public decimal Revenue { get; set; }
        public decimal Cost { get; set; }
        public decimal GrossProfit => Revenue - Cost;
        public decimal? MarginPct => Revenue > 0 ? GrossProfit / Revenue * 100 : (decimal?)null;
    }

    // Shipment count and volume. Many bookings (mostly full-container exports) carry weight but no
    // volume, so CBM is only a partial picture: CbmCoveragePct says how much of it is measured.
    public class TrendVolume
    {
        public int Shipments { get; set; }
        public decimal Cbm { get; set; }
        public decimal WeightKg { get; set; }
        public int CbmShipments { get; set; }
        // Revenue of the bookings that have a CBM value, so revenue per CBM compares like with like.
        public decimal CbmRevenue { get; set; }
        public decimal Tonnes => WeightKg / 1000;
        public decimal CbmCoveragePct => Shipments > 0 ? CbmShipments * 100m / Shipments : 0;
        public decimal? RevenuePerCbm => Cbm > 0 ? CbmRevenue / Cbm : (decimal?)null;
    }

    // One customer or salesperson: totals for the selected range, revenue in the previous
    // period of the same length, and revenue per bucket for the sparkline.
    public class TrendEntityRow : TrendVolume
    {
        public string Name { get; set; }
        public decimal Revenue { get; set; }
        public decimal Cost { get; set; }
        public decimal GrossProfit => Revenue - Cost;
        public decimal PreviousRevenue { get; set; }
        public List<decimal> RevenueByBucket { get; set; } = new List<decimal>();
    }
}
