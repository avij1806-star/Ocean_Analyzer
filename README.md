# Ocean Analyzer

Ocean Analyzer is an internal reporting web application for the ocean-freight business of **Gillship Navigation** and **Gillespie-Munro** (Canada and USA). It ingests revenue-analyzer spreadsheets exported from the freight system, stores them in MySQL, and presents five filterable dashboards covering shipment volume, profit and loss, staff workload, sales performance and trade lanes.

## Technology

| Layer | Choice |
|---|---|
| Framework | ASP.NET MVC 5.2.7 on .NET Framework 4.5 (Web.config targets 4.8), Razor views |
| Language | C# 7 |
| Database | MySQL, accessed with MySqlConnector 0.69.1 and raw SQL (no ORM) |
| Spreadsheet import | ExcelDataReader 3.6 (`.xlsx` / `.xls`) |
| Front end | Bootstrap 5.3, Bootstrap Icons, Chart.js 4.4 with the datalabels plugin, jQuery 3.6, DataTables 1.13 (all from CDN) |
| Hosting (as developed) | IIS Express on Windows, Visual Studio 2019 solution `Ocean_Analyzer_Fresh.sln` |

The scaffolded Bootstrap 3 / jQuery 3.4 bundles under `Scripts/` and `Content/` are only used by the default Home pages. The dashboards set `Layout = null` and load their own CDN assets.

## Repository layout

```
Ocean_Analyzer_Fresh.sln
packages/                                  NuGet packages (committed)
Ocean_Analyzer_Fresh/
  Global.asax(.cs)                         registers areas, filters, routes, bundles
  App_Start/RouteConfig.cs                 single route {controller}/{action}/{id}, default Home/Index
  App_Start/FilterConfig.cs                global HandleErrorAttribute
  App_Start/BundleConfig.cs                jQuery / Bootstrap 3 bundles for the Home pages
  Controllers/HomeController.cs            unused Visual Studio template (Index, About, Contact)
  Controllers/DashboardController.cs       all application logic (~1,500 lines)
  Models/OceanShipmentDashboardViewModel.cs the one view model shared by every dashboard
  Views/Dashboard/*.cshtml                 five self-contained dashboard pages
  Views/Home, Views/Shared                 template pages and layout
  App_Data/uploads/                        a sample ShipmentDataExport CSV (not read by the code)
  Web.config                               connection string and runtime settings
  bin/                                     committed compiled output
```

## Configuration

`Web.config` holds the only connection string:

```
Server=localhost;Database=loadbrd;Uid=root;Pwd=1234;   (provider MySqlConnector)
```

The app therefore reads and writes a MySQL server on the same machine it runs on. There is no remote or shared database and no environment-specific transform (the Debug and Release transforms contain only commented template text). Upload size is raised to 50 MB and request timeout to 300 s.

## Database

The schema is not in the repository; the tables must already exist. Two tables are used, both written by the upload action and read by the dashboards.

**`Ocean_Revenue_Analyzer_New`** is the summary table, one row per booking, keyed on `BookingNo`. Columns written: BookingNo, Station, Currency, Customer, Shipper, Consignee, Salesperson, Origin, Destination, ETS, ETA, PortOfLoading, LoadPortCountry, DestinationPort, DestinationCountry, NoPcs, Weight, ChargeableWeightVolume, UnmatchedUnPostedCost, PostedCost, Variance, UnPostedInvoices, PostedInvoices, EstimatedGP, ActualGP, ProfitLossPct, FileStatus, Createdby, Closedby, Mode, Type, CreatedOn. The dashboards additionally read `DatePosted`, which is filled from the detail table after each upload.

**`Ocean_Detail_Dates`** holds one row per detail/bill line: BookingNo, MasterBookingNo, Station, Currency, Customer, Shipper, Consignee, Salesperson, ETD_ETA, Origin, Destination, PCS, Weight, UoM, Measure, UoM1, BillCode, BillDescr, Name, Reference, UnPostCost, PostedCost, Variance, UnPostedInvoice, PostedInvoice, EffExchRate, DatePosted, Createdby, Closedby.

Dates (ETS, ETA, DatePosted) are stored as text. Every date filter tries three formats in SQL: `%m/%d/%Y`, `%d-%m-%Y` and `%Y-%m-%d`. Money columns are cleaned in SQL by stripping `$` and `,` before casting to DECIMAL.

## Data import

The Executive Overview page contains an upload form that posts two spreadsheets to `Dashboard/UploadNewRevenueAnalyzer`:

1. **Detail file**: every row with a booking number is appended to `Ocean_Detail_Dates` with a plain INSERT. Re-uploading duplicates rows.
2. **Summary file**: every row is upserted into `Ocean_Revenue_Analyzer_New` (`INSERT ... ON DUPLICATE KEY UPDATE`). Existing bookings get Station, Currency, Customer, Salesperson, PostedCost, PostedInvoices, ActualGP, FileStatus and Closedby refreshed; bookings absent from the file are kept.
3. `DatePosted` on each summary row is set to the latest DatePosted from its detail rows.

Both files are required. Column headers must match the freight-system export (for example `Booking No.`, `Station`, `Posted Cost`, `Actual GP`, `File Status`, `etd`, `eta`, `Createdby`). Missing columns become NULL or 0. Files are read in memory and never saved to disk. Success or error text is shown on the dashboard via TempData.

## Station grouping

Raw `Station` values are normalised everywhere by a shared CASE expression (`GetGroupedStationSql`):

| Raw value | Reported as |
|---|---|
| `GILLESPIE-MUNRO (USA) INC.` | Gillespie-Munro USA |
| anything else containing `GILLESPIE-MUNRO` | Gillespie-Munro Canada |
| anything containing `GILLSHIP NAVIGATION` | Gillship Navigation |
| empty / NULL | Unassigned |
| other | as-is |

The "Company" filter on every page offers these grouped names and the WHERE clause maps them back to the raw patterns.

## Dashboards

All five pages are GET actions on `DashboardController`, share `OceanShipmentDashboardViewModel`, show the same left navigation, and accept the same filter family: `selectedStation`, `selectedCustomer`, `selectedFileStatus`, `selectedCreatedBy`, `selectedSalesPerson`, `dateType` (`ets`, `eta` or `posted`) and `fromDate` / `toDate`. When no dates are given the range defaults to the first of the current month through today. Each page ends with a DataTables ledger listing the matching bookings.

Core figures on every page: **Total Shipments** = `COUNT(DISTINCT BookingNo)`, **Revenue** = `SUM(PostedInvoices)`, **Cost** = `SUM(ABS(PostedCost))`, **Gross Profit** = Revenue minus Cost, **Margin** = Gross Profit / Revenue.

| Route | Title | Filters | Content |
|---|---|---|---|
| `Dashboard/Ocean_Dashboard` | Executive P&L Dashboard | Company, Customer, Status, User, Sales person, date type, date range | KPI cards (shipments, cost, invoiced revenue, gross profit with margin); bar chart of shipments by company (top 6, click-through to Trade Lanes); pie chart of shipments by creating user (top 6, click-through to User Productivity); spreadsheet upload form; shipment ledger |
| `Dashboard/ProfitAnalysis` | Profit & Loss Financial Module | Company, Customer, date type, date range | KPI cards (expense, revenue, gross profit, margin); revenue vs cost vs GP bar charts; company summary table (shipments, revenue, cost, GP per grouped station); top 20 customers by gross profit; itemised P&L ledger |
| `Dashboard/UserPerformance` | User Performance & Workload Analysis | Company, Customer, Status, User, Sales person, date type, date range | KPI cards (bookings created, active operators); doughnut of files per creating user (top 8); operator workload table and bar chart (files handled, active vs closed, top 10); staff ledger |
| `Dashboard/SalesPersonPerformance` | Sales Person Performance Analysis | Company, Customer, Sales person, date type, date range | KPI cards (shipments, cost, revenue, gross return); bar chart of revenue by salesperson (top 8); doughnut of customer share computed from the ledger rows; sales ledger |
| `Dashboard/StationTradeLane` | Company Trade Lanes & Station Activity | Company, date type, date range | KPI cards (station volume, cost, revenue); monthly volume trend per company (bar chart from `ViewBag.TrendData`, grouped by year-month of the chosen date column); station profit table; trade-lane ledger |

Two JSON endpoints support cascading dropdowns on the pages: `Dashboard/GetCustomersByStation?station=` and `Dashboard/GetSalesPersonsByStation?station=` return the distinct customers or salespeople for a grouped station (or all when the "All" option is sent).

The `HomeController` pages (`/`, `/Home/About`, `/Home/Contact`) are the untouched Visual Studio template and are not linked from the dashboards.

## Behaviour notes

- Every action wraps its database work in a try/catch and renders the page with an error message rather than failing, so a missing table or bad connection shows as an inline alert.
- SQL filters use parameters for customer, salesperson, user, status and dates; the station patterns are hard-coded literals. Column names and date columns are interpolated from controlled values only.
- A duplicate, commented-out earlier version of `StationTradeLane` and of some UserPerformance queries remains in the controller.
- `OceanShipmentDashboardViewModel` carries fields (`SelectedMode`, `AvailableModes`, `CustomersByShipment`, `TopCustomers`) that are populated or declared but not rendered by any view.
- The sample CSV in `App_Data/uploads` is a general shipment export, not the revenue-analyzer format the upload action expects, and no code references it.

## Running

### Windows (original setup)

Open the solution in Visual Studio, ensure MySQL is running locally with database `loadbrd` and the two tables above (`docker/mysql-init/01-schema.sql` has CREATE statements), and start with IIS Express (configured for `https://localhost:44308/`). Then open `/Dashboard/Ocean_Dashboard` and upload the summary and detail spreadsheets.

### macOS / Linux (Docker)

There is no native runtime for System.Web outside Windows, so the repo includes a Docker Compose setup that hosts the precompiled app with Mono + XSP next to a MySQL 8.0 container. Requires Docker Desktop.

```
docker compose up -d --build      # first run builds the Mono image (~1 min) and creates the database
open http://localhost:8080/Dashboard/Ocean_Dashboard
docker compose stop               # stop, keep data
docker compose start              # start again
docker compose down -v            # remove everything including the database
```

How it fits together:

- `docker-compose.yml` defines `db` (MySQL, root/1234, database `loadbrd`, published on localhost:3306) and `app` (Mono + XSP on port 8080).
- `docker/Dockerfile` builds the hosting image. No compilation happens; the committed `Ocean_Analyzer_Fresh/bin` output is served, so C# changes need a rebuild. Without Visual Studio, build with Mono in a throwaway container and copy the DLL into `bin`, then `docker compose restart app`:

  ```
  docker run --rm -v "$PWD":/src ocean_analyzer_fresh2-app sh -c 'mkdir /work && cp -r /src/Ocean_Analyzer_Fresh /src/packages /work/ && cd /work/Ocean_Analyzer_Fresh && msbuild /nologo /v:minimal /p:OutDir=/tmp/build/ && cp /tmp/build/Ocean_Analyzer_Fresh.dll /tmp/build/Ocean_Analyzer_Fresh.pdb /src/Ocean_Analyzer_Fresh/bin/'
  ```
- `docker/Web.config` is mounted over the project's Web.config. It is identical except the server is `db` instead of `localhost` and the Windows-only `system.codedom` section is removed. Keep it in sync if the real Web.config changes.
- `docker/mysql-init/01-schema.sql` creates the two tables the first time the `ocean_mysql_data` volume is created.
- Uploads made through the app are stored in the MySQL volume and survive restarts. Connect with any MySQL client at localhost:3306 to inspect them.
- The project folder is bind-mounted, so edits to `.cshtml` views need no rebuild, but the app caches compiled views: run `docker compose restart app` to pick them up.
