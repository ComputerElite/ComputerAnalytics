using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComputerAnalytics
{
    public class GeoLocationQueryResponse
    {
        public string status { get; set; } = "";
        public string country { get; set; } = "";
        public string countryCode { get; set; } = "";
        public string region { get; set; } = "";
        public string regionName { get; set; } = "";
        public string city { get; set; } = "";
        public string zip { get; set; } = "";
        public double lat { get; set; } = 0.0;
        public double lon { get; set; } = 0.0;
        public string timezone { get; set; } = "";
        public string isp { get; set; } = "";
        public string org { get; set; } = "";

        [JsonPropertyName("as")]
        public string _as { get; set; } = "";
        public string query { get; set; } = "";
    }

    public class AnonymisedGeoLocationQueryResponse
    {
        public string countryCode { get; set; } = "";
        public string timezone { get; set; } = "";

        public static explicit operator AnonymisedGeoLocationQueryResponse(GeoLocationQueryResponse v)
        {
            AnonymisedGeoLocationQueryResponse g = new AnonymisedGeoLocationQueryResponse();
            g.countryCode = v.countryCode.ToLower();
            g.timezone = v.timezone;
            return g;
        }
    }

    public class GeoLocationClient
    {
        public static GeoLocationQueryResponse GetGeoLocation(string ip)
        {
            WebClient c = new WebClient();
            string glqr = c.DownloadString("http://ip-api.com/json/" + ip);
            try
            {
                GeoLocationQueryResponse geo = JsonSerializer.Deserialize<GeoLocationQueryResponse>(glqr);
                return geo;
            } catch
            {
                return null;
            }
        }

        public static AnonymisedGeoLocationQueryResponse GetAnonymisedGeoLocation(string ip)
        {
            return (AnonymisedGeoLocationQueryResponse)GetGeoLocation(ip);
        }
    }
}