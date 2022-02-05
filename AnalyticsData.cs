using ComputerUtils.Encryption;
using ComputerUtils.VarUtils;
using ComputerUtils.Webserver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ComputerAnalytics
{
    [BsonIgnoreExtraElements]
    public class AnalyticsData
    {
        public ObjectId _id { get; set; } = default(ObjectId);
        public string analyticsVersion { get; set; } = null;
        public string fullUri { get; set; } = null;
        public string fullEndpoint { get; set; } = null;
        public string host { get; set; } = null;
        public string endpoint { get; set; } = null;
        public string uA { get; set; } = null;
        public string remote { get; set; } = null;
        public string referrer { get; set; } = null;
        public long sideOpen { get; set; } = 0;// unix
        public DateTime openTime { get; set; } = DateTime.MinValue;
        public long sideClose { get; set; } = 0; // unix
        public DateTime closeTime { get; set; } = DateTime.MinValue;
        public long duration { get; set; } = 0; // seconds
        public string token { get; set; } = "";
        public string fileName { get; set; } = null;
        public long screenWidth { get; set; } = 0;
        public long screenHeight { get; set; } = 0;
        public AnonymisedGeoLocationQueryResponse geolocation { get; set; } = new AnonymisedGeoLocationQueryResponse();

        public override bool Equals(object obj)
        {
            AnalyticsData d = (AnalyticsData)obj;
            return sideOpen == d.sideOpen && fullUri == d.fullUri && d.uA == uA && sideClose == d.sideClose && referrer == d.referrer && remote == d.remote;
        }

        public static AnalyticsData Load(string f)
        {
            AnalyticsData d = JsonSerializer.Deserialize<AnalyticsData>(File.ReadAllText(f));
            if (d.fullUri.Contains("script") || d.uA.Contains("script") || d.referrer.Contains("script"))
            {
                throw new Exception("Analytics contains 'script' which is forbidden for security resons");
            }
            return d;
        }

        public static AnalyticsData Recieve(ServerRequest request)
        {
            AnalyticsData data = JsonSerializer.Deserialize<AnalyticsData>(request.bodyString);
            data.fileName = DateTime.UtcNow.Ticks + "_" + data.sideOpen + "_" + data.sideClose + ".json";
            switch (data.analyticsVersion)
            {
                case "1.0":
                    string ip = request.context.Request.Headers["X-Forwarded-For"] == null ? request.context.Request.RemoteEndPoint.Address.ToString() : request.context.Request.Headers["X-Forwarded-For"];
                    // data.endpoint = request.path; idiot, this will return /analytics
                    data.geolocation = GeoLocationClient.GetAnonymisedGeoLocation(ip);
                    data.fullUri = data.fullUri.Split('?')[0];
                    data.fullEndpoint = new Uri(data.fullUri).AbsolutePath;
                    data.endpoint = data.fullEndpoint.Substring(0, data.fullEndpoint.LastIndexOf("?") == -1 ? data.fullEndpoint.Length : data.fullEndpoint.LastIndexOf("?"));
                    if (!data.endpoint.EndsWith("/")) data.endpoint += "/";
                    data.host = new Uri(data.fullUri).Host;
                    data.uA = request.context.Request.UserAgent;
                    data.remote = Hasher.GetSHA256OfString(ip);
                    data.duration = data.sideClose - data.sideOpen;
                    if (data.duration < 0) throw new Exception("Some idiot made a manual request with negative duration.");
                    data.openTime = TimeConverter.UnixTimeStampToDateTime(data.sideOpen);
                    data.closeTime = TimeConverter.UnixTimeStampToDateTime(data.sideClose);
                    if (data.closeTime > DateTime.UtcNow + new TimeSpan(0, 10, 0)) throw new Exception("Some idiot or browser thought it'd be funny to close the site 10 minutes in the future");
                    if (data.closeTime < DateTime.UtcNow - new TimeSpan(0, 10, 0)) throw new Exception("So either the internet really took 5 minute to deliver the request or you just fucked up and got the time wrong");
                    if (data.fullUri.Contains("script") || data.uA.Contains("script") || data.referrer.Contains("script"))
                    {
                        throw new Exception("Analytics contains 'script' which is forbidden for security resons");
                    }
                    break;
                default:
                    throw new Exception("Please use a supported analyticsVersion. Current latest: 1.0");
            }
            return data;
        }

        public static AnalyticsData ImportAnalyticsEntry(AnalyticsData data)
        {
            //AnalyticsData data = JsonSerializer.Deserialize<AnalyticsData>(File.ReadAllText(file));
            data.fileName = DateTime.UtcNow.Ticks + "_" + data.sideOpen + "_" + data.sideClose + ".json";
            switch (data.analyticsVersion)
            {
                case "1.0":
                    // data.endpoint = request.path; idiot, this will return /analytics
                    if (data.fullUri.Contains("script") || data.uA.Contains("script") || data.referrer.Contains("script"))
                    {
                        throw new Exception("Analytics contains 'script' which is forbidden for security resons");
                    }
                    data.fullUri = data.fullUri.Split('?')[0];
                    data.fullEndpoint = new Uri(data.fullUri).AbsolutePath;
                    data.endpoint = data.fullEndpoint.Substring(0, data.fullEndpoint.LastIndexOf("?") == -1 ? data.fullEndpoint.Length : data.fullEndpoint.LastIndexOf("?"));
                    if (!data.endpoint.EndsWith("/")) data.endpoint += "/";
                    data.host = new Uri(data.fullUri).Host;
                    data.duration = data.sideClose - data.sideOpen;
                    if (data.duration < 0) throw new Exception("Some idiot made a manual request with negative duration.");
                    data.openTime = TimeConverter.UnixTimeStampToDateTime(data.sideOpen);
                    data.closeTime = TimeConverter.UnixTimeStampToDateTime(data.sideClose);
                    if (data.closeTime > DateTime.UtcNow) throw new Exception("Some idiot or browser thought it'd be funny to close the site in the future");
                    break;
                default:
                    throw new Exception("Please use a supported analyticsVersion. Current latest: 1.0");
            }

            return data;
        }

        public override string ToString()
        {
            return JsonSerializer.Serialize(this);
        }
    }
}
