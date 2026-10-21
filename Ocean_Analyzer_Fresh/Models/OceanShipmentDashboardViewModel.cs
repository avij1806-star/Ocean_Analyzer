using System;
using System.Data;
using System.Collections.Generic;

namespace Ocean_Analyzer_Fresh.Models
{
    public class OceanShipmentDashboardViewModel
    {
        public DataTable ShipmentData { get; set; } = new DataTable();
        public int TotalShipments { get; set; }
        public decimal TotalCost { get; set; }
        public decimal TotalPaidRevenue { get; set; }
        public decimal TotalGrossProfit { get; set; }

        public string DateType { get; set; } // "etd", "eta", or "ets"
        public Dictionary<string, int> ShipmentsByUser { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> TopCustomers { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> ShipmentsByStation { get; set; } = new Dictionary<string, int>();

        // Filter Selections
        public string SelectedStation { get; set; }
        public string SelectedFileStatus { get; set; }
        public string SelectedMode { get; set; }
        public string SelectedCreatedBy { get; set; }
        public string SelectedCustomer { get; set; }
        public string FromDate { get; set; }
        public string ToDate { get; set; }
        public DateTime? DataAvailableSince { get; set; }

        // Available Filter Options
        public List<string> AvailableStations { get; set; } = new List<string>();
        public List<string> AvailableFileStatuses { get; set; } = new List<string>();
        public List<string> AvailableModes { get; set; } = new List<string>();
        public List<string> AvailableCreatedBy { get; set; } = new List<string>();
        public List<string> AvailableCustomers { get; set; } = new List<string>();

        public string SelectedSalesPerson { get; set; }
        public List<string> AvailableSalesPersons { get; set; } = new List<string>();
        public Dictionary<string, int> ShipmentsBySalesPerson { get; set; } = new Dictionary<string, int>();

        public Dictionary<string, int> CustomersByShipment { get; set; } = new Dictionary<string, int>();

        public DataTable CompanySummaryData { get; set; } = new DataTable();
        public DataTable CustomerSummaryData { get; set; } = new DataTable();

        public DataTable OperatorFinancialData { get; set; } = new DataTable();

        // CBM (cubic metre) volume analysis
        public decimal TotalCbm { get; set; }
        public int ShipmentsWithCbm { get; set; }
        public string CbmPeriodLabel { get; set; } // "Day" or "Month"
        public DataTable CbmByCompany { get; set; } = new DataTable();
        public DataTable CbmByCustomer { get; set; } = new DataTable();
        public DataTable CbmBySalesPerson { get; set; } = new DataTable();
        public DataTable CbmByPeriod { get; set; } = new DataTable();
        public DataTable CbmBySalesPersonPeriod { get; set; } = new DataTable();
        public DataTable CbmByCompanyPeriod { get; set; } = new DataTable();
        public DataTable CbmByType { get; set; } = new DataTable();
    }
}