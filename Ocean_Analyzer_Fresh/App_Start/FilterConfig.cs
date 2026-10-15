using System.Web;
using System.Web.Mvc;

namespace Ocean_Analyzer_Fresh
{
    public class FilterConfig
    {
        public static void RegisterGlobalFilters(GlobalFilterCollection filters)
        {
            filters.Add(new HandleErrorAttribute());
        }
    }
}
