-- Tables used by DashboardController. Applied automatically the first time the MySQL volume is created.
CREATE TABLE IF NOT EXISTS Ocean_Revenue_Analyzer_New (
  BookingNo VARCHAR(100) NOT NULL PRIMARY KEY,
  Station TEXT, Currency VARCHAR(20), Customer TEXT, Shipper TEXT, Consignee TEXT,
  Salesperson TEXT, Origin TEXT, Destination TEXT, ETS VARCHAR(50), ETA VARCHAR(50),
  PortOfLoading TEXT, LoadPortCountry TEXT, DestinationPort TEXT, DestinationCountry TEXT,
  NoPcs DECIMAL(18,2) DEFAULT 0, Weight VARCHAR(100), ChargeableWeightVolume VARCHAR(100),
  UnmatchedUnPostedCost DECIMAL(18,2) DEFAULT 0, PostedCost DECIMAL(18,2) DEFAULT 0, Variance DECIMAL(18,2) DEFAULT 0,
  UnPostedInvoices DECIMAL(18,2) DEFAULT 0, PostedInvoices DECIMAL(18,2) DEFAULT 0, EstimatedGP DECIMAL(18,2) DEFAULT 0,
  ActualGP DECIMAL(18,2) DEFAULT 0, ProfitLossPct VARCHAR(50), FileStatus VARCHAR(100), Createdby TEXT, Closedby TEXT,
  Mode VARCHAR(100), Type VARCHAR(100), DatePosted VARCHAR(50), CreatedOn DATETIME DEFAULT CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS Ocean_Detail_Dates (
  Id INT AUTO_INCREMENT PRIMARY KEY,
  BookingNo VARCHAR(100), MasterBookingNo VARCHAR(100), Station TEXT, Currency VARCHAR(20), Customer TEXT,
  Shipper TEXT, Consignee TEXT, Salesperson TEXT, ETD_ETA VARCHAR(50), Origin TEXT, Destination TEXT,
  PCS DECIMAL(18,2) DEFAULT 0, Weight DECIMAL(18,2) DEFAULT 0, UoM VARCHAR(50), Measure DECIMAL(18,2) DEFAULT 0, UoM1 VARCHAR(50),
  BillCode VARCHAR(100), BillDescr TEXT, Name TEXT, Reference TEXT,
  UnPostCost DECIMAL(18,2) DEFAULT 0, PostedCost DECIMAL(18,2) DEFAULT 0, Variance DECIMAL(18,2) DEFAULT 0,
  UnPostedInvoice DECIMAL(18,2) DEFAULT 0, PostedInvoice DECIMAL(18,2) DEFAULT 0, EffExchRate DECIMAL(18,6) DEFAULT 0,
  DatePosted VARCHAR(50), Createdby TEXT, Closedby TEXT,
  INDEX idx_booking (BookingNo)
);
