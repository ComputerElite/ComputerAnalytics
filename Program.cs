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
                    Logger.Log("Error while recieving analytics json:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString(new AnalyticsResponse("error", e.Message).ToString(), "application/json", 500, true, new Dictionary<string, string>() { { "Access-Control-Allow-Origin", origin }, { "Access-Control-Allow-Credentials", "true" } });
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
                    string host = request.queryString.Get("host");
                    string endpoint = request.queryString.Get("endpoint");
                    string queryString = request.queryString.Get("querystring");
                    string referrer = request.queryString.Get("referrer");
                    string date = request.queryString.Get("date");
                    request.SendString(JsonSerializer.Serialize(database.GetAllEndpointsWithAssociatedData(null, host, endpoint, queryString, referrer, date)), "application/json");
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
                    string queryString = request.queryString.Get("querystring");
                    string referrer = request.queryString.Get("referrer");
                    string date = request.queryString.Get("date");
                    request.SendString(JsonSerializer.Serialize(database.GetAllEndpointsSortedByDateWithAssociatedData(null, host, endpoint, queryString, referrer, date)), "application/json");
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
                    string queryString = request.queryString.Get("querystring");
                    string referrer = request.queryString.Get("referrer");
                    string date = request.queryString.Get("date");
                    request.SendString(JsonSerializer.Serialize(database.GetAllReferrersWithAssociatedData(null, host, endpoint, queryString, referrer, date)), "application/json");
                }
                catch (Exception e)
                {
                    Logger.Log("Error while crunching data:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString("Error: " + e.Message, "text/plain", 500);
                }

                return true;
            }));
            server.AddRoute("GET", "/analytics/querystrings", new Func<ServerRequest, bool>(request =>
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
                    string queryString = request.queryString.Get("querystring");
                    string referrer = request.queryString.Get("referrer");
                    string date = request.queryString.Get("date");
                    request.SendString(JsonSerializer.Serialize(database.GetAllQueryStringsWithAssociatedData(null, host, endpoint, queryString, referrer, date)), "application/json");
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
                    string queryString = request.queryString.Get("querystring");
                    string referrer = request.queryString.Get("referrer");
                    string date = request.queryString.Get("date");
                    request.SendString(JsonSerializer.Serialize(database.GetAllHostsWithAssociatedData(null, host, endpoint, queryString, referrer, date)), "application/json");
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
                    int rejected = 0;
                    foreach(AnalyticsData analyticsData in datas)
                    {
                        if (!database.Contains(analyticsData))
                        {
                            
                            try
                            {
                                database.AddAnalyticData(AnalyticsData.ImportAnalyticsEntry(analyticsData));
                                i++;
                            } catch (Exception e)
                            {
                                Logger.Log("Expection while importing Analytics data:\n" + e.Message, LoggingType.Warning);
                                rejected++;
                            }
                            
                        }
                    }
                    request.SendString("Imported " + i + " AnalyticsDatas, rejected " + rejected + " AnalyticsDatas");
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
            bool log = Logger.displayLogInConsole;
            Logger.displayLogInConsole = true;
            Logger.Log("Loading database in " + analyticsDir);
            Stopwatch stopwatch = Stopwatch.StartNew();
            analyticsDirectory = analyticsDir;
            FileManager.CreateDirectoryIfNotExisting(analyticsDirectory);
            string[] files = Directory.GetFiles(analyticsDirectory);
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    data.Add(AnalyticsData.Load(files[i]));
                } catch(Exception e)
                {
                    Logger.Log("Analytics file " + files[i] + " failed to load:\n" + e.ToString(), LoggingType.Warning);
                }
            }
            stopwatch.Stop();
            Logger.Log("Loading Analytics Database with " + data.Count + " entries took " + stopwatch.Elapsed);
            Logger.displayLogInConsole = log;
        }

        public bool Contains(AnalyticsData d)
        {
            for(int i = 0; i < data.Count; i++)
            {
                if (d.Equals(data[i])) return true;
            }
            return false;
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

        public List<AnalyticsEndpoint> GetAllEndpointsWithAssociatedData(List<AnalyticsData> usedData = null, string host = null, string endpoint = null, string queryString = null, string referrer = null, string date = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching endpoints with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsEndpoint> endpoints = new Dictionary<string, AnalyticsEndpoint>();
            for (int i = 0; i < usedData.Count; i++)
            {
                if (IsNotValid(usedData[i], host, endpoint, queryString, referrer, date)) continue;
                if (!endpoints.ContainsKey(usedData[i].endpoint))
                {
                    endpoints.Add(usedData[i].endpoint, new AnalyticsEndpoint());
                    endpoints[usedData[i].endpoint].endpoint = usedData[i].endpoint;
                    endpoints[usedData[i].endpoint].host = usedData[i].host;
                    endpoints[usedData[i].endpoint].fullUri = usedData[i].fullUri.Split('?')[0];
                }
                endpoints[usedData[i].endpoint].clicks++;
                endpoints[usedData[i].endpoint].totalDuration += usedData[i].duration;
                if(endpoints[usedData[i].endpoint].maxDuration < usedData[i].duration) endpoints[usedData[i].endpoint].maxDuration = usedData[i].duration;
                if (endpoints[usedData[i].endpoint].minDuration > usedData[i].duration) endpoints[usedData[i].endpoint].minDuration = usedData[i].duration;
                endpoints[usedData[i].endpoint].data.Add(usedData[i]);
                if (!endpoints[usedData[i].endpoint].ips.Contains(usedData[i].remote))
                {
                    endpoints[usedData[i].endpoint].uniqueIPs++;
                    endpoints[usedData[i].endpoint].ips.Add(usedData[i].remote);
                }
            }
            List<AnalyticsEndpoint> endpointsL = endpoints.Values.ToList();
            endpointsL.ForEach(new Action<AnalyticsEndpoint>(e =>
            {
                e.avgDuration = e.totalDuration / (double)e.clicks;
                e.referrers = GetAllReferrersWithAssociatedData(e.data);
                e.queryStrings = GetAllQueryStringsWithAssociatedData(e.data);
            }));
            Logger.Log("Crunching of endpoints with all data took " + s.ElapsedMilliseconds + " ms");
            return endpointsL;
        }

        public List<AnalyticsHost> GetAllHostsWithAssociatedData(List<AnalyticsData> usedData = null, string host = null, string endpoint = null, string queryString = null, string referrer = null, string date = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching hosts with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsHost> hosts = new Dictionary<string, AnalyticsHost>();
            for (int i = 0; i < usedData.Count; i++)
            {
                if (IsNotValid(usedData[i], host, endpoint, queryString, referrer, date)) continue;
                if (date != null && !date.Split(',').Contains(usedData[i].openTime.ToString("dd.MM.yyyy"))) continue;
                if (!hosts.ContainsKey(usedData[i].host))
                {
                    hosts.Add(usedData[i].host, new AnalyticsHost());
                    hosts[usedData[i].host].host = usedData[i].host;
                }
                hosts[usedData[i].host].totalClicks++;
                hosts[usedData[i].host].data.Add(usedData[i]);
                hosts[usedData[i].host].totalDuration += usedData[i].duration;
                if (hosts[usedData[i].host].maxDuration < usedData[i].duration) hosts[usedData[i].host].maxDuration = usedData[i].duration;
                if (hosts[usedData[i].host].minDuration > usedData[i].duration) hosts[usedData[i].host].minDuration = usedData[i].duration;
                if (!hosts[usedData[i].host].ips.Contains(usedData[i].remote))
                {
                    hosts[usedData[i].host].totalUniqueIPs++;
                    hosts[usedData[i].host].ips.Add(usedData[i].remote);
                }
            }
            
            List<AnalyticsHost> hostsL = hosts.Values.ToList();
            hostsL.ForEach(new Action<AnalyticsHost>(h => {
                h.endpoints = GetAllEndpointsWithAssociatedData(h.data);
                h.avgDuration = h.totalDuration / (double)h.totalClicks;
                h.referrers = GetAllReferrersWithAssociatedData(h.data);
                h.queryStrings = GetAllQueryStringsWithAssociatedData(h.data);
            }));
            Logger.Log("Crunching of hosts with all data took " + s.ElapsedMilliseconds + " ms");
            return hostsL;
        }

        public List<AnalyticsDate> GetAllEndpointsSortedByDateWithAssociatedData(List<AnalyticsData> usedData = null, string host = null, string endpoint = null, string queryString = null, string referrer = null, string ddate = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching endpoints sorted by date with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsDate> dates = new Dictionary<string, AnalyticsDate>();
            for(int i = 0; i < usedData.Count; i++)
            {
                if (IsNotValid(usedData[i], host, endpoint, queryString, referrer, ddate)) continue;
                string date = usedData[i].openTime.ToString("dd.MM.yyyy");
                if(!dates.ContainsKey(date))
                {
                    dates.Add(date, new AnalyticsDate());
                    dates[date].date = date;
                    dates[date].unix = ((DateTimeOffset)new DateTime(usedData[i].openTime.Year, usedData[i].openTime.Month, usedData[i].openTime.Day)).ToUnixTimeSeconds();
                }
                dates[date].totalClicks++;
                dates[date].data.Add(usedData[i]);
                dates[date].totalDuration += usedData[i].duration;
                if (dates[date].maxDuration < usedData[i].duration) dates[date].maxDuration = usedData[i].duration;
                if (dates[date].minDuration > usedData[i].duration) dates[date].minDuration = usedData[i].duration;
                if (!dates[date].ips.Contains(usedData[i].remote))
                {
                    dates[date].totalUniqueIPs++;
                    dates[date].ips.Add(usedData[i].remote);
                }
            }
            dates = Sorter.Sort(dates);
            List<AnalyticsDate> datesL = dates.Values.ToList();
            datesL.ForEach(new Action<AnalyticsDate>(d => {
                d.endpoints = GetAllEndpointsWithAssociatedData(d.data);
                d.avgDuration = d.totalDuration / (double)d.totalClicks;
                d.referrers = GetAllReferrersWithAssociatedData(d.data);
                d.queryStrings = GetAllQueryStringsWithAssociatedData(d.data);
            }));
            Logger.Log("Crunching of endpoints sorted by date with all data took " + s.ElapsedMilliseconds + " ms");
            return datesL;
        }

        public List<AnalyticsReferrer> GetAllReferrersWithAssociatedData(List<AnalyticsData> usedData = null, string host = null, string endpoint = null, string queryString = null, string referrer = null, string date = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching referrers with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsReferrer> referrers = new Dictionary<string, AnalyticsReferrer>();
            for (int i = 0; i < usedData.Count; i++)
            {
                if (IsNotValid(usedData[i], host, endpoint, queryString, referrer, date)) continue;
                if (!referrers.ContainsKey(usedData[i].referrer))
                {
                    referrers.Add(usedData[i].referrer, new AnalyticsReferrer(usedData[i].referrer));
                }
                referrers[usedData[i].referrer].referred++;
                referrers[usedData[i].referrer].totalDuration += usedData[i].duration;
                if (referrers[usedData[i].referrer].maxDuration < usedData[i].duration) referrers[usedData[i].referrer].maxDuration = usedData[i].duration;
                if (referrers[usedData[i].referrer].minDuration > usedData[i].duration) referrers[usedData[i].referrer].minDuration = usedData[i].duration;
                if (!referrers[usedData[i].referrer].ips.Contains(usedData[i].remote))
                {
                    referrers[usedData[i].referrer].uniqueIPs++;
                    referrers[usedData[i].referrer].ips.Add(usedData[i].remote);
                }
            }
            List<AnalyticsReferrer> referrersL = referrers.Values.ToList();
            referrersL.ForEach(new Action<AnalyticsReferrer>(e => e.avgDuration = e.totalDuration / (double)e.referred));
            Logger.Log("Crunching of referrers with all data took " + s.ElapsedMilliseconds + " ms");
            return referrersL;
        }

        public bool IsNotValid(AnalyticsData d, string host = null, string endpoint = null, string queryString = null, string referrer = null, string date = null)
        {
            if (host != null && d.host != host) return true;
            if (endpoint != null && d.endpoint != endpoint) return true;
            if (queryString != null && d.queryString != queryString) return true;
            if (referrer != null && d.referrer != referrer) return true;
            if (date != null && !date.Split(',').Contains(d.openTime.ToString("dd.MM.yyyy"))) return true;
            return false;
        }

        public List<AnalyticsQueryString> GetAllQueryStringsWithAssociatedData(List<AnalyticsData> usedData = null, string host = null, string endpoint = null, string queryString = null, string referrer = null, string date = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching QueryStrings with all data for " + usedData.Count + " analytics just for a really idiotic idiot");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsQueryString> queryStrings = new Dictionary<string, AnalyticsQueryString>();
            for (int i = 0; i < usedData.Count; i++)
            {
                if (IsNotValid(usedData[i], host, endpoint, queryString, referrer, date)) continue;
                if (!queryStrings.ContainsKey(usedData[i].queryString))
                {
                    queryStrings.Add(usedData[i].queryString, new AnalyticsQueryString(usedData[i].queryString));
                }
                queryStrings[usedData[i].queryString].totalClicks++;
                if(!queryStrings[usedData[i].queryString].fullUris.Contains(usedData[i].fullUri)) queryStrings[usedData[i].queryString].fullUris.Add(usedData[i].fullUri);
                queryStrings[usedData[i].queryString].totalDuration += usedData[i].duration;
                if (queryStrings[usedData[i].queryString].maxDuration < usedData[i].duration) queryStrings[usedData[i].queryString].maxDuration = usedData[i].duration;
                if (queryStrings[usedData[i].queryString].minDuration > usedData[i].duration) queryStrings[usedData[i].queryString].minDuration = usedData[i].duration;
                if (!queryStrings[usedData[i].queryString].ips.Contains(usedData[i].remote))
                {
                    queryStrings[usedData[i].queryString].totalUniqueIPs++;
                    queryStrings[usedData[i].queryString].ips.Add(usedData[i].remote);
                }
            }
            List<AnalyticsQueryString> queryStringsL = queryStrings.Values.ToList();
            queryStringsL.ForEach(new Action<AnalyticsQueryString>(q =>
            {
                q.avgDuration = q.totalDuration / (double)q.totalClicks;
                q.referrers = GetAllReferrersWithAssociatedData(q.data);
            }));
            Logger.Log("Crunching of QueryStrings with all data took " + s.ElapsedMilliseconds + " ms");
            return queryStringsL;
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

        public List<AnalyticsQueryString> queryStrings { get; set; } = new List<AnalyticsQueryString>();
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

        public List<AnalyticsQueryString> queryStrings { get; set; }  = new List<AnalyticsQueryString>();
        public List<AnalyticsEndpoint> endpoints { get; set; } = new List<AnalyticsEndpoint>();
        public List<AnalyticsReferrer> referrers { get; set; } = new List<AnalyticsReferrer>();
        public List<AnalyticsData> data = new List<AnalyticsData>();
        public List<string> ips = new List<string>();
    }

    public class AnalyticsQueryString
    {
        public long totalClicks { get; set; } = 0;
        public long totalUniqueIPs { get; set; } = 0;
        public long minDuration { get; set; } = long.MaxValue;
        public long maxDuration { get; set; } = 0;
        public double avgDuration { get; set; } = 0.0;
        public long totalDuration { get; set; } = 0;
        public List<AnalyticsReferrer> referrers { get; set; } = new List<AnalyticsReferrer>();
        public List<AnalyticsData> data = new List<AnalyticsData>();
        public List<string> ips = new List<string>();
        public string queryString { get; set; } = "";
        public List<string> fullUris { get; set; } = new List<string>();

        public AnalyticsQueryString(string queryString = "")
        {
            this.queryString = queryString;
        }
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

        public List<AnalyticsQueryString> queryStrings { get; set; } = new List<AnalyticsQueryString>();
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
        public string analyticsVersion { get; set; } = null;
        public string fullUri { get; set; } = null;
        public string fullEndpoint { get; set; } = null;
        public string queryString { get; set; } = null;
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
        public string fileName { get; set; } = null;

        public override bool Equals(object obj)
        {
            AnalyticsData d = (AnalyticsData)obj;
            return sideOpen == d.sideOpen && fullUri == d.fullUri && d.uA == uA && sideClose == d.sideClose && referrer == d.referrer && remote == d.remote;
        }

        public static AnalyticsData Load(string f)
        {
            AnalyticsData d = JsonSerializer.Deserialize<AnalyticsData>(File.ReadAllText(f));
            if (d.queryString == null)
            {
                d.queryString = d.fullUri.Contains("?") ? d.fullUri.Substring( d.fullUri.IndexOf("?")) : "";
                File.WriteAllText(f, JsonSerializer.Serialize(d));
                Logger.Log("Added query string to " + d.fileName);
            }
            if (d.fullUri.Contains("script") || d.uA.Contains("script") || d.referrer.Contains("script"))
            {
                throw new Exception("Analytics contains 'script' which is forbidden for security resons");
            }
            return d;
        }

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
                    data.queryString = data.fullUri.Contains("?") ? data.fullUri.Substring(data.fullUri.IndexOf("?")) : "";
                    data.remote = request.context.Request.RemoteEndPoint.Address.ToString();
                    data.duration = data.sideClose - data.sideOpen;
                    if (data.duration < 0) throw new Exception("Some idiot made a manual request with negative duration.");
                    data.openTime = TimeConverter.UnixTimeStampToDateTime(data.sideOpen);
                    data.closeTime = TimeConverter.UnixTimeStampToDateTime(data.sideClose);
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
            data.fileName = DateTime.Now.Ticks + "_" + data.sideOpen + "_" + data.sideClose + ".json";
            switch (data.analyticsVersion)
            {
                case "1.0":
                    // data.endpoint = request.path; idiot, this will return /analytics
                    if (data.fullUri.Contains("script") || data.uA.Contains("script") || data.referrer.Contains("script"))
                    {
                        throw new Exception("Analytics contains 'script' which is forbidden for security resons");
                    }
                    data.fullEndpoint = new Uri(data.fullUri).AbsolutePath;
                    data.endpoint = data.fullEndpoint.Substring(0, data.fullEndpoint.LastIndexOf("?") == -1 ? data.fullEndpoint.Length : data.fullEndpoint.LastIndexOf("?"));
                    if (!data.endpoint.EndsWith("/")) data.endpoint += "/";
                    data.host = new Uri(data.fullUri).Host;
                    data.duration = data.sideClose - data.sideOpen;
                    if (data.duration < 0) throw new Exception("Some idiot made a manual request with negative duration.");
                    data.openTime = TimeConverter.UnixTimeStampToDateTime(data.sideOpen);
                    data.closeTime = TimeConverter.UnixTimeStampToDateTime(data.sideClose);
                    data.queryString = data.fullUri.Contains("?") ? data.fullUri.Substring(data.fullUri.IndexOf("?")) : "";
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
