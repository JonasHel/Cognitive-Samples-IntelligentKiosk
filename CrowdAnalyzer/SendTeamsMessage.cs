using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;

namespace IntelligentKioskSample
{
    public class SendTeamsMessage
    {
        private static readonly HttpClient client = new HttpClient();

        public static void NotifyLotsOfPeopleComing()
        {
            var values = new Dictionary<string, string>
            {
                { "thing1", "hello" },
                { "thing2", "world" }
            };

            var content = new FormUrlEncodedContent(values);

            string logicAppTriggerUrl = "https://prod-116.westeurope.logic.azure.com:443/workflows/e37902d7520c4d16bd1ff3d65d71d1ad/triggers/manual/paths/invoke?api-version=2016-10-01&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=9EoLhmILZk-BAE_RVlydPO4BazNnANRof-G1GlwoLsY";
            var response = client.PostAsync(logicAppTriggerUrl, content).Result;
            var responseString = response.Content.ReadAsStringAsync().Result;
        }
    }
}
