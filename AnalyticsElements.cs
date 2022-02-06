using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ComputerAnalytics
{
    [BsonIgnoreExtraElements]
    public class AnalyticsReferrerId
    {
        public string uri { get; set; } = "";
    }

    [BsonIgnoreExtraElements]
    public class AnalyticsAggregationQueryResult<T>
    {
        public T _id { get; set; } = default(T);
        public long totalClicks { get; set; } = 0;
        public long totalUniqueIPs { get; set; } = 0;
        public long minDuration { get; set; } = long.MaxValue;
        public long maxDuration { get; set; } = 0;
        public double avgDuration { get; set; } = 0.0;
        public long totalDuration { get; set; } = 0;
        public DateTime closeTime { get; set; } = DateTime.MinValue;
        public List<string> ips = new List<string>();
    }

    [BsonIgnoreExtraElements]
    public class AnalyticsAggregationNewUsersResult : AnalyticsAggregationQueryResult<AnalyticsTimeId>
    {
        public long newIPs { get; set; } = 0;
        public long returningIPs { get; set; } = 0;

    }

    [BsonIgnoreExtraElements]
    public class AnalyticsTimeId
    {
        public string time { get; set; } = "";
        public long unix { get; set; } = 0;
    }

    [BsonIgnoreExtraElements]
    public class AnalyticsScreenId
    {
        public long screenWidth { get; set; } = 0;
        public long screenHeight { get; set; } = 0;
    }

    [BsonIgnoreExtraElements]
    public class AnalyticsEndpointId
    {
        public string endpoint { get; set; } = "";
        public string fullUri { get; set; } = "";
        public string host { get; set; } = "";
    }

    [BsonIgnoreExtraElements]
    public class AnalyticsCountryId
    {
        public string countryCode { get; set; } = "";
    }

    public class AnalyticsResponse
    {
        public string type { get; set; } = "success";
        public string msg { get; set; } = "";
        public AnalyticsResponse(string type, string msg = "")
        {
            this.type = type;
            this.msg = msg;
        }

        public override string ToString()
        {
            return JsonSerializer.Serialize(this);
        }
    }
}
