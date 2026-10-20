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

        // CBM volume from the "Chargeable Weight/Volume" text (e.g. "79.49 CBM"). CBF is converted
        // to CBM; any other unit counts as 0 so weights are never mixed into the volume total.
        private const string CbmSql = @"(CASE
                WHEN UPPER(TRIM(ChargeableWeightVolume)) LIKE '%CBM' THEN CAST(NULLIF(REPLACE(SUBSTRING_INDEX(TRIM(ChargeableWeightVolume), ' ', 1), ',', ''), '') AS DECIMAL(18,3))
                WHEN UPPER(TRIM(ChargeableWeightVolume)) LIKE '%CBF' THEN CAST(NULLIF(REPLACE(SUBSTRING_INDEX(TRIM(ChargeableWeightVolume), ' ', 1), ',', ''), '') AS DECIMAL(18,3)) * 0.0283168
                ELSE 0 END)";

        private DataTable RunTable(MySqlConnection conn, string sql, List<MySqlParameter> cmdParams)
        {
            var table = new DataTable();
            using (var cmd = new MySqlCommand(sql, conn))
            {
                foreach (var p in cmdParams) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                using (var reader = cmd.ExecuteReader()) table.Load(reader);
            }
            return table;
        }

        [HttpGet]
        public ActionResult CbmAnalysis(string selectedStation = null, string selectedCustomer = null, string selectedSalesPerson = null, string fromDate = null, string toDate = null, string dateType = null)
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
                DateType = string.IsNullOrEmpty(dateType) ? "ets" : dateType,
                CbmPeriodLabel = "Month"
            };
            model.DataAvailableSince = GetDataAvailableSince(model.DateType);

            string dateColumn = "ETS";
            string dtLower = model.DateType.ToLower();
            if (dtLower == "eta") dateColumn = "ETA";
            else if (dtLower == "posted" || dtLower == "dateposted") dateColumn = "DatePosted";

            string parsedDate = $@"COALESCE(
                STR_TO_DATE(TRIM({dateColumn}), '%m/%d/%Y'),
                STR_TO_DATE(TRIM({dateColumn}), '%d-%m-%Y'),
                STR_TO_DATE(TRIM({dateColumn}), '%Y-%m-%d'))";

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
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                        else if (selectedStation == "Gillespie-Munro USA")
                            whereClause.Append(" AND UPPER(TRIM(Station)) = 'GILLESPIE-MUNRO (USA) INC.'");
                        else if (selectedStation == "Gillship Navigation")
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
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

                    if (DateTime.TryParse(fromDate, out DateTime parsedFrom) && DateTime.TryParse(toDate, out DateTime parsedTo))
                    {
                        whereClause.Append($" AND {parsedDate} BETWEEN STR_TO_DATE(@sqlFrom, '%Y-%m-%d') AND STR_TO_DATE(@sqlTo, '%Y-%m-%d')");
                        cmdParams.Add(new MySqlParameter("@sqlFrom", parsedFrom.ToString("yyyy-MM-dd")));
                        cmdParams.Add(new MySqlParameter("@sqlTo", parsedTo.ToString("yyyy-MM-dd")));

                        // Short ranges read better day by day; longer ones month by month.
                        if ((parsedTo - parsedFrom).TotalDays <= 62) model.CbmPeriodLabel = "Day";
                    }

                    var totals = RunTable(conn, $@"SELECT
                        COUNT(DISTINCT BookingNo) AS Shipments,
                        COALESCE(SUM({CbmSql}), 0) AS Cbm,
                        COUNT(DISTINCT CASE WHEN {CbmSql} > 0 THEN BookingNo END) AS WithCbm
                        FROM Ocean_Revenue_Analyzer_New {whereClause}", cmdParams);
                    if (totals.Rows.Count > 0)
                    {
                        model.TotalShipments = Convert.ToInt32(totals.Rows[0]["Shipments"]);
                        model.TotalCbm = Convert.ToDecimal(totals.Rows[0]["Cbm"]);
                        model.ShipmentsWithCbm = Convert.ToInt32(totals.Rows[0]["WithCbm"]);
                    }

                    model.CbmByCompany = RunTable(conn, $@"SELECT
                        {GetGroupedStationSql("Station")} AS Company,
                        COUNT(DISTINCT BookingNo) AS Shipments,
                        ROUND(COALESCE(SUM({CbmSql}), 0), 2) AS Cbm
                        FROM Ocean_Revenue_Analyzer_New {whereClause}
                        GROUP BY Company ORDER BY Cbm DESC", cmdParams);

                    model.CbmByCustomer = RunTable(conn, $@"SELECT
                        COALESCE(NULLIF(TRIM(Customer), ''), 'Unassigned') AS Customer,
                        COUNT(DISTINCT BookingNo) AS Shipments,
                        ROUND(COALESCE(SUM({CbmSql}), 0), 2) AS Cbm
                        FROM Ocean_Revenue_Analyzer_New {whereClause}
                        GROUP BY Customer ORDER BY Cbm DESC", cmdParams);

                    model.CbmBySalesPerson = RunTable(conn, $@"SELECT
                        COALESCE(NULLIF(TRIM(Salesperson), ''), 'Unassigned') AS SalesPerson,
                        COUNT(DISTINCT BookingNo) AS Shipments,
                        COUNT(DISTINCT TRIM(Customer)) AS Customers,
                        ROUND(COALESCE(SUM({CbmSql}), 0), 2) AS Cbm
                        FROM Ocean_Revenue_Analyzer_New {whereClause}
                        GROUP BY SalesPerson ORDER BY Cbm DESC", cmdParams);

                    string periodFormat = model.CbmPeriodLabel == "Day" ? "%Y-%m-%d" : "%Y-%m";
                    model.CbmByPeriod = RunTable(conn, $@"SELECT
                        DATE_FORMAT({parsedDate}, '{periodFormat}') AS Period,
                        COUNT(DISTINCT BookingNo) AS Shipments,
                        ROUND(COALESCE(SUM({CbmSql}), 0), 2) AS Cbm
                        FROM Ocean_Revenue_Analyzer_New {whereClause} AND {parsedDate} IS NOT NULL
                        GROUP BY Period ORDER BY Period", cmdParams);

                    model.CbmBySalesPersonPeriod = RunTable(conn, $@"SELECT
                        COALESCE(NULLIF(TRIM(Salesperson), ''), 'Unassigned') AS SalesPerson,
                        DATE_FORMAT({parsedDate}, '{periodFormat}') AS Period,
                        ROUND(COALESCE(SUM({CbmSql}), 0), 2) AS Cbm
                        FROM Ocean_Revenue_Analyzer_New {whereClause} AND {parsedDate} IS NOT NULL
                        GROUP BY SalesPerson, Period", cmdParams);

                    model.CbmByCompanyPeriod = RunTable(conn, $@"SELECT
                        {GetGroupedStationSql("Station")} AS Company,
                        DATE_FORMAT({parsedDate}, '{periodFormat}') AS Period,
                        ROUND(COALESCE(SUM({CbmSql}), 0), 2) AS Cbm
                        FROM Ocean_Revenue_Analyzer_New {whereClause} AND {parsedDate} IS NOT NULL
                        GROUP BY Company, Period", cmdParams);

                    model.CbmByType = RunTable(conn, $@"SELECT
                        COALESCE(NULLIF(TRIM(Type), ''), 'Unspecified') AS ShipType,
                        COUNT(DISTINCT BookingNo) AS Shipments,
                        ROUND(COALESCE(SUM({CbmSql}), 0), 2) AS Cbm
                        FROM Ocean_Revenue_Analyzer_New {whereClause}
                        GROUP BY ShipType ORDER BY Cbm DESC", cmdParams);
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

        [HttpGet]
        public ActionResult Trends(string selectedStation = null, string selectedCustomer = null, string selectedSalesPerson = null, string selectedMode = null, string fromDate = null, string toDate = null, string dateType = null, string grain = null)
        {
            // Trends need a longer window than the other pages' month-to-date default: the last six months.
            if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate))
            {
                DateTime now = DateTime.Now;
                fromDate = string.IsNullOrEmpty(fromDate) ? new DateTime(now.Year, now.Month, 1).AddMonths(-5).ToString("yyyy-MM-dd") : fromDate;
                toDate = string.IsNullOrEmpty(toDate) ? now.ToString("yyyy-MM-dd") : toDate;
            }

            var model = new OceanShipmentDashboardViewModel
            {
                SelectedStation = selectedStation,
                SelectedCustomer = selectedCustomer,
                SelectedSalesPerson = selectedSalesPerson,
                SelectedMode = selectedMode,
                FromDate = fromDate,
                ToDate = toDate,
                DateType = string.IsNullOrEmpty(dateType) ? "ets" : dateType
            };
            model.DataAvailableSince = GetDataAvailableSince(model.DateType);

            DateTime from, to;
            if (!DateTime.TryParse(fromDate, out from) || !DateTime.TryParse(toDate, out to) || from > to)
            {
                ViewBag.ErrorMessage = "Choose a valid date range.";
                model.Trends = new TrendsData();
                return View(model);
            }
            from = from.Date;
            to = to.Date;

            // Weekly points for ranges up to about four months, monthly beyond that, unless the user picked one.
            model.Grain = grain == "week" || grain == "month" ? grain : ((to - from).TotalDays <= 120 ? "week" : "month");

            int days = (int)(to - from).TotalDays + 1;
            DateTime prevTo = from.AddDays(-1);
            DateTime prevFrom = prevTo.AddDays(-(days - 1));

            string dateColumn = "ETS";
            string dtLower = model.DateType.ToLower();
            if (dtLower == "eta") dateColumn = "ETA";
            else if (dtLower == "posted" || dtLower == "dateposted") dateColumn = "DatePosted";

            var rows = new List<TrendRow>();
            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();
                    LoadDropdownFilters(conn, model);

                    using (var cmd = new MySqlCommand("SELECT DISTINCT TRIM(Mode) FROM Ocean_Revenue_Analyzer_New WHERE Mode IS NOT NULL AND TRIM(Mode) != '' ORDER BY 1", conn))
                    using (var rdr = cmd.ExecuteReader()) { while (rdr.Read()) model.AvailableModes.Add(rdr.GetString(0)); }

                    var whereClause = new StringBuilder(" WHERE 1=1");
                    var cmdParams = new List<MySqlParameter>();

                    if (!string.IsNullOrEmpty(selectedStation) && selectedStation != "-- All Stations --" && selectedStation != "-- All Companies --")
                    {
                        if (selectedStation == "Gillespie-Munro Canada")
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLESPIE-MUNRO%' AND UPPER(TRIM(Station)) != 'GILLESPIE-MUNRO (USA) INC.'");
                        else if (selectedStation == "Gillespie-Munro USA")
                            whereClause.Append(" AND UPPER(TRIM(Station)) = 'GILLESPIE-MUNRO (USA) INC.'");
                        else if (selectedStation == "Gillship Navigation")
                            whereClause.Append(" AND UPPER(TRIM(Station)) LIKE '%GILLSHIP NAVIGATION%'");
                        else
                        {
                            whereClause.Append($" AND {GetGroupedStationSql("Station")} = @station");
                            cmdParams.Add(new MySqlParameter("@station", selectedStation));
                        }
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

                    if (!string.IsNullOrEmpty(selectedMode) && selectedMode != "-- All Modes --")
                    {
                        whereClause.Append(" AND TRIM(Mode) = @mode");
                        cmdParams.Add(new MySqlParameter("@mode", selectedMode.Trim()));
                    }

                    // Dates are stored as text in several formats, so they are parsed here rather than in SQL.
                    string query = $@"SELECT TRIM({dateColumn}), {GetGroupedStationSql("Station")}, TRIM(Customer), TRIM(Salesperson), TRIM(Type),
                        COALESCE(PostedInvoices, 0), ABS(COALESCE(PostedCost, 0))
                        FROM Ocean_Revenue_Analyzer_New {whereClause}";

                    using (var cmd = new MySqlCommand(query, conn))
                    {
                        if (cmdParams.Count > 0) cmd.Parameters.AddRange(cmdParams.ToArray());
                        using (var rdr = cmd.ExecuteReader())
                        {
                            while (rdr.Read())
                            {
                                DateTime? date = ParseStoredDate(rdr.IsDBNull(0) ? null : rdr.GetString(0));
                                if (date == null || date.Value < prevFrom || date.Value > to) continue;
                                rows.Add(new TrendRow
                                {
                                    Date = date.Value,
                                    Station = rdr.IsDBNull(1) ? "Unassigned" : rdr.GetString(1),
                                    Customer = rdr.IsDBNull(2) || rdr.GetString(2) == "" ? "Unassigned" : rdr.GetString(2),
                                    SalesPerson = rdr.IsDBNull(3) || rdr.GetString(3) == "" ? "Unassigned" : rdr.GetString(3),
                                    Type = rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                                    Revenue = rdr.GetDecimal(5),
                                    Cost = rdr.GetDecimal(6)
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ViewBag.ErrorMessage = "Error loading trends: " + ex.Message;
            }

            model.Trends = BuildTrends(rows, from, to, prevFrom, prevTo, model.Grain);
            model.Trends.PreviousIncomplete = model.DataAvailableSince.HasValue && prevFrom < model.DataAvailableSince.Value;
            return View(model);
        }

        private class TrendRow
        {
            public DateTime Date;
            public string Station, Customer, SalesPerson, Type;
            public decimal Revenue, Cost;
        }

        private static readonly string[] StoredDateFormats = { "MM/dd/yyyy", "M/d/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd" };

        private static DateTime? ParseStoredDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            if (value.Length > 10 && value[4] == '-') value = value.Substring(0, 10); // yyyy-MM-dd with a time part
            DateTime parsed;
            return DateTime.TryParseExact(value, StoredDateFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsed)
                ? parsed.Date
                : (DateTime?)null;
        }

        private static DateTime TrendBucketStart(DateTime date, string grain)
        {
            if (grain == "month") return new DateTime(date.Year, date.Month, 1);
            int sinceMonday = ((int)date.DayOfWeek + 6) % 7;
            return date.AddDays(-sinceMonday);
        }

        private TrendsData BuildTrends(List<TrendRow> rows, DateTime from, DateTime to, DateTime prevFrom, DateTime prevTo, string grain)
        {
            var data = new TrendsData
            {
                Grain = grain,
                PreviousFrom = prevFrom.ToString("yyyy-MM-dd"),
                PreviousTo = prevTo.ToString("yyyy-MM-dd")
            };

            // Buckets covering the selected range; a bucket the range only partly covers is flagged.
            var bucketIndex = new Dictionary<DateTime, int>();
            for (DateTime start = TrendBucketStart(from, grain); start <= to; start = grain == "month" ? start.AddMonths(1) : start.AddDays(7))
            {
                DateTime end = (grain == "month" ? start.AddMonths(1) : start.AddDays(7)).AddDays(-1);
                bucketIndex[start] = data.Buckets.Count;
                data.Buckets.Add(new TrendBucket
                {
                    Start = start.ToString("yyyy-MM-dd"),
                    Label = grain == "month" ? start.ToString("MMM yyyy") : start.ToString("dd MMM"),
                    Partial = start < from || end > to
                });
            }
            int n = data.Buckets.Count;

            var current = rows.Where(r => r.Date >= from && r.Date <= to).ToList();
            var previous = rows.Where(r => r.Date >= prevFrom && r.Date <= prevTo).ToList();

            data.Current = Totals(current);
            data.Previous = Totals(previous);

            List<decimal> PerBucket(IEnumerable<TrendRow> source, Func<TrendRow, decimal> value)
            {
                var values = new decimal[n];
                foreach (var r in source) values[bucketIndex[TrendBucketStart(r.Date, grain)]] += value(r);
                return values.ToList();
            }

            data.Revenue = PerBucket(current, r => r.Revenue);
            data.Cost = PerBucket(current, r => r.Cost);
            data.Shipments = PerBucket(current, r => 1).Select(v => (int)v).ToList();
            for (int i = 0; i < n; i++)
            {
                decimal gp = data.Revenue[i] - data.Cost[i];
                data.GrossProfit.Add(gp);
                data.MarginPct.Add(data.Revenue[i] > 0 ? Math.Round(gp / data.Revenue[i] * 100, 1) : (decimal?)null);
            }

            // Shipment mix by booking type, in a fixed order so each type keeps its color.
            foreach (string type in new[] { "Import", "Export", "Domestic", "Other" })
            {
                Func<TrendRow, bool> isType = type == "Other"
                    ? (Func<TrendRow, bool>)(r => !new[] { "IMPORT", "EXPORT", "DOMESTIC" }.Contains(r.Type.ToUpper()))
                    : (r => r.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
                var series = new TrendSeries { Name = type, Values = PerBucket(current.Where(isType), r => 1) };
                if (type != "Other" || series.Values.Any(v => v > 0)) data.ShipmentsByType.Add(series);
            }

            // Gross profit for the five largest companies by revenue; the rest are combined as "Other".
            var topCompanies = current.GroupBy(r => r.Station).OrderByDescending(g => g.Sum(r => r.Revenue)).Take(5).Select(g => g.Key).ToList();
            foreach (string company in topCompanies)
                data.CompanyGrossProfit.Add(new TrendSeries { Name = company, Values = PerBucket(current.Where(r => r.Station == company), r => r.Revenue - r.Cost) });
            var otherCompanies = current.Where(r => !topCompanies.Contains(r.Station)).ToList();
            if (otherCompanies.Count > 0)
                data.CompanyGrossProfit.Add(new TrendSeries { Name = "Other", Values = PerBucket(otherCompanies, r => r.Revenue - r.Cost) });

            List<TrendEntityRow> EntityRows(Func<TrendRow, string> key)
            {
                var prevRevenue = previous.GroupBy(key).ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));
                var result = current.GroupBy(key).Select(g => new TrendEntityRow
                {
                    Name = g.Key,
                    Shipments = g.Count(),
                    Revenue = g.Sum(r => r.Revenue),
                    Cost = g.Sum(r => r.Cost),
                    PreviousRevenue = prevRevenue.ContainsKey(g.Key) ? prevRevenue[g.Key] : 0,
                    RevenueByBucket = PerBucket(g, r => r.Revenue)
                }).ToList();
                // Also list anyone who had revenue last period but none in this one.
                foreach (var gone in prevRevenue.Where(p => p.Value != 0 && !result.Any(r => r.Name == p.Key)))
                    result.Add(new TrendEntityRow { Name = gone.Key, PreviousRevenue = gone.Value, RevenueByBucket = new decimal[n].ToList() });
                return result.OrderByDescending(r => r.Revenue).ToList();
            }

            data.Customers = EntityRows(r => r.Customer);
            data.SalesPersons = EntityRows(r => r.SalesPerson);
            return data;
        }

        private static TrendTotals Totals(List<TrendRow> rows)
        {
            return new TrendTotals { Shipments = rows.Count, Revenue = rows.Sum(r => r.Revenue), Cost = rows.Sum(r => r.Cost) };
        }

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

                        // 3. Synchronize Date Posted: the latest posting date per booking. DatePosted is stored as
                        // text, so compare it as a date (a plain MAX would rank 12/10/2025 above 05/04/2026).
                        string updateQuery = @"
                UPDATE Ocean_Revenue_Analyzer_New m
                JOIN (
                    SELECT BookingNo, DATE_FORMAT(MAX(COALESCE(
                        STR_TO_DATE(TRIM(DatePosted), '%m/%d/%Y'),
                        STR_TO_DATE(TRIM(DatePosted), '%d-%m-%Y'),
                        STR_TO_DATE(TRIM(DatePosted), '%Y-%m-%d'))), '%m/%d/%Y') as DatePosted 
                    FROM Ocean_Detail_Dates 
                    WHERE DatePosted IS NOT NULL AND DatePosted != '' 
                    GROUP BY BookingNo
                ) d ON m.BookingNo = d.BookingNo
                SET m.DatePosted = d.DatePosted
                WHERE d.DatePosted IS NOT NULL;";

                        // Strict mode turns STR_TO_DATE failures on blank or odd dates into errors inside an UPDATE.
                        // Relax it on this upload's own connection so those dates are skipped, as the dashboard
                        // queries already do.
                        using (var cmdMode = new MySqlCommand("SET SESSION sql_mode = REPLACE(REPLACE(@@SESSION.sql_mode, 'STRICT_TRANS_TABLES', ''), 'STRICT_ALL_TABLES', '')", conn))
                        {
                            cmdMode.ExecuteNonQuery();
                        }

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