using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;

namespace TrackMeSecureFunctions.TrackMeEdit
{
    public static class GetActiveInReachKML
    {
        private static HelperKMLParse helperKMLParse = new HelperKMLParse();

        [FunctionName("GetActiveInReachKML")]
        public static async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo myTimer,
        [CosmosDB(
                databaseName: "FreeCosmosDB",
                collectionName: "TrackMe",
                ConnectionStringSetting = "CosmosDBForFree"
                )]
             IAsyncCollector<KMLInfo> output,
           [CosmosDB(
                databaseName: "FreeCosmosDB",
                collectionName: "TrackMe",
                ConnectionStringSetting = "CosmosDBForFree"
           )] DocumentClient documentClient,
            ExecutionContext context)
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
            var SendEmailFunctionKey = config["SendEmailInReachFunctionKey"];
            var SendEmailFunctionUrl = config["SendEmailFunctionUrl"];
            var WebSiteUrl = config["WebSiteUrl"];
            var TodayTrackId = config["TodayTrackId"];

            Uri collectionUri = UriFactory.CreateDocumentCollectionUri("FreeCosmosDB", "TrackMe");
            List<Emails> emails = new List<Emails>();

            DateTime dateTimeUTC = DateTime.UtcNow.ToUniversalTime();

            //getting active tracks from cosmos
            var query = new SqlQuerySpec("SELECT c.id, c.d1, c.groupid, c.UserTimezone, c.LastPointTimestamp, c.LastLatitude, c.LastLongitude, c.LastDistance, c.InReachWebAddress, c.InReachWebPassword FROM c WHERE (c.d1 < @dateTimeUTC and c.d2 > @dateTimeUTC) or c.id = @TodayTrack",
                new SqlParameterCollection(new SqlParameter[] { new SqlParameter { Name = "@dateTimeUTC", Value = dateTimeUTC }, new SqlParameter { Name = "@TodayTrack", Value = TodayTrackId } }));
            IEnumerable<KMLInfo> TracksMetadata = documentClient.CreateDocumentQuery<KMLInfo>(collectionUri, query, new FeedOptions { EnableCrossPartitionQuery = true }).AsEnumerable();

            foreach (var item in TracksMetadata)
            {
                DateTime lastd1 = DateTime.SpecifyKind(DateTime.Parse(item.d1, CultureInfo.InvariantCulture), DateTimeKind.Utc);
                DateTime today = DateTime.UtcNow.ToUniversalTime().AddDays(-1);
                //saving d1 to restore it later
                var saveForTrackd1 = item.d1;
                //set d1 to LastPointTimestamp (if exist) to download the feed from that point from Garmin
                if (!string.IsNullOrEmpty(item.LastPointTimestamp))
                    item.d1 = DateTime.Parse(item.LastPointTimestamp, CultureInfo.InvariantCulture).ToString("yyyy-MM-ddTHH:mm:ssZ");
                else
                    item.d1 = DateTime.Parse(item.d1, CultureInfo.InvariantCulture).ToString("yyyy-MM-ddTHH:mm:ssZ");

                //resetting Today's track, once at night according to user Timezone
                if (lastd1 < today && item.id == TodayTrackId)
                {
                    var dated1 = DateTime.UtcNow.ToUniversalTime().ToString("yyyy-MM-dd");
                    var dateTimed1 = DateTime.Parse(dated1).AddHours(-item.UserTimezone).ToString("yyyy-MM-ddTHH:mm:ssZ");
                    var dateTimed2 = DateTime.Parse(dated1).AddDays(1).AddHours(-item.UserTimezone).ToString("yyyy-MM-ddTHH:mm:ssZ");
                    item.d1 = dateTimed1;
                    item.d2 = dateTimed2;
                    item.LastLongitude = 0;
                    item.LastLatitude = 0;
                    item.LastTotalDistance = 0;
                    item.LastPointTimestamp = "";
                    item.TrackStartTime = "";
                    await output.AddAsync(item);
                }
                //getting always only last point from garmin (except if new day with active tracking has started)
                HelperGetKMLFromGarmin GetKMLFromGarmin = new HelperGetKMLFromGarmin();
                var kmlFeedresult = await GetKMLFromGarmin.GetKMLAsync(item);

                //if there are new points, then load whole track from database and add the point
                if (helperKMLParse.IsThereNewPoints(kmlFeedresult, item))
                {
                    var queryOne = new SqlQuerySpec("SELECT * FROM c WHERE c.id = @id",
                        new SqlParameterCollection(new SqlParameter[] { new SqlParameter { Name = "@id", Value = item.id } }));
                    KMLInfo fullTrack = documentClient.CreateDocumentQuery<KMLInfo>(collectionUri, queryOne, new FeedOptions { PartitionKey = new PartitionKey(item.groupid) }).AsEnumerable().FirstOrDefault();

                    //process the full track
                    helperKMLParse.ParseKMLFile(kmlFeedresult, fullTrack, emails, WebSiteUrl);
                    
                    //restore d1 as it was removed initially
                    if (fullTrack.id != TodayTrackId)
                    {
                        fullTrack.d1 = DateTime.Parse(saveForTrackd1, CultureInfo.InvariantCulture).ToString("yyyy-MM-ddTHH:mm:ssZ"); 
                        fullTrack.d2 = DateTime.Parse(fullTrack.d2, CultureInfo.InvariantCulture).ToString("yyyy-MM-ddTHH:mm:ssZ");
                    }
                    await output.AddAsync(fullTrack);
                }
            }

            //sending out emails
            if (emails.Any())
            {
                HttpClient client = new HttpClient();
                Uri SendEmailFunctionUri = new Uri($"{SendEmailFunctionUrl}?code={SendEmailFunctionKey}");
                var returnMessage = await client.PostAsJsonAsync(SendEmailFunctionUri, emails);
                var lastMessage = await returnMessage.Content.ReadAsStringAsync();
            }
        }
    }
}
