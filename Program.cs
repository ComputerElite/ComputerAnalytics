using ComputerUtils.FileManaging;
using ComputerUtils.Logging;
using ComputerUtils.VarUtils;
using ComputerUtils.Webserver;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ComputerAnalytics
{
    class Program
    {
        static void Main(string[] args)
        {
            AnalyticsServer s = new AnalyticsServer();
            HttpServer server = new HttpServer();
            server.StartServer(502);
            s.AddToServer(server);
        }
    }

    class AnalyticsServer
    {
        public HttpServer server = null;
        public AnalyticsDatabase database = new AnalyticsDatabase();

        /// <summary>
        /// Adds analytics functionality to a existing and RUNNING server
        /// </summary>
        /// <param name="ports">ports on which the analytics can be sent</param>
        /// <param name="httpServer">server to which you want to add analytics functionality</param>
        /// <param name="serverUris">all uris to which the server is assigned to aditionally to the computers ip adresses. This decides where the client sends the data to. If they are on a different server in allowed domains, if it isn't null, the first one is being used. Protocol must match with protocolof origin or the request is gonna be blocked by CORS</param>
        /// <param name="analyticsViewingAuthorization">Function to check if client is authorized to view data. default: no check</param>
        /// <param name="analyticsSendingAuthorization">Function to check if client is authorized to send data. default: no check</param>
        /// <param name="allowedOrigins">Allowed origins which can send analytics data to this server. default: all</param>
        public void AddToServer(HttpServer httpServer, List<string> serverUris = null, Func<ServerRequest, bool> analyticsViewingAuthorization = null, Func<ServerRequest, bool> analyticsSendingAuthorization = null, List<string> allowedOrigins = null)
        {
            this.server = httpServer;
            if (serverUris == null) serverUris = new List<string>();
            Logger.displayLogInConsole = true;
            foreach(string prefix in server.GetPrefixes())
            {
                if (!serverUris.Contains(prefix)) serverUris.Add(prefix);
            }
            serverUris.ForEach(x => {
                if (!x.EndsWith("/")) x += "/";
                if (!x.StartsWith("http://") && !x.StartsWith("https://")) x = "http://" + x;
                Logger.Log("Analytics can be send to " + x);
            });
            string serverUrisString = "\"" + String.Join("\",\"", serverUris) + "\"";
            
            server.AddRoute("POST", "/analytics", new Func<ServerRequest, bool>(request =>
            {
                if(analyticsSendingAuthorization != null && !analyticsSendingAuthorization.Invoke(request))
                {
                    request.SendString(new AnalyticsResponse("rejected", "Your analytics report was rejected by not passing the sending checks. This probably happens because you disable analytic submittion").ToString());
                    return true;
                }
                string origin = request.context.Request.Headers.Get("Origin");
                if (allowedOrigins != null && !allowedOrigins.Contains(origin)) origin = allowedOrigins[0];
                AnalyticsData data = new AnalyticsData();
                try
                {
                    data = AnalyticsData.Recieve(request);
                } catch(Exception e)
                {
                    Logger.Log("Error while parsing analytics json:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString(new AnalyticsResponse("error", "Error parsing json: " + e.Message).ToString(), "application/json", 500, true, new Dictionary<string, string>() { { "Access-Control-Allow-Origin", origin }, { "Access-Control-Allow-Credentials", "true" } });
                    return true;
                }
                try
                {
                    database.AddAnalyticData(data);
                }
                catch (Exception e)
                {
                    Logger.Log("Error while saving analytics json:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString(new AnalyticsResponse("error", "Error saving json: " + e.Message).ToString(), "application/json", 500, true, new Dictionary<string, string>() { { "Access-Control-Allow-Origin", origin }, { "Access-Control-Allow-Credentials", "true" } });
                    return true;
                }
                Logger.Log("Added new analytics data: " + data.fileName);
                request.SendString(new AnalyticsResponse("success", "Analytic recieved").ToString(), "application/json", 200, true, new Dictionary<string, string>() { { "Access-Control-Allow-Origin", origin }, { "Access-Control-Allow-Credentials", "true" } });
                return true;
            }));
            server.AddRoute("OPTIONS", "/analytics", new Func<ServerRequest, bool>(request =>
            {
                string origin = request.context.Request.Headers.Get("Origin");
                if (allowedOrigins != null && !allowedOrigins.Contains(origin)) origin = allowedOrigins[0];
                request.SendData(new byte[0], "", 200, true, new Dictionary<string, string>() { { "Access-Control-Allow-Origin", origin }, { "Access-Control-Allow-Methods", "POST, OPTIONS" }, { "Access-Control-Allow-Credentials", "true" }, { "Access-Control-Allow-Headers", "content-type" } });
                return true;
            }));
            server.AddRoute("GET", "/analytics.js", new Func<ServerRequest, bool>(request =>
            {
                request.SendString(ReadResource("analytics.js").Replace("{0}", serverUrisString), "application/javascript");
                return true;
            }));
            server.AddRoute("GET", "/analytics", new Func<ServerRequest, bool>(request =>
            {
                if (analyticsViewingAuthorization != null && analyticsViewingAuthorization.Invoke(request))
                {
                    request.Send403();
                    return true;
                }
                request.SendString(ReadResource("analytics.html"), "text/html");
                return true;
            }));
            server.AddRoute("GET", "/plotly.min.js", new Func<ServerRequest, bool>(request =>
            {
                request.SendString(ReadResource("plotly.js"), "application/javascript");
                return true;
            }));
            server.AddRoute("GET", "/analytics/endpoints", new Func<ServerRequest, bool>(request =>
            {
                if(analyticsViewingAuthorization != null && analyticsViewingAuthorization.Invoke(request))
                {
                    request.Send403();
                    return true;
                }
                try
                {
                    string host = request.queryString.Get("host") != "" ? request.queryString.Get("host") : "";
                    string endpoint = request.queryString.Get("enpoint") != "" ? request.queryString.Get("endpoint") : "";
                    request.SendString(JsonSerializer.Serialize(database.GetAllEndpointsWithAssociatedData(null, host, endpoint)), "application/json");
                } catch(Exception e)
                {
                    Logger.Log("Error while crunching data:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString("Error: " + e.Message, "text/plain", 500);
                }
                
                return true;
            }));
            server.AddRoute("GET", "/analytics/date", new Func<ServerRequest, bool>(request =>
            {
                if (analyticsViewingAuthorization != null && analyticsViewingAuthorization.Invoke(request))
                {
                    request.Send403();
                    return true;
                }
                try
                {
                    string host = request.queryString.Get("host");
                    string endpoint = request.queryString.Get("endpoint");
                    request.SendString(JsonSerializer.Serialize(database.GetAllEndpointsSortedByDateWithAssociatedData(null, host, endpoint)), "application/json");
                }
                catch (Exception e)
                {
                    Logger.Log("Error while crunching data:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString("Error: " + e.Message, "text/plain", 500);
                }

                return true;
            }));
            server.AddRoute("GET", "/analytics/referrers", new Func<ServerRequest, bool>(request =>
            {
                if (analyticsViewingAuthorization != null && analyticsViewingAuthorization.Invoke(request))
                {
                    request.Send403();
                    return true;
                }
                try
                {
                    string host = request.queryString.Get("host");
                    string endpoint = request.queryString.Get("endpoint");
                    request.SendString(JsonSerializer.Serialize(database.GetAllReferrersWithAssociatedData(null, host, endpoint)), "application/json");
                }
                catch (Exception e)
                {
                    Logger.Log("Error while crunching data:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString("Error: " + e.Message, "text/plain", 500);
                }

                return true;
            }));
            server.AddRoute("GET", "/analytics/hosts", new Func<ServerRequest, bool>(request =>
            {
                if (analyticsViewingAuthorization != null && analyticsViewingAuthorization.Invoke(request))
                {
                    request.Send403();
                    return true;
                }
                try
                {
                    string host = request.queryString.Get("host");
                    string endpoint = request.queryString.Get("endpoint");
                    request.SendString(JsonSerializer.Serialize(database.GetAllHostsWithAssociatedData(null, host, endpoint)), "application/json");
                }
                catch (Exception e)
                {
                    Logger.Log("Error while crunching data:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString("Error: " + e.Message, "text/plain", 500);
                }

                return true;
            }));
            server.AddRoute("POST", "/import", new Func<ServerRequest, bool>(request =>
            {
                if (analyticsViewingAuthorization != null && analyticsViewingAuthorization.Invoke(request))
                {
                    request.Send403();
                    return true;
                }
                try
                {
                    List<AnalyticsData> datas = JsonSerializer.Deserialize<List<AnalyticsData>>(request.bodyString);
                    int i = 0;
                    foreach(AnalyticsData analyticsData in datas)
                    {
                        i++;
                        database.AddAnalyticData(AnalyticsData.ImportAnalyticsEntry(analyticsData));
                    }
                    request.SendString("Imported " + i + " AnalyticsDatas");
                } catch (Exception e)
                {
                    request.SendString("Error: " + e.ToString(), "text/plain", 500);
                }
                return true;
            }));
        }

        public string ReadResource(string name)
        {
            // Determine path
            var assembly = Assembly.GetExecutingAssembly();
            string resourcePath = name;
            // Format: "{Namespace}.{Folder}.{filename}.{Extension}"
            if (!name.StartsWith(Assembly.GetExecutingAssembly().GetName().Name.ToString()))
            {
                resourcePath = assembly.GetManifestResourceNames()
                    .Single(str => str.EndsWith(name));
            }

            using (Stream stream = assembly.GetManifestResourceStream(resourcePath))
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
    }

    class AnalyticsDatabase
    {
        public static string analyticsDirectory { get; set; } = "analytics\\";
        public List<AnalyticsData> data { get; set; } = new List<AnalyticsData>();

        public AnalyticsDatabase(string analyticsDir = "analytics\\")
        {
            analyticsDirectory = analyticsDir;
            FileManager.CreateDirectoryIfNotExisting(analyticsDirectory);
            foreach(string s in Directory.GetFiles(analyticsDirectory))
            {
                try
                {
                    data.Add(JsonSerializer.Deserialize<AnalyticsData>(File.ReadAllText(s)));
                } catch(Exception e)
                {
                    Logger.Log("Analytics file " + s + " failed to load:\n" + e.ToString(), LoggingType.Warning);
                }
            }
        }

        public void AddAnalyticData(AnalyticsData analyticsData)
        {
            data.Add(analyticsData);
            File.WriteAllText(analyticsDirectory + analyticsData.fileName, analyticsData.ToString());
        }

        public void DeleteOldAnalytics(TimeSpan maxTime)
        {
            DateTime now = DateTime.Now;
            int deleted = 0;
            for(int i = 0; i < data.Count; i++)
            {
                try
                {
                    if (now - data[i - deleted].openTime > maxTime)
                    {
                        Logger.Log("Deleting" + data[i - deleted].fileName);
                        File.Delete(analyticsDirectory + data[i - deleted].fileName);
                        data.RemoveAt(i - deleted);
                        deleted++;

                    }
                } catch (Exception e)
                {
                    Logger.Log("Analytics file failed to delete while cleanup:\n" + e.ToString(), LoggingType.Warning);
                }
                
            }
        }

        public List<AnalyticsEndpoint> GetAllEndpointsWithAssociatedData(List<AnalyticsData> usedData = null, string host = null, string endpoint = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching endpoints with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsEndpoint> endpoints = new Dictionary<string, AnalyticsEndpoint>();
            foreach (AnalyticsData d in usedData)
            {
                if (host != null && d.host != host) continue;
                if (endpoint != null && d.endpoint != endpoint) continue;
                if (!endpoints.ContainsKey(d.endpoint))
                {
                    endpoints.Add(d.endpoint, new AnalyticsEndpoint());
                    endpoints[d.endpoint].endpoint = d.endpoint;
                    endpoints[d.endpoint].host = d.host;
                    endpoints[d.endpoint].fullUri = d.fullUri.Split('?')[0];
                }
                endpoints[d.endpoint].clicks++;
                endpoints[d.endpoint].totalDuration += d.duration;
                if(endpoints[d.endpoint].maxDuration < d.duration) endpoints[d.endpoint].maxDuration = d.duration;
                if (endpoints[d.endpoint].minDuration > d.duration) endpoints[d.endpoint].minDuration = d.duration;
                endpoints[d.endpoint].data.Add(d);
                if (!endpoints[d.endpoint].ips.Contains(d.remote))
                {
                    endpoints[d.endpoint].uniqueIPs++;
                    endpoints[d.endpoint].ips.Add(d.remote);
                }
            }
            List<AnalyticsEndpoint> endpointsL = endpoints.Values.ToList();
            endpointsL.ForEach(new Action<AnalyticsEndpoint>(e =>
            {
                e.avgDuration = e.totalDuration / (double)e.clicks;
                e.referrers = GetAllReferrersWithAssociatedData(e.data);
            }));
            Logger.Log("Crunching of endpoints with all data took " + s.ElapsedMilliseconds + " ms");
            return endpointsL;
        }

        public List<AnalyticsHost> GetAllHostsWithAssociatedData(List<AnalyticsData> usedData = null, string host = null, string endpoint = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching hosts with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsHost> hosts = new Dictionary<string, AnalyticsHost>();
            foreach (AnalyticsData d in usedData)
            {
                if (host != null && d.host != host) continue;
                if (endpoint != null && d.endpoint != endpoint) continue;
                if (!hosts.ContainsKey(d.host))
                {
                    hosts.Add(d.host, new AnalyticsHost());
                    hosts[d.host].host = d.host;
                }
                hosts[d.host].totalClicks++;
                hosts[d.host].data.Add(d);
                hosts[d.host].totalDuration += d.duration;
                if (hosts[d.host].maxDuration < d.duration) hosts[d.host].maxDuration = d.duration;
                if (hosts[d.host].minDuration > d.duration) hosts[d.host].minDuration = d.duration;
                if (!hosts[d.host].ips.Contains(d.remote))
                {
                    hosts[d.host].totalUniqueIPs++;
                    hosts[d.host].ips.Add(d.remote);
                }
            }
            List<AnalyticsHost> hostsL = hosts.Values.ToList();
            hostsL.ForEach(new Action<AnalyticsHost>(h => {
                h.endpoints = GetAllEndpointsWithAssociatedData(h.data);
                h.avgDuration = h.totalDuration / (double)h.totalClicks;
                h.referrers = GetAllReferrersWithAssociatedData(h.data);
            }));
            Logger.Log("Crunching of hosts with all data took " + s.ElapsedMilliseconds + " ms");
            return hostsL;
        }

        public List<AnalyticsDate> GetAllEndpointsSortedByDateWithAssociatedData(List<AnalyticsData> usedData = null, string host = null, string endpoint = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching endpoints sorted by date with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsDate> dates = new Dictionary<string, AnalyticsDate>();
            foreach(AnalyticsData d in usedData)
            {
                if (host != null && d.host != host) continue;
                if (endpoint != null && d.endpoint != endpoint) continue;
                string date = d.openTime.ToString("dd.MM.yyyy");
                if(!dates.ContainsKey(date))
                {
                    dates.Add(date, new AnalyticsDate());
                    dates[date].date = date;
                    dates[date].unix = ((DateTimeOffset)new DateTime(d.openTime.Year, d.openTime.Month, d.openTime.Day)).ToUnixTimeSeconds();
                }
                dates[date].totalClicks++;
                dates[date].data.Add(d);
                dates[date].totalDuration += d.duration;
                if (dates[date].maxDuration < d.duration) dates[date].maxDuration = d.duration;
                if (dates[date].minDuration > d.duration) dates[date].minDuration = d.duration;
                if (!dates[date].ips.Contains(d.remote))
                {
                    dates[date].totalUniqueIPs++;
                    dates[date].ips.Add(d.remote);
                }
            }
            List<AnalyticsDate> datesL = dates.Values.ToList();
            datesL.ForEach(new Action<AnalyticsDate>(d => {
                d.endpoints = GetAllEndpointsWithAssociatedData(d.data);
                d.avgDuration = d.totalDuration / (double)d.totalClicks;
                d.referrers = GetAllReferrersWithAssociatedData(d.data);
            }));
            Logger.Log("Crunching of endpoints sorted by date with all data took " + s.ElapsedMilliseconds + " ms");
            return datesL;
        }

        public List<AnalyticsReferrer> GetAllReferrersWithAssociatedData(List<AnalyticsData> usedData = null, string host = null, string endpoint = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching referrers with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsReferrer> referrers = new Dictionary<string, AnalyticsReferrer>();
            foreach (AnalyticsData d in usedData)
            {
                if (host != null && d.host != host) continue;
                if (endpoint != null && d.endpoint != endpoint) continue;
                if (!referrers.ContainsKey(d.referrer))
                {
                    referrers.Add(d.referrer, new AnalyticsReferrer(d.referrer));
                }
                referrers[d.referrer].referred++;
                referrers[d.referrer].totalDuration += d.duration;
                if (referrers[d.referrer].maxDuration < d.duration) referrers[d.referrer].maxDuration = d.duration;
                if (referrers[d.referrer].minDuration > d.duration) referrers[d.referrer].minDuration = d.duration;
                if (!referrers[d.referrer].ips.Contains(d.remote))
                {
                    referrers[d.referrer].uniqueIPs++;
                    referrers[d.referrer].ips.Add(d.remote);
                }
            }
            List<AnalyticsReferrer> referrersL = referrers.Values.ToList();
            referrersL.ForEach(new Action<AnalyticsReferrer>(e => e.avgDuration = e.totalDuration / (double)e.referred));
            Logger.Log("Crunching of referrers with all data took " + s.ElapsedMilliseconds + " ms");
            return referrersL;
        }
    }

    public class AnalyticsHost
    {
        public string host { get; set; } = "";
        public long totalClicks { get; set; } = 0;
        public long totalUniqueIPs { get; set; } = 0;
        public long minDuration { get; set; } = long.MaxValue;
        public long maxDuration { get; set; } = 0;
        public double avgDuration { get; set; } = 0.0;
        public long totalDuration { get; set; } = 0;

        public List<AnalyticsEndpoint> endpoints { get; set; } = new List<AnalyticsEndpoint>();
        public List<AnalyticsReferrer> referrers { get; set; } = new List<AnalyticsReferrer>();
        public List<AnalyticsData> data = new List<AnalyticsData>();
        public List<string> ips = new List<string>();
    }

    public class AnalyticsReferrer
    {
        public string uri { get; set; } = "";
        public long referred { get; set; } = 0;
        public long uniqueIPs { get; set; } = 0;
        public long minDuration { get; set; } = long.MaxValue;
        public long maxDuration { get; set; } = 0;
        public double avgDuration { get; set; } = 0.0;
        public long totalDuration { get; set; } = 0;
        public List<string> ips = new List<string>();

        public AnalyticsReferrer(string uri = "")
        {
            this.uri = uri;
        }
    }

    public class AnalyticsDate
    {
        public long totalClicks { get; set; } = 0;
        public long totalUniqueIPs { get; set; } = 0;
        public string date { get; set; } = "";
        public long unix { get; set; } = 0;
        public long minDuration { get; set; } = long.MaxValue;
        public long maxDuration { get; set; } = 0;
        public double avgDuration { get; set; } = 0.0;
        public long totalDuration { get; set; } = 0;
        public List<AnalyticsEndpoint> endpoints { get; set; } = new List<AnalyticsEndpoint>();
        public List<AnalyticsReferrer> referrers { get; set; } = new List<AnalyticsReferrer>();
        public List<AnalyticsData> data = new List<AnalyticsData>();
        public List<string> ips = new List<string>();
    }

    public class AnalyticsEndpoint
    {
        public string endpoint { get; set; } = "";
        public string fullUri { get; set; } = "";
        public string host { get; set; } = "";
        public long clicks { get; set; } = 0;
        public long minDuration { get; set; } = long.MaxValue;
        public long maxDuration { get; set; } = 0;
        public double avgDuration { get; set; } = 0.0;
        public long totalDuration { get; set; } = 0;
        public long uniqueIPs { get; set; } = 0;
        public List<AnalyticsReferrer> referrers { get; set; } = new List<AnalyticsReferrer>();
        public List<AnalyticsData> data = new List<AnalyticsData>();
        public List<string> ips = new List<string>();
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

    public class AnalyticsData
    {
        public string analyticsVersion { get; set; } = "";
        public string fullUri { get; set; } = "";
        public string fullEndpoint { get; set; } = "";
        public string host { get; set; } = "";
        public string endpoint { get; set; } = "";
        public string uA { get; set; } = "";
        public string remote { get; set; } = "";
        public string referrer { get; set; } = "";
        public long sideOpen { get; set; } = 0;// unix
        public DateTime openTime { get; set; } = DateTime.MinValue;
        public long sideClose { get; set; } = 0; // unix
        public DateTime closeTime { get; set; } = DateTime.MinValue;
        public long duration { get; set; } = 0; // seconds
        public string fileName { get; set; } = "";

        public static AnalyticsData Recieve(ServerRequest request)
        {
            AnalyticsData data = JsonSerializer.Deserialize<AnalyticsData>(request.bodyString);
            data.fileName = DateTime.Now.Ticks + "_" + data.sideOpen + "_" + data.sideClose + ".json";
            switch(data.analyticsVersion)
            {
                case "1.0":
                    // data.endpoint = request.path; idiot, this will return /analytics
                    data.fullEndpoint = new Uri(data.fullUri).AbsolutePath;
                    data.endpoint = data.fullEndpoint.Substring(0, data.fullEndpoint.LastIndexOf("?") == -1 ? data.fullEndpoint.Length : data.fullEndpoint.LastIndexOf("?"));
                    if (!data.endpoint.EndsWith("/")) data.endpoint += "/";
                    data.host = new Uri(data.fullUri).Host;
                    data.uA = request.context.Request.UserAgent;
                    data.remote = request.context.Request.RemoteEndPoint.Address.ToString();
                    data.duration = data.sideClose - data.sideOpen;
                    if (data.duration < 0) throw new Exception("Some idiot made a manual request with negative duration.");
                    data.openTime = TimeConverter.UnixTimeStampToDateTime(data.sideOpen);
                    data.closeTime = TimeConverter.UnixTimeStampToDateTime(data.sideClose);
                    break;
                default:
                    throw new Exception("Please use a supported analyticsVersion. Current latest: 1.0");
            }
            
            return data;
        }

        public static AnalyticsData ImportAnalyticsEntry(AnalyticsData data)
        {
            //AnalyticsData data = JsonSerializer.Deserialize<AnalyticsData>(File.ReadAllText(file));
            data.fileName = DateTime.Now.Ticks + "_" + data.sideOpen + "_" + data.sideClose + ".json";
            switch (data.analyticsVersion)
            {
                case "1.0":
                    // data.endpoint = request.path; idiot, this will return /analytics
                    data.fullEndpoint = new Uri(data.fullUri).AbsolutePath;
                    data.endpoint = data.fullEndpoint.Substring(0, data.fullEndpoint.LastIndexOf("?") == -1 ? data.fullEndpoint.Length : data.fullEndpoint.LastIndexOf("?"));
                    if (!data.endpoint.EndsWith("/")) data.endpoint += "/";
                    data.host = new Uri(data.fullUri).Host;
                    data.duration = data.sideClose - data.sideOpen;
                    if (data.duration < 0) throw new Exception("Some idiot made a manual request with negative duration.");
                    data.openTime = TimeConverter.UnixTimeStampToDateTime(data.sideOpen);
                    data.closeTime = TimeConverter.UnixTimeStampToDateTime(data.sideClose);
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
