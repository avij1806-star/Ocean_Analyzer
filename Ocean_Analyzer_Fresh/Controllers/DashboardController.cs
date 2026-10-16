using System;
using System.Data;
using System.Web;
using System.Collections.Generic;
using System.Web.Mvc;
using System.IO;
using System.Configuration;
using MySqlConnector;
using Ocean_Analyzer_Fresh.Models;
using System.Text;
using System.Linq;
using ExcelDataReader;
using MySql.Data.MySqlClient;

namespace Ocean_Analyzer.Controllers
{
    public class DashboardController : Controller
    {
        private string connStr = ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString;

        private string GetGroupedStationSql(string columnName)
        {
            return $@"CASE 
                WHEN UPPER(TRIM({columnName})) = 'GILLESPIE-MUNRO (USA) INC.' THEN 'Gillespie-Munro USA'
                WHEN UPPER(TRIM({columnName})) LIKE '%GILLESPIE-MUNRO%' THEN 'Gillespie-Munro Canada'
                WHEN UPPER(TRIM({columnName})) LIKE '%GILLSHIP NAVIGATION%' THEN 'Gillship Navigation'
                WHEN {columnName} IS NULL OR TRIM({columnName}) = '' THEN 'Unassigned'
                ELSE TRIM({columnName})
            END";
        }

        // Earliest date in the data for the chosen date basis (ETS, ETA or Posted). Months holding
        // under 1% of bookings are skipped so a few stray or mistyped dates don't make the data
        // look far older than it is. Returns null when nothing can be read.
        private DateTime? GetDataAvailableSince(string dateType)
        {
            string dateColumn = "ETS";
            string dtLower = dateType?.ToLower();
            if (dtLower == "eta") dateColumn = "ETA";
            else if (dtLower == "posted" || dtLower == "dateposted") dateColumn = "DatePosted";

            string parsedDate = $@"COALESCE(
                STR_TO_DATE(TRIM({dateColumn}), '%m/%d/%Y'),
                STR_TO_DATE(TRIM({dateColumn}), '%d-%m-%Y'),
                STR_TO_DATE(TRIM({dateColumn}), '%Y-%m-%d'))";

            string query = $@"
                SELECT MIN(d.dt)
                FROM (SELECT {parsedDate} AS dt FROM Ocean_Revenue_Analyzer_New) d
                JOIN (
                    SELECT DATE_FORMAT(dt, '%Y-%m') AS ym
                    FROM (SELECT {parsedDate} AS dt FROM Ocean_Revenue_Analyzer_New) x
                    WHERE dt IS NOT NULL
                    GROUP BY ym
                    HAVING COUNT(*) >= 0.01 * (SELECT COUNT(*) FROM Ocean_Revenue_Analyzer_New)
                ) m ON DATE_FORMAT(d.dt, '%Y-%m') = m.ym";

            try
            {
                using (var conn = new MySqlConnection(connStr))
                using (var cmd = new MySqlCommand(query, conn))
                {
                    conn.Open();
                    object result = cmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value) return Convert.ToDateTime(result);
                }
            }
            catch (Exception) { }

            return null;
        }

        [HttpGet]
        public ActionResult Ocean_Dashboard(string viewMode, string selectedStation, string selectedFileStatus, string selectedCustomer, string selectedSalesPerson, string selectedCreatedBy, string fromDate, string toDate, string dateType)
        {
            viewMode = string.IsNullOrEmpty(viewMode) ? "operational" : viewMode;

            if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate))
            {
                var now = DateTime.Now;
                fromDate = string.IsNullOrEmpty(fromDate) ? new DateTime(now.Year, now.Month, 1).ToString("yyyy-MM-dd") : fromDate;
                toDate = string.IsNullOrEmpty(toDate) ? now.ToString("yyyy-MM-dd") : toDate;
            }

            var model = new OceanShipmentDashboardViewModel
            {
                SelectedStation = selectedStation,
                SelectedFileStatus = selectedFileStatus,
                SelectedCustomer = selectedCustomer,
                SelectedSalesPerson = selectedSalesPerson,
                SelectedCreatedBy = selectedCreatedBy,
                FromDate = fromDate,
                ToDate = toDate,
                DateType = string.IsNullOrEmpty(dateType) ? "ets" : dateType
            };
            model.DataAvailableSince = GetDataAvailableSince(model.DateType);

            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();
                    LoadDropdownFilters(conn, model);

                    var whereClause = new StringBuilder(" WHERE 1=1");
                    var cmdParams = new List<MySqlParameter>();

                    if (!string.IsNullOrEmpty(selectedStation) && selectedStation != "-- All Stations --" && selectedStation != "-- All Companies --")
                    {
                        if (selectedStation == "Gillespie-Munro Canada")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                        }
                        else if (selectedStation == "Gillespie-Munro USA")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) = 'GILLESPIE-MUNRO (USA) INC.'");
                        }
                        else if (selectedStation == "Gillship Navigation")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
                        }
                        else
                        {
                            whereClause.Append($" AND {GetGroupedStationSql("Station")} = @station");
                            cmdParams.Add(new MySqlParameter("@station", selectedStation));
                        }
                    }

                    if (!string.IsNullOrEmpty(selectedFileStatus) && selectedFileStatus != "-- All Statuses --")
                    {
                        whereClause.Append(" AND FileStatus = @fileStatus");
                        cmdParams.Add(new MySqlParameter("@fileStatus", selectedFileStatus));
                    }

                    if (!string.IsNullOrEmpty(selectedCustomer) && selectedCustomer != "-- All Customers --")
                    {
                        whereClause.Append(" AND TRIM(Customer) = @customer");
                        cmdParams.Add(new MySqlParameter("@customer", selectedCustomer.Trim()));
                    }

                    if (!string.IsNullOrEmpty(selectedSalesPerson) && selectedSalesPerson != "-- All Sales Persons --")
                    {
                        whereClause.Append(" AND TRIM(Salesperson) = @salesPerson");
                        cmdParams.Add(new MySqlParameter("@salesPerson", selectedSalesPerson.Trim()));
                    }

                    if (!string.IsNullOrEmpty(selectedCreatedBy) && selectedCreatedBy != "-- All Users --")
                    {
                        whereClause.Append(" AND TRIM(Createdby) = @createdByUser");
                        cmdParams.Add(new MySqlParameter("@createdByUser", selectedCreatedBy.Trim()));
                    }

                    if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
                    {
                        string dateColumn = "ETS";
                        string dtLower = model.DateType?.ToLower();
                        if (dtLower == "eta") dateColumn = "ETA";
                        else if (dtLower == "posted" || dtLower == "dateposted") dateColumn = "DatePosted";

                        if (DateTime.TryParse(fromDate, out DateTime parsedFrom) && DateTime.TryParse(toDate, out DateTime parsedTo))
                        {
                            string sqlFrom = parsedFrom.ToString("yyyy-MM-dd");
                            string sqlTo = parsedTo.ToString("yyyy-MM-dd");

                            whereClause.Append($@" AND (
                                STR_TO_DATE(TRIM({dateColumn}), '%m/%d/%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
                                STR_TO_DATE(TRIM({dateColumn}), '%d-%m-%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
                                STR_TO_DATE(TRIM({dateColumn}), '%Y-%m-%d') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d')
                            )");

                            cmdParams.Add(new MySqlParameter("@sqlFrom", sqlFrom));
                            cmdParams.Add(new MySqlParameter("@sqlTo", sqlTo));
                        }
                    }

                    string metricsQuery = $@"SELECT 
                        COUNT(DISTINCT BookingNo), 
                        COALESCE(SUM(CAST(NULLIF(TRIM(PostedInvoices), '') AS DECIMAL(18,2))), 0),
                        COALESCE(SUM(ABS(CAST(NULLIF(TRIM(PostedCost), '') AS DECIMAL(18,2)))), 0)
                        FROM Ocean_Revenue_Analyzer_New {whereClause}";

                    using (var cmd = new MySqlCommand(metricsQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                model.TotalShipments = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                                model.TotalPaidRevenue = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1);
                                model.TotalCost = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
                                model.TotalGrossProfit = model.TotalPaidRevenue - model.TotalCost;
                            }
                        }
                    }

                    LoadAllChartsData(conn, model, whereClause.ToString(), cmdParams);

                    string tableQuery = $"SELECT BookingNo as ShipmentNo, Customer, Station, Type, Salesperson as SalesPerson, ActualGP, PostedCost, PostedInvoices, DatePosted, FileStatus, Createdby FROM Ocean_Revenue_Analyzer_New {whereClause}";
                    using (var cmd = new MySqlCommand(tableQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            model.ShipmentData.Load(reader);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Dashboard error: " + ex.Message;
            }

            ViewBag.ViewMode = viewMode;
            return View(model);
        }

        [HttpGet]
        public ActionResult ProfitAnalysis(string selectedStation = null, string selectedCustomer = null, string selectedSalesPerson = null, string fromDate = null, string toDate = null, string dateType = null)
        {
            if (selectedStation == "-- All Companies --" || selectedStation == "-- All Stations --") selectedStation = null;
            if (selectedCustomer == "-- All Customers --") selectedCustomer = null;
            if (selectedSalesPerson == "-- All Sales Persons --") selectedSalesPerson = null;

            if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate))
            {
                var now = DateTime.Now;
                fromDate = string.IsNullOrEmpty(fromDate) ? new DateTime(now.Year, now.Month, 1).ToString("yyyy-MM-dd") : fromDate;
                toDate = string.IsNullOrEmpty(toDate) ? now.ToString("yyyy-MM-dd") : toDate;
            }

            var model = new OceanShipmentDashboardViewModel
            {
                SelectedStation = selectedStation,
                SelectedCustomer = selectedCustomer,
                SelectedSalesPerson = selectedSalesPerson,
                FromDate = fromDate,
                ToDate = toDate,
                DateType = string.IsNullOrEmpty(dateType) ? "ets" : dateType
            };
            model.DataAvailableSince = GetDataAvailableSince(model.DateType);

            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();
                    LoadDropdownFilters(conn, model);

                    var whereClause = new StringBuilder(" WHERE 1=1");
                    var cmdParams = new List<MySqlParameter>();

                    if (!string.IsNullOrEmpty(selectedStation))
                    {
                        if (selectedStation == "Gillespie-Munro Canada")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                        }
                        else if (selectedStation == "Gillespie-Munro USA")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) = 'GILLESPIE-MUNRO (USA) INC.'");
                        }
                        else if (selectedStation == "Gillship Navigation")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
                        }
                        else
                        {
                            whereClause.Append($" AND {GetGroupedStationSql("Station")} = @station");
                            cmdParams.Add(new MySqlParameter("@station", selectedStation));
                        }
                    }

                    if (!string.IsNullOrEmpty(selectedCustomer))
                    {
                        whereClause.Append(" AND UPPER(TRIM(Customer)) = UPPER(@customer)");
                        cmdParams.Add(new MySqlParameter("@customer", selectedCustomer.Trim()));
                    }

                    if (!string.IsNullOrEmpty(selectedSalesPerson))
                    {
                        whereClause.Append(" AND UPPER(TRIM(Salesperson)) = UPPER(@salesPerson)");
                        cmdParams.Add(new MySqlParameter("@salesPerson", selectedSalesPerson.Trim()));
                    }

                    if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
                    {
                        string dateColumn = "ETS";
                        string dtLower = model.DateType?.ToLower();
                        if (dtLower == "eta") dateColumn = "ETA";
                        else if (dtLower == "posted" || dtLower == "dateposted") dateColumn = "DatePosted";

                        if (DateTime.TryParse(fromDate, out DateTime parsedFrom) && DateTime.TryParse(toDate, out DateTime parsedTo))
                        {
                            string sqlFrom = parsedFrom.ToString("yyyy-MM-dd");
                            string sqlTo = parsedTo.ToString("yyyy-MM-dd");

                            whereClause.Append($@" AND (
                                STR_TO_DATE(TRIM({dateColumn}), '%m/%d/%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
                                STR_TO_DATE(TRIM({dateColumn}), '%d-%m-%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
                                STR_TO_DATE(TRIM({dateColumn}), '%Y-%m-%d') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d')
                            )");
                            cmdParams.Add(new MySqlParameter("@sqlFrom", sqlFrom));
                            cmdParams.Add(new MySqlParameter("@sqlTo", sqlTo));
                        }
                    }

                    string cleanRev = "CAST(NULLIF(REPLACE(REPLACE(TRIM(PostedInvoices), ',', ''), '$', ''), '') AS DECIMAL(18,2))";
                    string cleanCost = "ABS(CAST(NULLIF(REPLACE(REPLACE(TRIM(PostedCost), ',', ''), '$', ''), '') AS DECIMAL(18,2)))";

                    string metricsQuery = $@"SELECT 
                        COUNT(DISTINCT BookingNo), 
                        COALESCE(SUM({cleanRev}), 0),
                        COALESCE(SUM({cleanCost}), 0)
                        FROM Ocean_Revenue_Analyzer_New {whereClause}";

                    using (var cmd = new MySqlCommand(metricsQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                model.TotalShipments = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                                model.TotalPaidRevenue = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1);
                                model.TotalCost = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
                                model.TotalGrossProfit = model.TotalPaidRevenue - model.TotalCost;
                            }
                        }
                    }

                    model.CompanySummaryData = new DataTable();
                    string companySummaryQuery = $@"SELECT 
                        {GetGroupedStationSql("Station")} as Company,
                        COUNT(DISTINCT BookingNo) as Shipments,
                        COALESCE(SUM({cleanRev}), 0) as Revenue,
                        COALESCE(SUM({cleanCost}), 0) as Cost,
                        (COALESCE(SUM({cleanRev}), 0) - COALESCE(SUM({cleanCost}), 0)) as GrossProfit
                        FROM Ocean_Revenue_Analyzer_New {whereClause} 
                        GROUP BY Company 
                        ORDER BY Revenue DESC";

                    using (var cmd = new MySqlCommand(companySummaryQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            model.CompanySummaryData.Load(reader);
                        }
                    }

                    model.CustomerSummaryData = new DataTable();
                    string customerSummaryQuery = $@"SELECT 
                        COALESCE(NULLIF(TRIM(Customer), ''), 'Unassigned') as CustomerName,
                        COUNT(DISTINCT BookingNo) as Shipments,
                        COALESCE(SUM({cleanRev}), 0) as Revenue,
                        COALESCE(SUM({cleanCost}), 0) as Cost,
                        (COALESCE(SUM({cleanRev}), 0) - COALESCE(SUM({cleanCost}), 0)) as GrossProfit
                        FROM Ocean_Revenue_Analyzer_New {whereClause} 
                        GROUP BY CustomerName 
                        ORDER BY GrossProfit DESC LIMIT 20";

                    using (var cmd = new MySqlCommand(customerSummaryQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            model.CustomerSummaryData.Load(reader);
                        }
                    }

                    string tableQuery = $"SELECT BookingNo as ShipmentNo, Customer, Station, Type, Salesperson as SalesPerson, ActualGP, PostedCost, PostedInvoices, DatePosted FROM Ocean_Revenue_Analyzer_New {whereClause}";
                    using (var cmd = new MySqlCommand(tableQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            model.ShipmentData.Load(reader);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }

            return View(model);
        }


        [HttpGet]
        public ActionResult StationTradeLane(string selectedStation = null, string fromDate = null, string toDate = null, string dateType = null)
        {
            if (string.IsNullOrEmpty(selectedStation) || selectedStation == "-- All Companies --" || selectedStation == "-- All Stations --") selectedStation = null;

            if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate))
            {
                var now = DateTime.Now;
                fromDate = string.IsNullOrEmpty(fromDate) ? new DateTime(now.Year, now.Month, 1).ToString("yyyy-MM-dd") : fromDate;
                toDate = string.IsNullOrEmpty(toDate) ? now.ToString("yyyy-MM-dd") : toDate;
            }

            var model = new OceanShipmentDashboardViewModel
            {
                SelectedStation = selectedStation,
                FromDate = fromDate,
                ToDate = toDate,
                DateType = string.IsNullOrEmpty(dateType) ? "ets" : dateType
            };
            model.DataAvailableSince = GetDataAvailableSince(model.DateType);

            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();
                    LoadDropdownFilters(conn, model);

                    var whereClause = new StringBuilder(" WHERE 1=1");

                    if (!string.IsNullOrEmpty(selectedStation))
                    {
                        if (selectedStation == "Gillespie-Munro Canada")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                        }
                        else if (selectedStation == "Gillespie-Munro USA")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO (USA) INC.%'");
                        }
                        else if (selectedStation == "Gillship Navigation")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
                        }
                        else
                        {
                            whereClause.Append($" AND {GetGroupedStationSql("Station")} = @station");
                        }
                    }

                    string dateColumn = "ETS";
                    string dtLower = model.DateType?.ToLower();
                    if (dtLower == "eta") dateColumn = "ETA";
                    else if (dtLower == "posted" || dtLower == "dateposted") dateColumn = "DatePosted";

                    string sqlFrom = null;
                    string sqlTo = null;
                    if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
                    {
                        if (DateTime.TryParse(fromDate, out DateTime parsedFrom) && DateTime.TryParse(toDate, out DateTime parsedTo))
                        {
                            sqlFrom = parsedFrom.ToString("yyyy-MM-dd");
                            sqlTo = parsedTo.ToString("yyyy-MM-dd");

                            whereClause.Append($@" AND (
                        STR_TO_DATE(TRIM({dateColumn}), '%m/%d/%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
                        STR_TO_DATE(TRIM({dateColumn}), '%d-%m-%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
                        STR_TO_DATE(TRIM({dateColumn}), '%Y-%m-%d') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d')
                    )");
                        }
                    }

                    List<MySqlParameter> GetFreshParameters()
                    {
                        var p = new List<MySqlParameter>();
                        if (!string.IsNullOrEmpty(selectedStation) && selectedStation != "Gillespie-Munro Canada" && selectedStation != "Gillespie-Munro USA" && selectedStation != "Gillship Navigation")
                        {
                            p.Add(new MySqlParameter("@station", selectedStation));
                        }
                        if (!string.IsNullOrEmpty(sqlFrom) && !string.IsNullOrEmpty(sqlTo))
                        {
                            p.Add(new MySqlParameter("@sqlFrom", sqlFrom));
                            p.Add(new MySqlParameter("@sqlTo", sqlTo));
                        }
                        return p;
                    }

                    // Financial String Sanitization Helpers
                    string cleanRev = "CAST(NULLIF(REPLACE(REPLACE(TRIM(PostedInvoices), ',', ''), '$', ''), '') AS DECIMAL(18,2))";
                    string cleanCost = "ABS(CAST(NULLIF(REPLACE(REPLACE(TRIM(PostedCost), ',', ''), '$', ''), '') AS DECIMAL(18,2)))";

                    // 1. Metrics KPI Query
                    string metricsQuery = $@"SELECT 
                COUNT(DISTINCT BookingNo), 
                COALESCE(SUM({cleanRev}), 0),
                COALESCE(SUM({cleanCost}), 0)
                FROM Ocean_Revenue_Analyzer_New {whereClause}";

                    using (var cmd = new MySqlCommand(metricsQuery, conn))
                    {
                        var pList = GetFreshParameters();
                        if (pList.Count > 0) cmd.Parameters.AddRange(pList.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                model.TotalShipments = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                                model.TotalPaidRevenue = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1);
                                model.TotalCost = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
                                model.TotalGrossProfit = model.TotalPaidRevenue - model.TotalCost;
                            }
                        }
                    }

                    // 2. Dynamic Multi-Company Trend Query over Time
                    DataTable trendTable = new DataTable();
                    string stationTrendQuery = $@"SELECT 
                DATE_FORMAT(STR_TO_DATE(TRIM({dateColumn}), '%Y-%m-%d'), '%Y-%m') as TrendMonth,
                {GetGroupedStationSql("Station")} as CompanyStation,
                COUNT(DISTINCT BookingNo) as MonthlyVolume
                FROM Ocean_Revenue_Analyzer_New 
                {whereClause} 
                AND {dateColumn} IS NOT NULL AND TRIM({dateColumn}) != ''
                GROUP BY TrendMonth, CompanyStation 
                ORDER BY TrendMonth ASC";

                    using (var cmd = new MySqlCommand(stationTrendQuery, conn))
                    {
                        var pList = GetFreshParameters();
                        if (pList.Count > 0) cmd.Parameters.AddRange(pList.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            trendTable.Load(reader);
                        }
                    }
                    ViewBag.TrendData = trendTable;

                    // 3. Company / Station Profitability & Margin Breakdown Query
                    model.OperatorFinancialData = new DataTable();
                    string stationProfitQuery = $@"SELECT 
                {GetGroupedStationSql("Station")} as CompanyStation,
                COUNT(DISTINCT BookingNo) as Shipments,
                COALESCE(SUM({cleanRev}), 0) as Revenue,
                COALESCE(SUM({cleanCost}), 0) as Cost,
                (COALESCE(SUM({cleanRev}), 0) - COALESCE(SUM({cleanCost}), 0)) as GrossProfit
                FROM Ocean_Revenue_Analyzer_New {whereClause} 
                GROUP BY CompanyStation 
                ORDER BY GrossProfit DESC";

                    using (var cmd = new MySqlCommand(stationProfitQuery, conn))
                    {
                        var pList = GetFreshParameters();
                        if (pList.Count > 0) cmd.Parameters.AddRange(pList.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            model.OperatorFinancialData.Load(reader);
                        }
                    }

                    // 4. Ledger Table Query
                    string tableQuery = $"SELECT BookingNo as ShipmentNo, Customer, Station, Type, Salesperson as SalesPerson, ActualGP, PostedCost, PostedInvoices, DatePosted FROM Ocean_Revenue_Analyzer_New {whereClause}";
                    using (var cmd = new MySqlCommand(tableQuery, conn))
                    {
                        var pList = GetFreshParameters();
                        if (pList.Count > 0) cmd.Parameters.AddRange(pList.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            model.ShipmentData.Load(reader);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }

            return View(model);
        }


        //[HttpGet]
        //public ActionResult StationTradeLane(string selectedStation = null, string fromDate = null, string toDate = null, string dateType = null)
        //{
        //    if (string.IsNullOrEmpty(selectedStation) || selectedStation == "-- All Companies --" || selectedStation == "-- All Stations --") selectedStation = null;

        //    if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate))
        //    {
        //        var now = DateTime.Now;
        //        fromDate = string.IsNullOrEmpty(fromDate) ? new DateTime(now.Year, now.Month, 1).ToString("yyyy-MM-dd") : fromDate;
        //        toDate = string.IsNullOrEmpty(toDate) ? now.ToString("yyyy-MM-dd") : toDate;
        //    }

        //    var model = new OceanShipmentDashboardViewModel
        //    {
        //        SelectedStation = selectedStation,
        //        FromDate = fromDate,
        //        ToDate = toDate,
        //        DateType = string.IsNullOrEmpty(dateType) ? "ets" : dateType
        //    };

        //    try
        //    {
        //        using (var conn = new MySqlConnection(connStr))
        //        {
        //            conn.Open();
        //            LoadDropdownFilters(conn, model);

        //            var whereClause = new StringBuilder(" WHERE 1=1");

        //            if (!string.IsNullOrEmpty(selectedStation))
        //            {
        //                if (selectedStation == "Gillespie-Munro Canada")
        //                {
        //                    whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
        //                }
        //                else if (selectedStation == "Gillespie-Munro USA")
        //                {
        //                    whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO (USA) INC.%'");
        //                }
        //                else if (selectedStation == "Gillship Navigation")
        //                {
        //                    whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
        //                }
        //                else
        //                {
        //                    whereClause.Append($" AND {GetGroupedStationSql("Station")} = @station");
        //                }
        //            }

        //            string dateColumn = "ETS";
        //            string dtLower = model.DateType?.ToLower();
        //            if (dtLower == "eta") dateColumn = "ETA";
        //            else if (dtLower == "posted" || dtLower == "dateposted") dateColumn = "DatePosted";

        //            string sqlFrom = null;
        //            string sqlTo = null;
        //            if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
        //            {
        //                if (DateTime.TryParse(fromDate, out DateTime parsedFrom) && DateTime.TryParse(toDate, out DateTime parsedTo))
        //                {
        //                    sqlFrom = parsedFrom.ToString("yyyy-MM-dd");
        //                    sqlTo = parsedTo.ToString("yyyy-MM-dd");

        //                    whereClause.Append($@" AND (
        //                        STR_TO_DATE(TRIM({dateColumn}), '%m/%d/%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
        //                        STR_TO_DATE(TRIM({dateColumn}), '%d-%m-%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
        //                        STR_TO_DATE(TRIM({dateColumn}), '%Y-%m-%d') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d')
        //                    )");
        //                }
        //            }

        //            List<MySqlParameter> GetFreshParameters()
        //            {
        //                var p = new List<MySqlParameter>();
        //                if (!string.IsNullOrEmpty(selectedStation) && selectedStation != "Gillespie-Munro Canada" && selectedStation != "Gillespie-Munro USA" && selectedStation != "Gillship Navigation")
        //                {
        //                    p.Add(new MySqlParameter("@station", selectedStation));
        //                }
        //                if (!string.IsNullOrEmpty(sqlFrom) && !string.IsNullOrEmpty(sqlTo))
        //                {
        //                    p.Add(new MySqlParameter("@sqlFrom", sqlFrom));
        //                    p.Add(new MySqlParameter("@sqlTo", sqlTo));
        //                }
        //                return p;
        //            }

        //            string metricsQuery = $@"SELECT 
        //                COUNT(DISTINCT BookingNo), 
        //                COALESCE(SUM(CAST(NULLIF(TRIM(PostedInvoices), '') AS DECIMAL(18,2))), 0),
        //                COALESCE(SUM(ABS(CAST(NULLIF(TRIM(PostedCost), '') AS DECIMAL(18,2)))), 0)
        //                FROM Ocean_Revenue_Analyzer_New {whereClause}";

        //            using (var cmd = new MySqlCommand(metricsQuery, conn))
        //            {
        //                var pList = GetFreshParameters();
        //                if (pList.Count > 0) cmd.Parameters.AddRange(pList.ToArray());
        //                using (var reader = cmd.ExecuteReader())
        //                {
        //                    if (reader.Read())
        //                    {
        //                        model.TotalShipments = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        //                        model.TotalPaidRevenue = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1);
        //                        model.TotalCost = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
        //                        model.TotalGrossProfit = model.TotalPaidRevenue - model.TotalCost;
        //                    }
        //                }
        //            }

        //            DataTable trendTable = new DataTable();
        //            string stationTrendQuery = $@"SELECT 
        //                DATE_FORMAT(STR_TO_DATE(TRIM({dateColumn}), '%Y-%m-%d'), '%Y-%m') as TrendMonth,
        //                {GetGroupedStationSql("Station")} as CompanyStation,
        //                COUNT(DISTINCT BookingNo) as MonthlyVolume
        //                FROM Ocean_Revenue_Analyzer_New 
        //                {whereClause} 
        //                AND {dateColumn} IS NOT NULL AND TRIM({dateColumn}) != ''
        //                GROUP BY TrendMonth, CompanyStation 
        //                ORDER BY TrendMonth ASC";

        //            using (var cmd = new MySqlCommand(stationTrendQuery, conn))
        //            {
        //                var pList = GetFreshParameters();
        //                if (pList.Count > 0) cmd.Parameters.AddRange(pList.ToArray());
        //                using (var reader = cmd.ExecuteReader())
        //                {
        //                    trendTable.Load(reader);
        //                }
        //            }
        //            ViewBag.TrendData = trendTable;

        //            model.OperatorFinancialData = new DataTable();
        //            string stationProfitQuery = $@"SELECT 
        //                {GetGroupedStationSql("Station")} as CompanyStation,
        //                COUNT(DISTINCT BookingNo) as Shipments,
        //                COALESCE(SUM(CAST(NULLIF(TRIM(PostedInvoices), '') AS DECIMAL(18,2)))), 0) as Revenue,
        //                COALESCE(SUM(ABS(CAST(NULLIF(TRIM(PostedCost), '') AS DECIMAL(18,2)))), 0) as Cost,
        //                COALESCE(SUM(CAST(NULLIF(TRIM(ActualGP), '') AS DECIMAL(18,2)))), 0) as GrossProfit
        //                FROM Ocean_Revenue_Analyzer_New {whereClause} GROUP BY CompanyStation ORDER BY GrossProfit DESC";

        //            using (var cmd = new MySqlCommand(stationProfitQuery, conn))
        //            {
        //                var pList = GetFreshParameters();
        //                if (pList.Count > 0) cmd.Parameters.AddRange(pList.ToArray());
        //                using (var reader = cmd.ExecuteReader())
        //                {
        //                    model.OperatorFinancialData.Load(reader);
        //                }
        //            }

        //            string tableQuery = $"SELECT BookingNo as ShipmentNo, Customer, Station, Type, Salesperson as SalesPerson, ActualGP, PostedCost, PostedInvoices, DatePosted FROM Ocean_Revenue_Analyzer_New {whereClause}";
        //            using (var cmd = new MySqlCommand(tableQuery, conn))
        //            {
        //                var pList = GetFreshParameters();
        //                if (pList.Count > 0) cmd.Parameters.AddRange(pList.ToArray());
        //                using (var reader = cmd.ExecuteReader())
        //                {
        //                    model.ShipmentData.Load(reader);
        //                }
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        TempData["ErrorMessage"] = ex.Message;
        //    }

        //    return View(model);
        //}

        [HttpGet]
        public ActionResult SalesPersonPerformance(string selectedStation = null, string selectedCustomer = null, string selectedSalesPerson = null, string fromDate = null, string toDate = null, string dateType = null)
        {
            if (selectedStation == "-- All Companies --" || selectedStation == "-- All Stations --") selectedStation = null;
            if (selectedCustomer == "-- All Customers --") selectedCustomer = null;
            if (selectedSalesPerson == "-- All Sales Persons --") selectedSalesPerson = null;

            if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate))
            {
                var now = DateTime.Now;
                fromDate = string.IsNullOrEmpty(fromDate) ? new DateTime(now.Year, now.Month, 1).ToString("yyyy-MM-dd") : fromDate;
                toDate = string.IsNullOrEmpty(toDate) ? now.ToString("yyyy-MM-dd") : toDate;
            }

            var model = new OceanShipmentDashboardViewModel
            {
                SelectedStation = selectedStation,
                SelectedCustomer = selectedCustomer,
                SelectedSalesPerson = selectedSalesPerson,
                FromDate = fromDate,
                ToDate = toDate,
                DateType = string.IsNullOrEmpty(dateType) ? "ets" : dateType
            };
            model.DataAvailableSince = GetDataAvailableSince(model.DateType);

            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();
                    LoadDropdownFilters(conn, model);

                    var whereClause = new StringBuilder(" WHERE 1=1");
                    var cmdParams = new List<MySqlParameter>();

                    if (!string.IsNullOrEmpty(selectedStation))
                    {
                        if (selectedStation == "Gillespie-Munro Canada")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                        }
                        else if (selectedStation == "Gillespie-Munro USA")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO (USA) INC.%'");
                        }
                        else if (selectedStation == "Gillship Navigation")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
                        }
                        else
                        {
                            whereClause.Append($" AND {GetGroupedStationSql("Station")} = @station");
                            cmdParams.Add(new MySqlParameter("@station", selectedStation));
                        }
                    }

                    if (!string.IsNullOrEmpty(selectedCustomer))
                    {
                        whereClause.Append(" AND LOWER(TRIM(Customer)) LIKE LOWER(@customer)");
                        cmdParams.Add(new MySqlParameter("@customer", "%" + selectedCustomer.Trim() + "%"));
                    }

                    if (!string.IsNullOrEmpty(selectedSalesPerson))
                    {
                        whereClause.Append(" AND LOWER(TRIM(Salesperson)) LIKE LOWER(@salesPerson)");
                        cmdParams.Add(new MySqlParameter("@salesPerson", "%" + selectedSalesPerson.Trim() + "%"));
                    }

                    if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
                    {
                        string dateColumn = "ETS";
                        string dtLower = model.DateType?.ToLower();
                        if (dtLower == "eta") dateColumn = "ETA";
                        else if (dtLower == "posted" || dtLower == "dateposted") dateColumn = "DatePosted";

                        if (DateTime.TryParse(fromDate, out DateTime parsedFrom) && DateTime.TryParse(toDate, out DateTime parsedTo))
                        {
                            string sqlFrom = parsedFrom.ToString("yyyy-MM-dd");
                            string sqlTo = parsedTo.ToString("yyyy-MM-dd");

                            whereClause.Append($@" AND (
                        STR_TO_DATE(TRIM({dateColumn}), '%m/%d/%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
                        STR_TO_DATE(TRIM({dateColumn}), '%d-%m-%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
                        STR_TO_DATE(TRIM({dateColumn}), '%Y-%m-%d') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d')
                    )");
                            cmdParams.Add(new MySqlParameter("@sqlFrom", sqlFrom));
                            cmdParams.Add(new MySqlParameter("@sqlTo", sqlTo));
                        }
                    }

                    // Sanitization helpers matching other functional modules
                    string cleanRev = "CAST(NULLIF(REPLACE(REPLACE(TRIM(PostedInvoices), ',', ''), '$', ''), '') AS DECIMAL(18,2))";
                    string cleanCost = "ABS(CAST(NULLIF(REPLACE(REPLACE(TRIM(PostedCost), ',', ''), '$', ''), '') AS DECIMAL(18,2)))";

                    // Main KPI Metrics Query
                    string metricsQuery = $@"SELECT 
                COUNT(DISTINCT BookingNo), 
                COALESCE(SUM({cleanRev}), 0),
                COALESCE(SUM({cleanCost}), 0)
                FROM Ocean_Revenue_Analyzer_New {whereClause}";

                    using (var cmd = new MySqlCommand(metricsQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                model.TotalShipments = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                                model.TotalPaidRevenue = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1);
                                model.TotalCost = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
                                model.TotalGrossProfit = model.TotalPaidRevenue - model.TotalCost;
                            }
                        }
                    }

                    // Sales Chart Query
                    string salesChartQuery = $@"SELECT COALESCE(NULLIF(TRIM(Salesperson), ''), 'Unassigned') as SalesName, 
                COALESCE(SUM({cleanRev}), 0) as rev 
                FROM Ocean_Revenue_Analyzer_New {whereClause} GROUP BY SalesName ORDER BY rev DESC LIMIT 8";

                    using (var cmd = new MySqlCommand(salesChartQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                model.ShipmentsBySalesPerson[reader.GetString(0)] = (int)Math.Round(reader.GetDecimal(1));
                            }
                        }
                    }

                    // Transaction Ledger Table Data Query
                    string tableQuery = $"SELECT BookingNo as ShipmentNo, Customer, Station, Type, Salesperson as SalesPerson, ActualGP, PostedCost, PostedInvoices, DatePosted FROM Ocean_Revenue_Analyzer_New {whereClause}";
                    using (var cmd = new MySqlCommand(tableQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            model.ShipmentData.Load(reader);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }

            return View(model);
        }

        [HttpGet]
        public ActionResult UserPerformance(string selectedStation = null, string selectedCustomer = null, string selectedFileStatus = null, string selectedCreatedBy = null, string selectedSalesPerson = null, string fromDate = null, string toDate = null, string dateType = null)
        {
            if (string.IsNullOrEmpty(selectedStation) || selectedStation == "-- All Companies --" || selectedStation == "-- All Stations --") selectedStation = null;
            if (string.IsNullOrEmpty(selectedCustomer) || selectedCustomer == "-- All Customers --") selectedCustomer = null;
            if (string.IsNullOrEmpty(selectedFileStatus) || selectedFileStatus == "-- All Statuses --") selectedFileStatus = null;
            if (string.IsNullOrEmpty(selectedCreatedBy) || selectedCreatedBy == "-- All Users --") selectedCreatedBy = null;
            if (string.IsNullOrEmpty(selectedSalesPerson) || selectedSalesPerson == "-- All Sales Persons --") selectedSalesPerson = null;

            if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate))
            {
                var now = DateTime.Now;
                fromDate = string.IsNullOrEmpty(fromDate) ? new DateTime(now.Year, now.Month, 1).ToString("yyyy-MM-dd") : fromDate;
                toDate = string.IsNullOrEmpty(toDate) ? now.ToString("yyyy-MM-dd") : toDate;
            }

            var model = new OceanShipmentDashboardViewModel
            {
                SelectedStation = selectedStation,
                SelectedCustomer = selectedCustomer,
                SelectedFileStatus = selectedFileStatus,
                SelectedCreatedBy = selectedCreatedBy,
                SelectedSalesPerson = selectedSalesPerson,
                FromDate = fromDate,
                ToDate = toDate,
                DateType = string.IsNullOrEmpty(dateType) ? "ets" : dateType
            };
            model.DataAvailableSince = GetDataAvailableSince(model.DateType);

            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();
                    LoadDropdownFilters(conn, model);

                    var whereClause = new StringBuilder(" WHERE 1=1");
                    var cmdParams = new List<MySqlParameter>();

                    if (!string.IsNullOrEmpty(selectedStation))
                    {
                        if (selectedStation == "Gillespie-Munro Canada")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                        }
                        else if (selectedStation == "Gillespie-Munro USA")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO (USA) INC.%'");
                        }
                        else if (selectedStation == "Gillship Navigation")
                        {
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
                        }
                        else
                        {
                            whereClause.Append($" AND {GetGroupedStationSql("Station")} = @station");
                            cmdParams.Add(new MySqlParameter("@station", selectedStation));
                        }
                    }

                    if (!string.IsNullOrEmpty(selectedCustomer))
                    {
                        whereClause.Append(" AND TRIM(Customer) = @customer");
                        cmdParams.Add(new MySqlParameter("@customer", selectedCustomer.Trim()));
                    }

                    if (!string.IsNullOrEmpty(selectedFileStatus))
                    {
                        whereClause.Append(" AND TRIM(FileStatus) = @fileStatus");
                        cmdParams.Add(new MySqlParameter("@fileStatus", selectedFileStatus.Trim()));
                    }

                    if (!string.IsNullOrEmpty(selectedCreatedBy))
                    {
                        whereClause.Append(" AND TRIM(CreatedBy) = @createdBy");
                        cmdParams.Add(new MySqlParameter("@createdBy", selectedCreatedBy.Trim()));
                    }

                    if (!string.IsNullOrEmpty(selectedSalesPerson))
                    {
                        whereClause.Append(" AND TRIM(Salesperson) = @salesPerson");
                        cmdParams.Add(new MySqlParameter("@salesPerson", selectedSalesPerson.Trim()));
                    }

                    string sqlFrom = null;
                    string sqlTo = null;
                    if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
                    {
                        string dateColumn = "ETS";
                        string dtLower = model.DateType?.ToLower();
                        if (dtLower == "eta") dateColumn = "ETA";
                        else if (dtLower == "posted" || dtLower == "dateposted") dateColumn = "DatePosted";

                        if (DateTime.TryParse(fromDate, out DateTime parsedFrom) && DateTime.TryParse(toDate, out DateTime parsedTo))
                        {
                            sqlFrom = parsedFrom.ToString("yyyy-MM-dd");
                            sqlTo = parsedTo.ToString("yyyy-MM-dd");

                            whereClause.Append($@" AND (
                                STR_TO_DATE(TRIM({dateColumn}), '%m/%d/%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
                                STR_TO_DATE(TRIM({dateColumn}), '%d-%m-%Y') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d') OR 
                                STR_TO_DATE(TRIM({dateColumn}), '%Y-%m-%d') BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d')
                            )");
                            cmdParams.Add(new MySqlParameter("@sqlFrom", sqlFrom));
                            cmdParams.Add(new MySqlParameter("@sqlTo", sqlTo));
                        }
                    }

                    string metricsQuery = $@"SELECT COUNT(DISTINCT BookingNo) FROM Ocean_Revenue_Analyzer_New {whereClause}";
                    using (var cmd = new MySqlCommand(metricsQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        object result = cmd.ExecuteScalar();
                        model.TotalShipments = result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
                    }

                    string chartQuery = $@"SELECT COALESCE(NULLIF(TRIM(CreatedBy), ''), 'Unassigned') as UserName, 
                        COUNT(DISTINCT BookingNo) as fileCount 
                        FROM Ocean_Revenue_Analyzer_New {whereClause} GROUP BY UserName ORDER BY fileCount DESC LIMIT 8";

                    using (var cmd = new MySqlCommand(chartQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                model.ShipmentsByUser[reader.GetString(0)] = reader.GetInt32(1);
                            }
                        }
                    }

                    model.OperatorFinancialData = new DataTable();
                    string operatorWorkloadQuery = $@"SELECT 
                        COALESCE(NULLIF(TRIM(CreatedBy), ''), 'Unassigned') as Operator,
                        COUNT(DISTINCT BookingNo) as FilesHandled,
                        SUM(CASE WHEN LOWER(TRIM(FileStatus)) = 'active' OR LOWER(TRIM(FileStatus)) LIKE '%open%' THEN 1 ELSE 0 END) as ActiveFiles,
                        SUM(CASE WHEN LOWER(TRIM(FileStatus)) = 'closed' OR LOWER(TRIM(FileStatus)) LIKE '%posted%' THEN 1 ELSE 0 END) as ClosedFiles
                        FROM Ocean_Revenue_Analyzer_New {whereClause} GROUP BY Operator ORDER BY FilesHandled DESC LIMIT 10";

                    using (var cmd = new MySqlCommand(operatorWorkloadQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            model.OperatorFinancialData.Load(reader);
                        }
                    }

                    string tableQuery = $"SELECT BookingNo as ShipmentNo, Customer, Station, FileStatus, CreatedBy, Salesperson as SalesPerson, ActualGP, DatePosted FROM Ocean_Revenue_Analyzer_New {whereClause}";
                    using (var cmd = new MySqlCommand(tableQuery, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var reader = cmd.ExecuteReader())
                        {
                            model.ShipmentData.Load(reader);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }

            return View(model);
        }

        private void LoadAllChartsData(MySqlConnection conn, OceanShipmentDashboardViewModel model, string whereClause, List<MySqlParameter> cmdParams)
        {
            model.ShipmentsByStation.Clear();
            string stationQuery = $@"SELECT {GetGroupedStationSql("Station")} as Stn, COUNT(*) as cnt 
                                   FROM Ocean_Revenue_Analyzer_New {whereClause} 
                                   GROUP BY Stn ORDER BY cnt DESC LIMIT 6";
            using (var cmd = new MySqlCommand(stationQuery, conn))
            {
                foreach (var p in cmdParams) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read()) model.ShipmentsByStation[rdr.GetString(0)] = rdr.GetInt32(1);
                }
            }

            model.ShipmentsByUser.Clear();
            string userQuery = $@"SELECT COALESCE(NULLIF(TRIM(Createdby), ''), 'Unknown') as Usr, COUNT(*) as cnt 
                                FROM Ocean_Revenue_Analyzer_New {whereClause} 
                                GROUP BY Usr ORDER BY cnt DESC LIMIT 6";
            using (var cmd = new MySqlCommand(userQuery, conn))
            {
                foreach (var p in cmdParams) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read()) model.ShipmentsByUser[rdr.GetString(0)] = rdr.GetInt32(1);
                }
            }

            model.ShipmentsBySalesPerson.Clear();
            string salesQuery = $@"SELECT COALESCE(NULLIF(TRIM(Salesperson), ''), 'Unassigned') as Sp, 
                                   COALESCE(SUM(CAST(NULLIF(TRIM(PostedInvoices), '') AS DECIMAL(18,2))), 0) as totalAmount 
                                   FROM Ocean_Revenue_Analyzer_New {whereClause} 
                                   GROUP BY Sp ORDER BY totalAmount DESC LIMIT 6";
            using (var cmd = new MySqlCommand(salesQuery, conn))
            {
                foreach (var p in cmdParams) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read()) model.ShipmentsBySalesPerson[rdr.GetString(0)] = Convert.ToInt32(rdr.GetDecimal(1));
                }
            }

            model.TopCustomers.Clear();
            string custQuery = $@"SELECT COALESCE(NULLIF(TRIM(Customer), ''), 'Unknown') as Cust, 
                                COALESCE(SUM(CAST(NULLIF(TRIM(PostedInvoices), '') AS DECIMAL(18,2))), 0) as custRevenue 
                                FROM Ocean_Revenue_Analyzer_New {whereClause} 
                                GROUP BY Cust ORDER BY custRevenue DESC LIMIT 6";
            using (var cmd = new MySqlCommand(custQuery, conn))
            {
                foreach (var p in cmdParams) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read()) model.TopCustomers[rdr.GetString(0)] = Convert.ToInt32(rdr.GetDecimal(1));
                }
            }
        }

        //private void LoadAllChartsData(MySqlConnection conn, OceanShipmentDashboardViewModel model, string whereClause, List<MySqlParameter> cmdParams)
        //{
        //    model.ShipmentsByStation.Clear();
        //    string stationQuery = $@"SELECT {GetGroupedStationSql("Station")} as Stn, COUNT(*) as cnt 
        //                           FROM Ocean_Revenue_Analyzer_New {whereClause} 
        //                           GROUP BY Stn ORDER BY cnt DESC LIMIT 6";
        //    using (var cmd = new MySqlCommand(stationQuery, conn))
        //    {
        //        foreach (var p in cmdParams) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
        //        using (var rdr = cmd.ExecuteReader())
        //        {
        //            while (rdr.Read()) model.ShipmentsByStation[rdr.GetString(0)] = rdr.GetInt32(1);
        //        }
        //    }

        //    model.ShipmentsByUser.Clear();
        //    string userQuery = $@"SELECT COALESCE(NULLIF(TRIM(Createdby), ''), 'Unknown') as Usr, COUNT(*) as cnt 
        //                        FROM Ocean_Revenue_Analyzer_New {whereClause} 
        //                        GROUP BY Usr ORDER BY cnt DESC LIMIT 6";
        //    using (var cmd = new MySqlCommand(userQuery, conn))
        //    {
        //        foreach (var p in cmdParams) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
        //        using (var rdr = cmd.ExecuteReader())
        //        {
        //            while (rdr.Read()) model.ShipmentsByUser[rdr.GetString(0)] = rdr.GetInt32(1);
        //        }
        //    }

        //    model.ShipmentsBySalesPerson.Clear();
        //    string salesQuery = $@"SELECT COALESCE(NULLIF(TRIM(Salesperson), ''), 'Unassigned') as Sp, 
        //                           COALESCE(SUM(CAST(NULLIF(TRIM(PostedInvoices), '') AS DECIMAL(18,2)))), 0) as totalAmount 
        //                           FROM Ocean_Revenue_Analyzer_New {whereClause} 
        //                           GROUP BY Sp ORDER BY totalAmount DESC LIMIT 6";
        //    using (var cmd = new MySqlCommand(salesQuery, conn))
        //    {
        //        foreach (var p in cmdParams) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
        //        using (var rdr = cmd.ExecuteReader())
        //        {
        //            while (rdr.Read()) model.ShipmentsBySalesPerson[rdr.GetString(0)] = Convert.ToInt32(rdr.GetDecimal(1));
        //        }
        //    }

        //    model.TopCustomers.Clear();
        //    string custQuery = $@"SELECT COALESCE(NULLIF(TRIM(Customer), ''), 'Unknown') as Cust, 
        //                        COALESCE(SUM(CAST(NULLIF(TRIM(PostedInvoices), '') AS DECIMAL(18,2)))), 0) as custRevenue 
        //                        FROM Ocean_Revenue_Analyzer_New {whereClause} 
        //                        GROUP BY Cust ORDER BY custRevenue DESC LIMIT 6";
        //    using (var cmd = new MySqlCommand(custQuery, conn))
        //    {
        //        foreach (var p in cmdParams) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
        //        using (var rdr = cmd.ExecuteReader())
        //        {
        //            while (rdr.Read()) model.TopCustomers[rdr.GetString(0)] = Convert.ToInt32(rdr.GetDecimal(1));
        //        }
        //    }
        //}

        private void LoadDropdownFilters(MySqlConnection conn, OceanShipmentDashboardViewModel model)
        {
            using (var cmd = new MySqlCommand($"SELECT DISTINCT {GetGroupedStationSql("Station")} AS StationName FROM Ocean_Revenue_Analyzer_New WHERE Station IS NOT NULL AND Station != '' ORDER BY StationName", conn))
            using (var rdr = cmd.ExecuteReader()) { while (rdr.Read()) model.AvailableStations.Add(rdr.GetString(0)); }

            using (var cmd = new MySqlCommand("SELECT DISTINCT FileStatus FROM Ocean_Revenue_Analyzer_New WHERE FileStatus IS NOT NULL ORDER BY FileStatus", conn))
            using (var rdr = cmd.ExecuteReader()) { while (rdr.Read()) model.AvailableFileStatuses.Add(rdr.GetString(0)); }

            using (var cmd = new MySqlCommand("SELECT DISTINCT Createdby FROM Ocean_Revenue_Analyzer_New WHERE Createdby IS NOT NULL AND Createdby != '' ORDER BY Createdby", conn))
            using (var rdr = cmd.ExecuteReader()) { while (rdr.Read()) model.AvailableCreatedBy.Add(rdr.GetString(0)); }

            var custQuery = new StringBuilder("SELECT DISTINCT Customer FROM Ocean_Revenue_Analyzer_New WHERE Customer IS NOT NULL AND Customer != ''");
            var custParams = new List<MySqlParameter>();

            if (!string.IsNullOrEmpty(model.SelectedStation) && model.SelectedStation != "-- All Stations --" && model.SelectedStation != "-- All Companies --")
            {
                if (model.SelectedStation == "Gillespie-Munro Canada")
                {
                    custQuery.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                }
                else if (model.SelectedStation == "Gillespie-Munro USA")
                {
                    custQuery.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO (USA) INC.%'");
                }
                else if (model.SelectedStation == "Gillship Navigation")
                {
                    custQuery.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
                }
                else
                {
                    custQuery.Append($" AND {GetGroupedStationSql("Station")} = @dropStation");
                    custParams.Add(new MySqlParameter("@dropStation", model.SelectedStation));
                }
            }
            custQuery.Append(" ORDER BY Customer LIMIT 100");

            using (var cmd = new MySqlCommand(custQuery.ToString(), conn))
            {
                if (custParams.Count > 0) cmd.Parameters.AddRange(custParams.ToArray());
                using (var rdr = cmd.ExecuteReader()) { while (rdr.Read()) model.AvailableCustomers.Add(rdr.GetString(0)); }
            }

            try
            {
                var salesProp = model.GetType().GetProperty("AvailableSalesPersons");
                if (salesProp != null)
                {
                    var salesList = salesProp.GetValue(model) as List<string>;
                    if (salesList != null)
                    {
                        var salesQuery = new StringBuilder("SELECT DISTINCT Salesperson FROM Ocean_Revenue_Analyzer_New WHERE Salesperson IS NOT NULL AND Salesperson != ''");
                        var salesParams = new List<MySqlParameter>();

                        if (!string.IsNullOrEmpty(model.SelectedStation) && model.SelectedStation != "-- All Stations --" && model.SelectedStation != "-- All Companies --")
                        {
                            if (model.SelectedStation == "Gillespie-Munro Canada")
                            {
                                salesQuery.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                            }
                            else if (model.SelectedStation == "Gillespie-Munro USA")
                            {
                                salesQuery.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO (USA) INC.%'");
                            }
                            else if (model.SelectedStation == "Gillship Navigation")
                            {
                                salesQuery.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
                            }
                            else
                            {
                                salesQuery.Append($" AND {GetGroupedStationSql("Station")} = @dropSalesStation");
                                salesParams.Add(new MySqlParameter("@dropSalesStation", model.SelectedStation));
                            }
                        }
                        salesQuery.Append(" ORDER BY Salesperson LIMIT 100");

                        using (var cmd = new MySqlCommand(salesQuery.ToString(), conn))
                        {
                            if (salesParams.Count > 0) cmd.Parameters.AddRange(salesParams.ToArray());
                            using (var rdr = cmd.ExecuteReader()) { while (rdr.Read()) salesList.Add(rdr.GetString(0)); }
                        }
                    }
                }
            }
            catch { }
        }

        [HttpGet]
        public JsonResult GetCustomersByStation(string station)
        {
            var customers = new List<string>();
            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();
                    var query = new StringBuilder("SELECT DISTINCT Customer FROM Ocean_Revenue_Analyzer_New WHERE Customer IS NOT NULL AND Customer != ''");
                    var cmdParams = new List<MySqlParameter>();

                    if (!string.IsNullOrEmpty(station) && station != "-- All Stations --" && station != "-- All Companies --")
                    {
                        if (station == "Gillespie-Munro Canada")
                        {
                            query.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                        }
                        else if (station == "Gillespie-Munro USA")
                        {
                            query.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO (USA) INC.%'");
                        }
                        else if (station == "Gillship Navigation")
                        {
                            query.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
                        }
                        else
                        {
                            query.Append($" AND {GetGroupedStationSql("Station")} = @station");
                            cmdParams.Add(new MySqlParameter("@station", station));
                        }
                    }
                    query.Append(" ORDER BY Customer LIMIT 100");

                    using (var cmd = new MySqlCommand(query.ToString(), conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var rdr = cmd.ExecuteReader())
                        {
                            while (rdr.Read()) customers.Add(rdr.GetString(0));
                        }
                    }
                }
            }
            catch (Exception) { }

            return Json(customers, JsonRequestBehavior.AllowGet);
        }

        [HttpGet]
        public JsonResult GetSalesPersonsByStation(string station)
        {
            var salesPersons = new List<string>();
            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();
                    var query = new StringBuilder("SELECT DISTINCT Salesperson FROM Ocean_Revenue_Analyzer_New WHERE Salesperson IS NOT NULL AND Salesperson != ''");
                    var cmdParams = new List<MySqlParameter>();

                    if (!string.IsNullOrEmpty(station) && station != "-- All Stations --" && station != "-- All Companies --")
                    {
                        if (station == "Gillespie-Munro Canada")
                        {
                            query.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                        }
                        else if (station == "Gillespie-Munro USA")
                        {
                            query.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO (USA) INC.%'");
                        }
                        else if (station == "Gillship Navigation")
                        {
                            query.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
                        }
                        else
                        {
                            query.Append($" AND {GetGroupedStationSql("Station")} = @station");
                            cmdParams.Add(new MySqlParameter("@station", station));
                        }
                    }
                    query.Append(" ORDER BY Salesperson LIMIT 100");

                    using (var cmd = new MySqlCommand(query.ToString(), conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var rdr = cmd.ExecuteReader())
                        {
                            while (rdr.Read()) salesPersons.Add(rdr.GetString(0));
                        }
                    }
                }
            }
            catch (Exception) { }

            return Json(salesPersons, JsonRequestBehavior.AllowGet);
        }

        [HttpPost]
        public ActionResult UploadNewRevenueAnalyzer(HttpPostedFileBase summaryFile, HttpPostedFileBase detailFile)
        {
            if (summaryFile != null && summaryFile.ContentLength > 0 && detailFile != null && detailFile.ContentLength > 0)
            {
                try
                {
                    DataTable dtDetail = new DataTable();
                    using (var stream = detailFile.InputStream)
                    {
                        using (var reader = ExcelDataReader.ExcelReaderFactory.CreateReader(stream))
                        {
                            var result = reader.AsDataSet(new ExcelDataReader.ExcelDataSetConfiguration()
                            {
                                ConfigureDataTable = (_) => new ExcelDataReader.ExcelDataTableConfiguration()
                                {
                                    UseHeaderRow = true
                                }
                            });
                            dtDetail = result.Tables[0];
                        }
                    }

                    DataTable dtSummary = new DataTable();
                    using (var stream = summaryFile.InputStream)
                    {
                        using (var reader = ExcelDataReader.ExcelReaderFactory.CreateReader(stream))
                        {
                            var result = reader.AsDataSet(new ExcelDataReader.ExcelDataSetConfiguration()
                            {
                                ConfigureDataTable = (_) => new ExcelDataReader.ExcelDataTableConfiguration()
                                {
                                    UseHeaderRow = true
                                }
                            });
                            dtSummary = result.Tables[0];
                        }
                    }

                    using (var conn = new MySqlConnection(connStr))
                    {
                        conn.Open();

                        // REMOVED TRUNCATE: We now preserve historical data and use Upsert logic instead.

                        // 1. Process Detail Data with Upsert (Matched on BookingNo and BillCode/Reference if needed, or appended)
                        foreach (DataRow detailRow in dtDetail.Rows)
                        {
                            string bNo = "";
                            if (detailRow.Table.Columns.Contains("Booking No.:") && detailRow["Booking No.:"] != DBNull.Value)
                                bNo = detailRow["Booking No.:"].ToString().Trim();
                            else if (detailRow.Table.Columns.Contains("Booking No.") && detailRow["Booking No."] != DBNull.Value)
                                bNo = detailRow["Booking No."].ToString().Trim();
                            else if (detailRow.ItemArray.Length > 0 && detailRow[0] != DBNull.Value)
                                bNo = detailRow[0].ToString().Trim();

                            if (string.IsNullOrEmpty(bNo)) continue;

                            string insertDetailQuery = @"INSERT INTO Ocean_Detail_Dates 
                    (BookingNo, MasterBookingNo, Station, Currency, Customer, Shipper, Consignee, Salesperson, ETD_ETA, Origin, Destination, PCS, Weight, UoM, Measure, UoM1, BillCode, BillDescr, Name, Reference, UnPostCost, PostedCost, Variance, UnPostedInvoice, PostedInvoice, EffExchRate, DatePosted, Createdby, Closedby) 
                    VALUES 
                    (@BookingNo, @MasterBookingNo, @Station, @Currency, @Customer, @Shipper, @Consignee, @Salesperson, @ETD_ETA, @Origin, @Destination, @PCS, @Weight, @UoM, @Measure, @UoM1, @BillCode, @BillDescr, @Name, @Reference, @UnPostCost, @PostedCost, @Variance, @UnPostedInvoice, @PostedInvoice, @EffExchRate, @DatePosted, @Createdby, @Closedby)";

                            using (var cmdD = new MySqlCommand(insertDetailQuery, conn))
                            {
                                object GetDetVal(string colName) => detailRow.Table.Columns.Contains(colName) && detailRow[colName] != DBNull.Value ? detailRow[colName] : DBNull.Value;
                                decimal ParseDetDec(string colName)
                                {
                                    if (detailRow.Table.Columns.Contains(colName) && detailRow[colName] != DBNull.Value)
                                    {
                                        string valStr = detailRow[colName].ToString().Replace(",", "").Trim();
                                        if (decimal.TryParse(valStr, out decimal parsed)) return parsed;
                                    }
                                    return 0;
                                }

                                cmdD.Parameters.AddWithValue("@BookingNo", bNo);
                                cmdD.Parameters.AddWithValue("@MasterBookingNo", GetDetVal("Master Booking No.:"));
                                cmdD.Parameters.AddWithValue("@Station", GetDetVal("STATION:"));
                                cmdD.Parameters.AddWithValue("@Currency", GetDetVal("CURRENCY:"));
                                cmdD.Parameters.AddWithValue("@Customer", GetDetVal("Customer:  "));
                                cmdD.Parameters.AddWithValue("@Shipper", GetDetVal("Shipper:  "));
                                cmdD.Parameters.AddWithValue("@Consignee", GetDetVal("Consignee:"));
                                cmdD.Parameters.AddWithValue("@Salesperson", GetDetVal("Salesperson"));
                                cmdD.Parameters.AddWithValue("@ETD_ETA", GetDetVal(" ETD / ETA:"));
                                cmdD.Parameters.AddWithValue("@Origin", GetDetVal("Origin:"));
                                cmdD.Parameters.AddWithValue("@Destination", GetDetVal("Destination:"));
                                cmdD.Parameters.AddWithValue("@PCS", ParseDetDec("PCS:"));
                                cmdD.Parameters.AddWithValue("@Weight", ParseDetDec("Weight:"));
                                cmdD.Parameters.AddWithValue("@UoM", GetDetVal("UoM"));
                                cmdD.Parameters.AddWithValue("@Measure", ParseDetDec("Measure:"));
                                cmdD.Parameters.AddWithValue("@UoM1", GetDetVal("UoM.1"));
                                cmdD.Parameters.AddWithValue("@BillCode", GetDetVal("Bill code"));
                                cmdD.Parameters.AddWithValue("@BillDescr", GetDetVal("Bill Descr"));
                                cmdD.Parameters.AddWithValue("@Name", GetDetVal("Name"));
                                cmdD.Parameters.AddWithValue("@Reference", GetDetVal("Reference"));
                                cmdD.Parameters.AddWithValue("@UnPostCost", ParseDetDec("UnPost Cost"));
                                cmdD.Parameters.AddWithValue("@PostedCost", ParseDetDec("Posted Cost"));
                                cmdD.Parameters.AddWithValue("@Variance", ParseDetDec("Variance"));
                                cmdD.Parameters.AddWithValue("@UnPostedInvoice", ParseDetDec("UnPosted Invoice"));
                                cmdD.Parameters.AddWithValue("@PostedInvoice", ParseDetDec("Posted Invoice"));
                                cmdD.Parameters.AddWithValue("@EffExchRate", ParseDetDec("Eff Exch Rate"));
                                cmdD.Parameters.AddWithValue("@DatePosted", GetDetVal("Date Posted"));
                                cmdD.Parameters.AddWithValue("@Createdby", GetDetVal("Createdby"));
                                cmdD.Parameters.AddWithValue("@Closedby", GetDetVal("Closedby"));

                                cmdD.ExecuteNonQuery();
                            }
                        }

                        // 2. Process Summary Data with ON DUPLICATE KEY UPDATE (Preserves old bookings, updates changed ones)
                        int processedCount = 0;
                        foreach (DataRow row in dtSummary.Rows)
                        {
                            string bookingNo = "";
                            if (dtSummary.Columns.Contains("Booking No.") && row["Booking No."] != DBNull.Value)
                                bookingNo = row["Booking No."].ToString().Trim();
                            else if (row.ItemArray.Length > 0 && row[0] != DBNull.Value)
                                bookingNo = row[0].ToString().Trim();

                            if (string.IsNullOrEmpty(bookingNo)) continue;

                            string upsertQuery = @"INSERT INTO Ocean_Revenue_Analyzer_New 
                    (BookingNo, Station, Currency, Customer, Shipper, Consignee, Salesperson, Origin, Destination, ETS, ETA, PortOfLoading, LoadPortCountry, DestinationPort, DestinationCountry, NoPcs, Weight, ChargeableWeightVolume, UnmatchedUnPostedCost, PostedCost, Variance, UnPostedInvoices, PostedInvoices, EstimatedGP, ActualGP, ProfitLossPct, FileStatus, Createdby, Closedby, Mode, Type, CreatedOn) 
                    VALUES 
                    (@BookingNo, @Station, @Currency, @Customer, @Shipper, @Consignee, @Salesperson, @Origin, @Destination, @ETS, @ETA, @PortOfLoading, @LoadPortCountry, @DestinationPort, @DestinationCountry, @NoPcs, @Weight, @ChargeableWeightVolume, @UnmatchedUnPostedCost, @PostedCost, @Variance, @UnPostedInvoices, @PostedInvoices, @EstimatedGP, @ActualGP, @ProfitLossPct, @FileStatus, @Createdby, @Closedby, @Mode, @Type, NOW())
                    ON DUPLICATE KEY UPDATE 
                    Station = VALUES(Station),
                    Currency = VALUES(Currency),
                    Customer = VALUES(Customer),
                    Salesperson = VALUES(Salesperson),
                    PostedCost = VALUES(PostedCost),
                    PostedInvoices = VALUES(PostedInvoices),
                    ActualGP = VALUES(ActualGP),
                    FileStatus = VALUES(FileStatus),
                    Closedby = VALUES(Closedby);";

                            using (var cmd = new MySqlCommand(upsertQuery, conn))
                            {
                                object GetColVal(string colName) => dtSummary.Columns.Contains(colName) && row[colName] != DBNull.Value ? row[colName] : DBNull.Value;
                                decimal ParseDecimal(string colName)
                                {
                                    if (dtSummary.Columns.Contains(colName) && row[colName] != DBNull.Value)
                                    {
                                        string valStr = row[colName].ToString().Replace(",", "").Trim();
                                        if (decimal.TryParse(valStr, out decimal parsed)) return parsed;
                                    }
                                    return 0;
                                }

                                cmd.Parameters.AddWithValue("@BookingNo", bookingNo);
                                cmd.Parameters.AddWithValue("@Station", GetColVal("Station"));
                                cmd.Parameters.AddWithValue("@Currency", GetColVal("Currency"));
                                cmd.Parameters.AddWithValue("@Customer", GetColVal("Customer"));
                                cmd.Parameters.AddWithValue("@Shipper", GetColVal("Shipper"));
                                cmd.Parameters.AddWithValue("@Consignee", GetColVal("Consignee"));
                                cmd.Parameters.AddWithValue("@Salesperson", GetColVal("Salesperson"));
                                cmd.Parameters.AddWithValue("@Origin", GetColVal("Origin"));
                                cmd.Parameters.AddWithValue("@Destination", GetColVal("Destination"));
                                cmd.Parameters.AddWithValue("@ETS", GetColVal("etd"));
                                cmd.Parameters.AddWithValue("@ETA", GetColVal("eta"));
                                cmd.Parameters.AddWithValue("@PortOfLoading", GetColVal("Port of Loading"));
                                cmd.Parameters.AddWithValue("@LoadPortCountry", GetColVal("Load Port Country"));
                                cmd.Parameters.AddWithValue("@DestinationPort", GetColVal("Destination Port "));
                                cmd.Parameters.AddWithValue("@DestinationCountry", GetColVal("Destination Country"));
                                cmd.Parameters.AddWithValue("@NoPcs", ParseDecimal("No. Pcs"));
                                cmd.Parameters.AddWithValue("@Weight", GetColVal("Weight"));
                                cmd.Parameters.AddWithValue("@ChargeableWeightVolume", GetColVal("Chargeable Weight/Volume"));
                                cmd.Parameters.AddWithValue("@UnmatchedUnPostedCost", ParseDecimal("Unmatched/ UnPosted/ Cost"));
                                cmd.Parameters.AddWithValue("@PostedCost", ParseDecimal("Posted Cost"));
                                cmd.Parameters.AddWithValue("@Variance", ParseDecimal("Variance"));
                                cmd.Parameters.AddWithValue("@UnPostedInvoices", ParseDecimal("UnPosted Invoices"));
                                cmd.Parameters.AddWithValue("@PostedInvoices", ParseDecimal("Posted Invoices"));
                                cmd.Parameters.AddWithValue("@EstimatedGP", ParseDecimal("Estimated GP"));
                                cmd.Parameters.AddWithValue("@ActualGP", ParseDecimal("Actual GP"));
                                cmd.Parameters.AddWithValue("@ProfitLossPct", GetColVal("Profit/Loss %"));
                                cmd.Parameters.AddWithValue("@FileStatus", GetColVal("File Status"));
                                cmd.Parameters.AddWithValue("@Createdby", GetColVal("Createdby"));
                                cmd.Parameters.AddWithValue("@Closedby", GetColVal("Closedby"));
                                cmd.Parameters.AddWithValue("@Mode", GetColVal("Mode"));
                                cmd.Parameters.AddWithValue("@Type", GetColVal("Type"));

                                cmd.ExecuteNonQuery();
                                processedCount++;
                            }
                        }

                        // 3. Synchronize Date Posted
                        string updateQuery = @"
                UPDATE Ocean_Revenue_Analyzer_New m
                JOIN (
                    SELECT BookingNo, MAX(DatePosted) as DatePosted 
                    FROM Ocean_Detail_Dates 
                    WHERE DatePosted IS NOT NULL AND DatePosted != '' 
                    GROUP BY BookingNo
                ) d ON m.BookingNo = d.BookingNo
                SET m.DatePosted = d.DatePosted;";

                        using (var cmdUpdate = new MySqlCommand(updateQuery, conn))
                        {
                            cmdUpdate.ExecuteNonQuery();
                        }

                        TempData["UploadMessage"] = $"Successfully merged/added records. Processed {processedCount} summary entries while retaining historical data.";
                    }
                }
                catch (Exception ex)
                {
                    TempData["ErrorMessage"] = "Error importing files: " + ex.Message;
                }
            }
            else
            {
                TempData["ErrorMessage"] = "Please select both the Summary and Detail files.";
            }
            return RedirectToAction("Ocean_Dashboard");
        }
    }
}