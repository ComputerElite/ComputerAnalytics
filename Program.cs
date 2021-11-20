using ComputerUtils.FileManaging;
using ComputerUtils.Logging;
using ComputerUtils.RandomExtensions;
using ComputerUtils.StringFormatters;
using ComputerUtils.VarUtils;
using ComputerUtils.Webserver;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComputerAnalytics
{
    class Program
    {
        static void Main(string[] args)
        {
            if(args.Length >= 1 && args[0] == "update")
            {
                Logger.Log("Replacing everything with zip contents.");
                Thread.Sleep(1000);
                string destDir = new DirectoryInfo(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory)).Parent.FullName + Path.DirectorySeparatorChar;
                using (ZipArchive archive = ZipFile.OpenRead(destDir + "updater" + Path.DirectorySeparatorChar +  "update.zip"))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        String name = entry.FullName;
                        if (name.EndsWith("/")) continue;
                        if (name.Contains("/")) Directory.CreateDirectory(destDir + Path.GetDirectoryName(name));
                        entry.ExtractToFile(destDir + entry.FullName, true);
                    }
                }
                ProcessStartInfo i = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "\"" + destDir + "ComputerAnalytics.dll\"",
                    UseShellExecute = true
                };
                Process.Start(i);
                Environment.Exit(0);
            }
            AnalyticsServer s = new AnalyticsServer();
            HttpServer server = new HttpServer();
            s.AddToServer(server);
        }
    }

    class AnalyticsServer
    {
        public HttpServer server = null;
        public AnalyticsDatabaseCollection collection = new AnalyticsDatabaseCollection();

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
            Logger.displayLogInConsole = true;
            collection.LoadAllDatabases();
            this.server = httpServer;
            server.StartServer(collection.config.port);
            /*
            if (serverUris == null) serverUris = new List<string>();
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
            */
            Logger.Log("Analytics will be send to " + collection.GetPublicAddress());
            server.AddRoute("POST", "/analytics", new Func<ServerRequest, bool>(request =>
            {
                string origin = request.context.Request.Headers.Get("Origin");
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
                    collection.AddAnalyticsToWebsite(origin, data);
                }
                catch (Exception e)
                {
                    Logger.Log("Error while accepting analytics json:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString(new AnalyticsResponse("error", "Error accepting json: " + e.Message).ToString(), "application/json", 500, true, new Dictionary<string, string>() { { "Access-Control-Allow-Origin", origin }, { "Access-Control-Allow-Credentials", "true" } });
                    return true;
                }
                Logger.Log("Added new analytics data: " + data.fileName);
                request.SendString(new AnalyticsResponse("success", "Analytic recieved").ToString(), "application/json", 200, true, new Dictionary<string, string>() { { "Access-Control-Allow-Origin", origin }, { "Access-Control-Allow-Credentials", "true" } });
                return true;
            }));
            server.AddRoute("OPTIONS", "/analytics", new Func<ServerRequest, bool>(request =>
            {
                string origin = request.context.Request.Headers.Get("Origin");
                request.SendData(new byte[0], "", 200, true, new Dictionary<string, string>() { { "Access-Control-Allow-Origin", collection.GetAllowedOrigin(origin) }, { "Access-Control-Allow-Methods", "POST, OPTIONS" }, { "Access-Control-Allow-Credentials", "true" }, { "Access-Control-Allow-Headers", "content-type" } });
                return true;
            }));
            server.AddRoute("GET", "/analytics.js", new Func<ServerRequest, bool>(request =>
            {
                string origin = request.queryString.Get("origin");
                if(origin == null)
                {
                    request.SendString("alert(`Add '?origin=YourSite' to analytics.js src. Replace YourSite with your site e. g. https://computerelite.github.io`)", "application/javascript") ;
                    return true;
                }
                request.SendString(ReadResource("analytics.js").Replace("{0}", "\"" + collection.GetPublicAddress() + "/\"").Replace("{1}", collection.GetPublicToken(origin)), "application/javascript");
                return true;
            }));
            server.AddRoute("GET", "/analytics", new Func<ServerRequest, bool>(request =>
            {
                if (IsNotLoggedIn(request)) return true;
                request.SendString(ReadResource("analytics.html"), "text/html");
                return true;
            }));
            server.AddRoute("GET", "/", new Func<ServerRequest, bool>(request =>
            {
                request.SendString(ReadResource("login.html"), "text/html");
                return true;
            }));
            server.AddRoute("GET", "/plotly.min.js", new Func<ServerRequest, bool>(request =>
            {
                request.SendString(ReadResource("plotly.js"), "application/javascript");
                return true;
            }));
            server.AddRoute("GET", "/analytics/endpoints", new Func<ServerRequest, bool>(request =>
            {
                try
                {
                    request.SendString(JsonSerializer.Serialize(collection.GetAllEndpointsWithAssociatedData(request)), "application/json");
                } catch(Exception e)
                {
                    Logger.Log("Error while crunching data:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString("Error: " + e.Message, "text/plain", 500);
                }
                
                return true;
            }));
            server.AddRoute("GET", "/analytics/date", new Func<ServerRequest, bool>(request =>
            {
                if (IsNotLoggedIn(request)) return true;
                try
                {
                    request.SendString(JsonSerializer.Serialize(collection.GetAllEndpointsSortedByDateWithAssociatedData(request)), "application/json");
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
                if (IsNotLoggedIn(request)) return true;
                try
                {
                    request.SendString(JsonSerializer.Serialize(collection.GetAllReferrersWithAssociatedData(request)), "application/json");
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
                if (IsNotLoggedIn(request)) return true;
                try
                {
                    request.SendString(JsonSerializer.Serialize(collection.GetAllQueryStringsWithAssociatedData(request)), "application/json");
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
                if (IsNotLoggedIn(request)) return true;
                try
                {
                    request.SendString(JsonSerializer.Serialize(collection.GetAllHostsWithAssociatedData(request)), "application/json");
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
                if (IsNotLoggedIn(request)) return true;
                try
                {
                    List<AnalyticsData> datas = JsonSerializer.Deserialize<List<AnalyticsData>>(request.bodyString);
                    int i = 0;
                    int rejected = 0;
                    string publicToken = collection.GetPublicTokenFromPrivateToken(GetToken(request));
                    foreach(AnalyticsData analyticsData in datas)
                    {
                        if (analyticsData.token == "") analyticsData.token = publicToken;
                        try
                        {
                            if (!collection.Contains(analyticsData))
                            {

                                collection.AddAnalyticsToWebsite(AnalyticsData.ImportAnalyticsEntry(analyticsData));
                                i++;

                            }
                        } catch (Exception e)
                        {
                            Logger.Log("Expection while importing Analytics data:\n" + e.Message, LoggingType.Warning);
                            rejected++;
                        }
                        
                    }
                    request.SendString("Imported " + i + " AnalyticsDatas, rejected " + rejected + " AnalyticsDatas");
                } catch (Exception e)
                {
                    request.SendString("Error: " + e.ToString(), "text/plain", 500);
                }
                return true;
            }));
            server.AddRoute("GET", "/websites", new Func<ServerRequest, bool>(request =>
            {
                if(GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(JsonSerializer.Serialize(collection.config.Websites), "application/json");
                return true;
            }));
            server.AddRoute("DELETE", "/website", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(collection.DeleteWebsite(request.bodyString));
                return true;
            }));
            server.AddRoute("POST", "/website", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(collection.CreateWebsite(request.bodyString), "application/json");
                return true;
            }));
            server.AddRoute("POST", "/renewtokens", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(collection.RenewTokens(request.bodyString), "application/json");
                return true;
            }));
            server.AddRoute("GET", "/manage", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(ReadResource("manage.html"), "text/html");
                return true;
            }));
            server.AddRoute("GET", "/login", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString("True");
                return true;
            }));
            server.AddRoute("POST", "/publicaddress", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(collection.SetPublicAddress(request.bodyString), "application/json");
                return true;
            }));
            server.AddRoute("GET", "/publicaddress", new Func<ServerRequest, bool>(request =>
            {
                request.SendString(collection.GetPublicAddress(), "application/json");
                return true;
            }));
            server.AddRoute("GET", "/export", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                string filename = DateTime.Now.Ticks + ".zip";
                Logger.Log("Exporting all data as zip. This may take a minute to do");
                ZipFile.CreateFromDirectory(collection.analyticsDir, filename);
                Logger.Log("Sending zip");
                request.SendFile(filename);
                File.Delete(filename);
                return true;
            }));
            server.AddRoute("GET", "/metrics", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                Metrics m = new Metrics();
                Process currentProcess = Process.GetCurrentProcess();
                m.ramUsage = currentProcess.WorkingSet64;
                m.ramUsageString = SizeConverter.ByteSizeToString(m.ramUsage);
                request.SendString(JsonSerializer.Serialize(m), "application/json");
                return true;
            }));
            server.AddRoute("POST", "/update", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                FileManager.RecreateDirectoryIfExisting("updater");
                string zip = "updater" + Path.DirectorySeparatorChar + "update.zip";
                File.WriteAllBytes(zip, request.bodyBytes);
                foreach(string s in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory))
                {
                    File.Copy(s, "updater" + Path.DirectorySeparatorChar + Path.GetFileName(s), true);
                }
                //Logger.Log("dotnet \"" + AppDomain.CurrentDomain.BaseDirectory + "updater" + Path.DirectorySeparatorChar + "ComputerAnalytics.dll\" update");
                request.SendString("Starting update. Please wait a bit and come back.");
                ProcessStartInfo i = new ProcessStartInfo
                {
                    Arguments = "\"" + AppDomain.CurrentDomain.BaseDirectory + "updater" + Path.DirectorySeparatorChar + "ComputerAnalytics.dll\" update",
                    UseShellExecute = true,
                    FileName = "dotnet"
                };
                Process.Start(i);
                Environment.Exit(0);
                return true;
            }));
        }

        public string GetToken(ServerRequest request)
        {
            Cookie token = request.cookies["token"];
            if (token == null)
            {
                request.Send403();
                return "";
            }
            return token.Value;
        }

        public bool IsNotLoggedIn(ServerRequest request)
        {
            if (!collection.DoesWebsiteWithPrivateTokenExist(GetToken(request)))
            {
                request.Send403();
                return true;
            }
            return false;
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

    class AnalyticsDatabaseCollection
    {
        public List<AnalyticsDatabase> databases { get; set; } = new List<AnalyticsDatabase>();
        public Config config = new Config();
        public string analyticsDir = "";

        public void LoadAllDatabases(string analyticsDir = "analytics")
        {
            if (!analyticsDir.EndsWith(Path.DirectorySeparatorChar)) analyticsDir += Path.DirectorySeparatorChar;
            this.analyticsDir = analyticsDir;
            Logger.Log("Loading all databases");
            FileManager.CreateDirectoryIfNotExisting(analyticsDir);
            if (!File.Exists(analyticsDir + "config.json")) SaveConfig();
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(analyticsDir + "config.json"));
            if (config.publicAddress == "") SetPublicAddress("http://localhost");
            for(int i = 0; i < config.Websites.Count; i++)
            {
                AnalyticsDatabase database = new AnalyticsDatabase(analyticsDir + config.Websites[i].folder);
                databases.Add(database);
            }
            Logger.Log("Loaded all databases");
        }

        public string SetPublicAddress(string newAddress)
        {
            config.publicAddress = newAddress;
            SaveConfig();
            return "Set public address to " + GetPublicAddress() + ".";
        }

        public string GetPublicAddress()
        {
            return config.publicAddress;
        }

        public string GetPublicToken(string origin)
        {
            foreach(Website w in config.Websites)
            {
                if (w.url == origin) return w.publicToken;
            }
            return "";
        }

        public string GetPublicTokenFromPrivateToken(string privateToken)
        {
            foreach (Website w in config.Websites)
            {
                if (w.privateToken == privateToken) return w.publicToken;
            }
            return "";
        }

        public string CreateRandomToken()
        {
            string token = RandomExtension.CreateToken();
            while(config.usedTokens.Contains(token))
            {
                token = RandomExtension.CreateToken();
            }
            config.usedTokens.Add(token);
            return token;
        }

        public int GetDatabaseIndexWithPublicToken(string publicToken, string origin)
        {
            for (int i = 0; i < config.Websites.Count; i++)
            {
                if (config.Websites[i].publicToken == publicToken && config.Websites[i].url == origin)
                {
                    return i;
                }
            }
            return -1;
        }

        public int GetDatabaseIndexWithPublicToken(string publicToken)
        {
            for (int i = 0; i < config.Websites.Count; i++)
            {
                if (config.Websites[i].publicToken == publicToken)
                {
                    return i;
                }
            }
            return -1;
        }

        public int GetDatabaseIndexWithPrivateToken(string privateToken)
        {
            for (int i = 0; i < config.Websites.Count; i++)
            {
                if (config.Websites[i].privateToken == privateToken)
                {
                    return i;
                }
            }
            return -1;
        }

        public List<AnalyticsData> GetAnalyticsForWebsite(string privateToken)
        {
            for(int i = 0; i <config.Websites.Count; i++)
            {
                if(config.Websites[i].privateToken == privateToken)
                {
                    return databases[i].data;
                }
            }

            throw new Exception("Website not registered");
        }

        public void AddAnalyticsToWebsite(string origin, AnalyticsData data)
        {
            int i = GetDatabaseIndexWithPublicToken(data.token, origin);
            if (i == -1) throw new Exception("Website not registered");
            databases[i].AddAnalyticData(data);
            return;
        }

        public void AddAnalyticsToWebsite(AnalyticsData data)
        {
            int i = GetDatabaseIndexWithPublicToken(data.token);
            if (i == -1) throw new Exception("Website not registered");
            databases[i].AddAnalyticData(data);
            return;
        }

        public bool Contains(AnalyticsData d)
        {
            int i = GetDatabaseIndexWithPublicToken(d.token);
            if (i == -1) throw new Exception("Website not registered");

            for (int ii = 0; ii < databases[i].data.Count; ii++)
            {
                if (d.Equals(databases[i].data[ii])) return true;
            }
            return false;
        }

        public string GetAllowedOrigin(string origin)
        {
            foreach(Website w in config.Websites)
            {
                if(w.url == origin) return w.url;
            }
            return "";
        }

        public string CreateWebsite(string host) // Host e. g. https://computerelite.github.io
        {
            Website website = new Website();
            website.url = host;
            website.publicToken = CreateRandomToken();
            website.privateToken = CreateRandomToken();
            website.folder = StringFormatter.FileNameSafe(host).Replace("https", "").Replace("http", "") + Path.DirectorySeparatorChar;
            if (Directory.Exists(analyticsDir + website.folder)) return "Website already exists";
            AnalyticsDatabase database = new AnalyticsDatabase(analyticsDir + website.folder);
            databases.Add(database);
            config.Websites.Add(website);
            SaveConfig();
            return "Created " + website.url;
        }

        public string DeleteWebsite(string url)
        {
            for(int i = 0; i < config.Websites.Count; i++)
            {
                if(config.Websites[i].url == url)
                {
                    Directory.Delete(analyticsDir + config.Websites[i].folder, true);
                    config.Websites.RemoveAt(i);
                    SaveConfig();
                    return "Deleted " + url + " including all Analytics";
                }
            }
            return "Website not registered";
        }

        public string RenewTokens(string url)
        {
            for (int i = 0; i < config.Websites.Count; i++)
            {
                if (config.Websites[i].url == url)
                {
                    config.Websites[i].publicToken = CreateRandomToken();
                    config.Websites[i].privateToken = CreateRandomToken();
                    SaveConfig();
                    return "Renewed tokens for " + url;
                }
            }
            return "Website not registered";
        }

        public void SaveConfig()
        {
            if(config.masterToken == "") config.masterToken = CreateRandomToken();
            File.WriteAllText(analyticsDir + "config.json", JsonSerializer.Serialize(config));
        }

        public bool DoesWebsiteWithPublicTokenExist(string publicToken, string origin)
        {
            return GetDatabaseIndexWithPublicToken(publicToken, origin) != -1;
        }

        public bool DoesWebsiteWithPrivateTokenExist(string privateToken)
        {
            foreach (Website website in config.Websites)
            {
                if (website.privateToken == privateToken) return true;
            }
            return false;
        }

        public List<AnalyticsEndpoint> GetAllEndpointsWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if(i == -1) return new List<AnalyticsEndpoint>();
            return databases[i].GetAllEndpointsWithAssociatedData(null, queryString);
        }

        public List<AnalyticsHost> GetAllHostsWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsHost>();
            return databases[i].GetAllHostsWithAssociatedData(null, queryString);
        }

        public List<AnalyticsDate> GetAllEndpointsSortedByDateWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsDate>();
            return databases[i].GetAllEndpointsSortedByDateWithAssociatedData(null, queryString);
        }

        public List<AnalyticsReferrer> GetAllReferrersWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsReferrer>();
            return databases[i].GetAllReferrersWithAssociatedData(null, queryString);
        }

        public List<AnalyticsQueryString> GetAllQueryStringsWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsQueryString>();
            return databases[i].GetAllQueryStringsWithAssociatedData(null, queryString);
        }
    }

    class AnalyticsDatabase
    {
        public static string analyticsDirectory { get; set; } = "analytics" + Path.DirectorySeparatorChar;
        public List<AnalyticsData> data { get; set; } = new List<AnalyticsData>();

        public AnalyticsDatabase(string analyticsDir = "analytics")
        {
            if (!analyticsDir.EndsWith(Path.DirectorySeparatorChar)) analyticsDir += Path.DirectorySeparatorChar;
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

        public List<AnalyticsEndpoint> GetAllEndpointsWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching endpoints with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsEndpoint> endpoints = new Dictionary<string, AnalyticsEndpoint>();
            for (int i = 0; i < usedData.Count; i++)
            {
                if (IsNotValid(usedData[i], queryString)) continue;
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

        public List<AnalyticsHost> GetAllHostsWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching hosts with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsHost> hosts = new Dictionary<string, AnalyticsHost>();
            for (int i = 0; i < usedData.Count; i++)
            {
                if (IsNotValid(usedData[i], queryString)) continue;
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

        public List<AnalyticsDate> GetAllEndpointsSortedByDateWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching endpoints sorted by date with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsDate> dates = new Dictionary<string, AnalyticsDate>();
            for(int i = 0; i < usedData.Count; i++)
            {
                if (IsNotValid(usedData[i], queryString)) continue;
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

        public List<AnalyticsReferrer> GetAllReferrersWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching referrers with all data for " + usedData.Count + " analytics just for the idiot wanting to view it");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsReferrer> referrers = new Dictionary<string, AnalyticsReferrer>();
            for (int i = 0; i < usedData.Count; i++)
            {
                if (IsNotValid(usedData[i], queryString)) continue;
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

        public bool IsNotValid(AnalyticsData d, NameValueCollection queryString)
        {
            if (queryString == null) return false;
            if (queryString.Get("host") != null && d.host != queryString.Get("host")) return true;
            if (queryString.Get("endpoint") != null && d.endpoint != queryString.Get("endpoint")) return true;
            if (queryString.Get("querystring") != null && d.queryString != queryString.Get("querystring")) return true;
            if (queryString.Get("referrer") != null && d.referrer != queryString.Get("referrer")) return true;
            if (queryString.Get("date") != null && !queryString.Get("date").Split(',').Contains(d.openTime.ToString("dd.MM.yyyy"))) return true;
            return false;
        }

        public List<AnalyticsQueryString> GetAllQueryStringsWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            if (usedData == null) usedData = data;
            Logger.Log("Crunching QueryStrings with all data for " + usedData.Count + " analytics just for a really idiotic idiot");
            Stopwatch s = Stopwatch.StartNew();
            Dictionary<string, AnalyticsQueryString> queryStrings = new Dictionary<string, AnalyticsQueryString>();
            for (int i = 0; i < usedData.Count; i++)
            {
                if (IsNotValid(usedData[i], queryString)) continue;
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
        public string token { get; set; } = "";
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
