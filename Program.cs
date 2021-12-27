using ComputerUtils.Discord;
using ComputerUtils.FileManaging;
using ComputerUtils.Logging;
using ComputerUtils.RandomExtensions;
using ComputerUtils.StringFormatters;
using ComputerUtils.VarUtils;
using ComputerUtils.Webserver;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
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
using System.Text.RegularExpressions;
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
        public AnalyticsDatabaseCollection collection = null;
        public Dictionary<string, string> replace = new Dictionary<string,string> { { "<", ""}, { ">", ""}, { "\\u003C", "" }, { "\\u003E", "" } };

        /// <summary>
        /// Adds analytics functionality to a existing server
        /// </summary>
        /// <param name="httpServer">server to which you want to add analytics functionality</param>
        public void AddToServer(HttpServer httpServer)
        {
            AppDomain.CurrentDomain.UnhandledException += HandleExeption;
            Logger.displayLogInConsole = true;
            
            collection = new AnalyticsDatabaseCollection(this);
            this.server = httpServer;
            server.StartServer(collection.config.port);
            Logger.Log("Public address: " + collection.GetPublicAddress());
            SendMasterWebhookMessage("Server started", "The server has just started", 0x42BBEB);
            Thread warningSystemsAndSiteMetrics = new Thread(() =>
            {
                int iteration = 0;
                TimeSpan waiting = new TimeSpan(24, 0, 0);
                while (true)
                {
                    if (iteration == 1)
                    {
                        try
                        {
                            double sleep = (waiting - (DateTime.UtcNow - collection.config.lastWebhookUpdate)).TotalMinutes;
                            Logger.Log("Warning Systems and Site metrics waiting " + sleep + " minutes until next execution");
                            Thread.Sleep(sleep <= 0 ? 0 : (int)Math.Round(sleep * 60 * 1000));
                        }
                        catch (Exception ex)
                        {
                            Logger.Log("Error while waiting: " + ex.ToString(), LoggingType.Warning);
                        }
                        SendMasterWebhookMessage("ComputerAnalytics metrics report", "__**Those metrics are for the last " + (DateTime.UtcNow - collection.config.lastWebhookUpdate).TotalHours + " hours**__\n\n**Analytics recieved:** `" + collection.config.recievedAnalytics + "`\n**Rejected Analytics:** `" + collection.config.rejectedAnalytics + "`\n**Current Ram usage:**`" + SizeConverter.ByteSizeToString(Process.GetCurrentProcess().WorkingSet64) + "`\n\n_Next update in approximately " + waiting.TotalHours + " hours_", 0x1CAD15);
                        collection.config.recievedAnalytics = 0;
                        collection.config.rejectedAnalytics = 0;
                        for (int i = 0; i < collection.config.Websites.Count; i++)
                        {
                            Website w = collection.config.Websites[i];
                            if (w.discordWebhookUrl != "")
                            {
                                try
                                {
                                    Logger.Log("Sending webhook for " + w.url);
                                    DiscordWebhook webhook = new DiscordWebhook(w.discordWebhookUrl);
                                    webhook.SendEmbed("Site metrics report", "__**Those metrics are for the last " + (DateTime.UtcNow - w.lastWebhookUpdate).TotalHours + " hours**__\n\n**Site clicks:** `" + w.siteClicks + "`\n_If you want more details check the analytics page_\n\n_Next update in approximately " + waiting.TotalHours + " hours_", w.url + " " + DateTime.UtcNow, "ComputerAnalytics", "https://computerelite.github.io/assets/CE_512px.png", collection.GetPublicAddress(), "https://computerelite.github.io/assets/CE_512px.png", collection.GetPublicAddress(), 0x1CAD15);
                                } catch (Exception ex)
                                {
                                    Logger.Log("Exception while sending webhook" + ex.ToString(), LoggingType.Warning);
                                }
                                collection.config.Websites[i].lastWebhookUpdate = DateTime.UtcNow;
                            }
                            collection.config.Websites[i].siteClicks = 0;
                        }
                        collection.config.lastWebhookUpdate = DateTime.UtcNow;
                        collection.SaveConfig();
                        iteration = -1;
                    }
                    iteration++;
                }
            });
            warningSystemsAndSiteMetrics.Start();
            Logger.Log("Analytics will be send to " + collection.GetPublicAddress());
            server.AddRoute("POST", "/analytics", new Func<ServerRequest, bool>(request =>
            {
                string origin = request.context.Request.Headers.Get("Origin");
                AnalyticsData data = new AnalyticsData();
                try
                {
                    data = AnalyticsData.Recieve(request);
                    collection.config.recievedAnalytics++;
                } catch(Exception e)
                {
                    collection.config.rejectedAnalytics++;
                    collection.SaveConfig();
                    SendMasterWebhookMessage("ComputerAnalytics rejected analytic", "**Reason:** `" + e.Message + "`\n**UA:** `" + request.context.Request.UserAgent + "`", 0xDA3633);
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
                    request.SendStringReplace(JsonSerializer.Serialize(collection.GetAllEndpointsWithAssociatedData(request)), "application/json", 200, replace);
                } catch(Exception e)
                {
                    Logger.Log("Error while crunching data:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString("Error: " + e.Message, "text/plain", 500);
                }
                return true;
            }));
            server.AddRoute("GET", "/analytics/time", new Func<ServerRequest, bool>(request =>
            {
                if (IsNotLoggedIn(request)) return true;
                try
                {
                    request.SendStringReplace(JsonSerializer.Serialize(collection.GetAllEndpointsSortedByTimeWithAssociatedData(request)), "application/json", 200, replace);
                }
                catch (Exception e)
                {
                    Logger.Log("Error while crunching data:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString("Error: " + e.Message, "text/plain", 500);
                }
                return true;
            }));
            server.AddRoute("GET", "/analytics/screen", new Func<ServerRequest, bool>(request =>
            {
                if (IsNotLoggedIn(request)) return true;
                try
                {
                    request.SendStringReplace(JsonSerializer.Serialize(collection.GetAllScreensWithAssociatedData(request)), "application/json", 200, replace);
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
                    request.SendStringReplace(JsonSerializer.Serialize(collection.GetAllReferrersWithAssociatedData(request)), "application/json", 200, replace);
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
                    request.SendStringReplace(JsonSerializer.Serialize(collection.GetAllHostsWithAssociatedData(request)), "application/json", 200, replace);
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
            server.AddRoute("POST", "/renewmastertoken", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(collection.RenewMasterToken(), "application/json");
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
            server.AddRoute("GET", "/requestexport", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                string filename = "export.zip";
                request.SendString("the zip is being generated. Check /export to get the file once it's available");
                //request.SendString("requested export. Please head to /export");
                Logger.Log("Exporting all data as zip. This may take a minute to do");
                try
                {
                    if (File.Exists(filename)) File.Delete(filename);
                } catch
                {
                    Logger.Log("Could not delete old export.zip");
                    return true;
                }
                
                ZipFile.CreateFromDirectory(collection.analyticsDir, filename);
                Logger.Log("zip created");
                return true;
            }));
            server.AddRoute("GET", "/export", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                if(!File.Exists("export.zip"))
                {
                    request.SendString("File does not exist yet. Please refresh this site in a bit or request a new export zip from /requestexport", "text/plain", 425);
                    return true;
                }
                
                Logger.Log("Sending zip");
                try
                {
                    request.SendFile("export.zip");
                    File.Delete("export.zip");
                } catch { request.SendString("File exists but isn't ready. Please refresh this site in a bit or request a new export zip from /requestexport", "text/plain", 425); }
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
                m.workingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                request.SendString(JsonSerializer.Serialize(m), "application/json");
                return true;
            }));
            server.AddRoute("GET", "/test", new Func<ServerRequest, bool>(request =>
            {
                string s = "";
                foreach(string header in request.context.Request.Headers.AllKeys)
                {
                    s += header + ": " + request.context.Request.Headers[header] + "  <br>";
                }
                request.SendString(s, "text/html");
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
                SendMasterWebhookMessage("ComputerAnalytics Update Deployed", "**Changelog:** `" + (request.queryString.Get("changelog") == null ? "none" : request.queryString.Get("changelog")) + "`", 0x42BBEB);
                string zip = "updater" + Path.DirectorySeparatorChar + "update.zip";
                File.WriteAllBytes(zip, request.bodyBytes);
                foreach(string s in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory))
                {
                    if (s.EndsWith("zip")) continue;
                    Logger.Log("Copying " + s);
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
            server.AddRoute("GET", "/console", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                Logger.saveOutputInVariable = true;
                request.SendString(ReadResource("console.html"), "text/html");
                return true;
            }));
            server.AddRoute("GET", "/consoleoutput", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendStringReplace(Logger.log, "text/plain", 200, replace);
                return true;
            }));
            server.AddRoute("POST", "/restart", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString("Restarting");
                ProcessStartInfo i = new ProcessStartInfo
                {
                    Arguments = "\"" + AppDomain.CurrentDomain.BaseDirectory + "ComputerAnalytics.dll\"",
                    UseShellExecute = true,
                    FileName = "dotnet"
                };
                Process.Start(i);
                Environment.Exit(0);
                return true;
            }));
            server.AddRoute("POST", "/shutdown", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString("Shutting down");
                Environment.Exit(0);
                return true;
            }));
            server.AddRoute("GET", "/script.js", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken && IsNotLoggedIn(request))
                {
                    request.Send403();
                    return true;
                }
                request.SendString(ReadResource("script.js"), "application/javascript");
                return true;
            }));
            server.AddRoute("GET", "/style.css", new Func<ServerRequest, bool>(request =>
            {
                request.SendString(ReadResource("style.css"), "text/css");
                return true;
            }));
            server.AddRoute("POST", "/masterwebhook", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                collection.config.masterDiscordWebhookUrl = request.bodyString;
                collection.SaveConfig();
                request.SendString("Set masterwebhook");
                return true;
            }));
            server.AddRoute("GET", "/masterwebhook", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(collection.config.masterDiscordWebhookUrl);
                return true;
            }));
            server.AddRoute("POST", "/webhook", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                collection.SaveConfig();
                request.SendString(collection.SetWebhook(request.queryString.Get("url"), request.bodyString));
                return true;
            }));
            server.AddRoute("GET", "/privacy", new Func<ServerRequest, bool>(request =>
            {
                request.Redirect("https://github.com/ComputerElite/ComputerAnalytics/wiki");
                return true;
            }));
            server.AddRoute("GET", "/analytics/docs", new Func<ServerRequest, bool>(request =>
            {
                request.SendString(ReadResource("api.txt"));
                return true;
            }));
            server.AddRoute("GET", "/config", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(JsonSerializer.Serialize(collection.config), "application/json");
                return true;
            }));
            server.AddRoute("POST", "/config", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                collection.config = JsonSerializer.Deserialize<Config>(request.bodyString);
                collection.SaveConfig();
                request.SendString("Updated config. Please restart the server to apply the changes you did.");
                return true;
            }));
            // Do all stuff after server setup
            collection.LoadAllDatabases();
            collection.ReorderDataOfAllDataSetsV1();
        }

        public void HandleExeption(object sender, UnhandledExceptionEventArgs args)
        {
            SendMasterWebhookMessage("Critical Unhandled Exception", "ComputerAnalytics managed to crash. Well done Developer: " + ((Exception)args.ExceptionObject).ToString().Substring(0, 1900), 0xFF0000);
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

        public void SendMasterWebhookMessage(string title, string description, int color)
        {
            if (collection.config.masterDiscordWebhookUrl == "") return;
            try
            {
                Logger.Log("Sending master webhook");
                DiscordWebhook webhook = new DiscordWebhook(collection.config.masterDiscordWebhookUrl);
                webhook.SendEmbed(title, description, "master " + DateTime.UtcNow, "ComputerAnalytics", "https://computerelite.github.io/assets/CE_512px.png", collection.GetPublicAddress(), "https://computerelite.github.io/assets/CE_512px.png", collection.GetPublicAddress(), color);
            }
            catch (Exception ex)
            {
                Logger.Log("Exception while sending webhook" + ex.ToString(), LoggingType.Warning);
            }
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
        public AnalyticsServer parentServer = null;
        public MongoClient mongoClient = null;

        public AnalyticsDatabaseCollection(AnalyticsServer parent)
        {
            this.parentServer = parent;
        }

        public void LoadAllDatabases(string analyticsDir = "analytics")
        {
            if (!analyticsDir.EndsWith(Path.DirectorySeparatorChar)) analyticsDir += Path.DirectorySeparatorChar;
            this.analyticsDir = analyticsDir;
            Logger.Log("Loading all databases");
            FileManager.CreateDirectoryIfNotExisting(analyticsDir);
            if(!File.Exists(analyticsDir + "config.json")) SaveConfig();
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(analyticsDir + "config.json"));
            if (config.publicAddress == "") SetPublicAddress("http://localhost");
            if (config.useMongoDB)
            {
                mongoClient = new MongoClient(config.mongoDBUrl);
            }
            for (int i = 0; i < config.Websites.Count; i++)
            {
                AnalyticsDatabase database = new AnalyticsDatabase(config.Websites[i].url, this, analyticsDir + config.Websites[i].folder);
                databases.Add(database);
            }
            SaveConfig();
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

        public void AddAnalyticsToWebsite(string origin, AnalyticsData data)
        {
            int i = GetDatabaseIndexWithPublicToken(data.token, origin);
            if (i == -1) throw new Exception("Website not registered");
            config.Websites[i].siteClicks++;
            databases[i].AddAnalyticData(data);
            SaveConfig();
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
            throw new Exception("This method is deprecated");
            /*
            return false;
            int i = GetDatabaseIndexWithPublicToken(d.token);
            if (i == -1) throw new Exception("Website not registered");

            foreach(string s in Directory.EnumerateFiles(databases[i].analyticsDirectory))
            {
                if (d.Equals(AnalyticsData.Load(s))) return true;
            }
            return false;
            */
        }

        public string GetAllowedOrigin(string origin)
        {
            foreach(Website w in config.Websites)
            {
                if(w.url == origin) return w.url;
            }
            return "";
        }

        public string SetWebhook(string url, string webhookUrl)
        {
            for(int i = 0; i < config.Websites.Count; i++)
            {
                if(config.Websites[i].url == url)
                {
                    config.Websites[i].discordWebhookUrl = webhookUrl;
                    SaveConfig();
                    return "Set webhook for " + url;
                }
            }
            return "url does not exist";
        }

        public string CreateWebsite(string host) // Host e. g. https://computerelite.github.io
        {
            Website website = new Website();
            website.url = host;
            website.publicToken = CreateRandomToken();
            website.privateToken = CreateRandomToken();
            website.folder = StringFormatter.FileNameSafe(host).Replace("https", "").Replace("http", "") + Path.DirectorySeparatorChar;
            if(config.useMongoDB)
            {
                if (mongoClient.ListDatabaseNames().ToList().FirstOrDefault(x => x == website.url) != null) return "Website already exists";
            } else
            {
                if (Directory.Exists(analyticsDir + website.folder)) return "Website already exists";
            }
            AnalyticsDatabase database = new AnalyticsDatabase(website.url, this, analyticsDir + website.folder);
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

                    if(Directory.Exists(analyticsDir + config.Websites[i].folder)) Directory.Delete(analyticsDir + config.Websites[i].folder, true);
                    if (config.useMongoDB)
                    {
                        Logger.Log("Dropping MongoDBCollection");
                        mongoClient.GetDatabase(config.mongoDBName).DropCollection(config.Websites[i].url);
                    }
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

        public string RenewMasterToken()
        {
            config.masterToken = CreateRandomToken();
            SaveConfig();
            return "New Master Token is " + config.masterToken + ". Save it somewhere safe!";
        }

        public void SaveConfig()
        {
            if(config.masterToken == "") RenewMasterToken();
            Logger.Log("Saving config");
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

        public void ReorderDataOfAllDataSetsV1()
        {
            for(int i = 0; i < databases.Count; i++)
            {
                //databases[i].ReOrderDataFromV1();
            }
        }

        public List<AnalyticsEndpoint> GetAllEndpointsWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if(i == -1) return new List<AnalyticsEndpoint>();
            Logger.Log("Crunching Endpoints of database for " + config.Websites[i].url + " just because some idiot needs them");
            return databases[i].GetAllEndpointsWithAssociatedData(null, queryString);
        }

        public List<AnalyticsHost> GetAllHostsWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsHost>();
            Logger.Log("Crunching Hosts of database for " + config.Websites[i].url + " just because they were requested by some ugly guy");
            return databases[i].GetAllHostsWithAssociatedData(null, queryString);
        }

        public List<AnalyticsTime> GetAllEndpointsSortedByTimeWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsTime>();
            Logger.Log("Crunching Endpoints Sorted by time of database for " + config.Websites[i].url + " just because of DN");
            return databases[i].GetAllEndpointsSortedByTimeWithAssociatedData(null, queryString);
        }

        public List<AnalyticsScreen> GetAllScreensWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsScreen>();
            Logger.Log("Crunching screens of database for " + config.Websites[i].url + " just because of all those interesting sizes");
            return databases[i].GetAllScreensWithAssociatedData(null, queryString);
        }

        public List<AnalyticsReferrer> GetAllReferrersWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsReferrer>();
            Logger.Log("Crunching Referrers of database for " + config.Websites[i].url + " just because of IsPinkCute == true");
            return databases[i].GetAllReferrersWithAssociatedData(null, queryString);
        }
    }

    class AnalyticsDatabase
    {
        public IMongoCollection<BsonDocument> documents = null;
        public AnalyticsDatabaseCollection parentCollection = null;
        public MongoClient parentMongoClient = null;
        public string analyticsDirectory = "";
        //public List<AnalyticsData> data { get; set; } = new List<AnalyticsData>();

        public AnalyticsDatabase(string collectionName, AnalyticsDatabaseCollection parentCollection, string analyticsDir)
        {
            this.parentCollection = parentCollection;
            this.parentMongoClient = parentCollection.mongoClient;
            this.analyticsDirectory = analyticsDir;
            bool log = Logger.displayLogInConsole;
            Logger.displayLogInConsole = true;

            if (parentCollection.config.useMongoDB)
            {
                
                Logger.Log("Loading MongoDB Collection");
                IMongoDatabase database = parentCollection.mongoClient.GetDatabase(parentCollection.config.mongoDBName);
                documents = database.GetCollection<BsonDocument>(collectionName);
                if (parentCollection.config.migrateOldDataToMongoDB)
                {
                    UploadDatabaseToMongoDB();
                }
            } else
            {
                FileManager.CreateDirectoryIfNotExisting(analyticsDirectory);
            }
            
            Logger.displayLogInConsole = log;
        }

        public IEnumerable<AnalyticsData> GetIterator(bool useQueryString = true)
        {
            DateTime lastTime = DateTime.Now - new TimeSpan(days, hours, minutes, seconds);
            Logger.Log("Gettings Enumerable for all files from " + lastTime.ToString() + " to " + DateTime.Now);
            long  lastTimeUnix = TimeConverter.DateTimeToUnixTimestamp(lastTime);
            if(parentCollection.config.useMongoDB)
            {
                BsonDocument filter = new BsonDocument("sideClose", new BsonDocument("$gte", lastTimeUnix));
                //if (host != null && d.host != host) return true;
                if (endpoint != null) filter.Add(new BsonElement("endpoint", endpoint));
                if (referrer != null) filter.Add(new BsonElement("referrer", referrer));
                if (screenwidth != null) filter.Add(new BsonElement("screenWidth", screenwidth));
                if (screenheight != null) filter.Add(new BsonElement("screenHeight", screenheight));
                IEnumerable<BsonDocument> docs = documents.Find(filter).ToEnumerable();
                foreach (BsonDocument document in docs)
                {
                    yield return BsonSerializer.Deserialize<AnalyticsData>(document);
                }
                filter = null;
                docs = null;
            } else
            {
                for (DateTime time = lastTime; time.Date <= DateTime.Now.Date; time = time.AddDays(1))
                {
                    string d = analyticsDirectory + time.ToString("dd.MM.yyyy") + Path.DirectorySeparatorChar;
                    if (Directory.Exists(d))
                    {
                        foreach (string f in Directory.EnumerateFiles(d))
                        {
                            if (DoesFilenameMatchRequirements(f)) continue;
                            AnalyticsData data = AnalyticsData.Load(f);
                            yield return data;
                            data = null;
                        }
                    }
                }
            }
            GC.Collect();
        }

        public void UploadDatabaseToMongoDB()
        {
            if (!Directory.Exists(analyticsDirectory)) return;
            Thread t = new Thread(() =>
            {
                if (File.Exists(analyticsDirectory + "migratedTo" + StringFormatter.FileNameSafe(parentCollection.config.mongoDBUrl))) return;
                Logger.Log("Migrating local database to MongoDB");
                int done = 0;
                foreach (string d in Directory.EnumerateDirectories(analyticsDirectory))
                {
                    if (File.Exists(d + "migratedTo" + StringFormatter.FileNameSafe(parentCollection.config.mongoDBUrl))) continue;
                    foreach (string f in Directory.EnumerateFiles(d))
                    {

                        BsonDocument doc = BsonSerializer.Deserialize<BsonDocument>(File.ReadAllText(f));
                        if (documents.Find<BsonDocument>(doc).CountDocuments() >= 1) continue;
                        documents.InsertOne(doc);
                        done++;
                        if (done % 100 == 0) Logger.Log("Migrated " + done + " files");
                    }
                    File.WriteAllText(d + "migratedTo" + StringFormatter.FileNameSafe(parentCollection.config.mongoDBUrl), "");
                }
                Logger.Log("Finished. Migrated " + done + "files");
                File.WriteAllText(analyticsDirectory + "migratedTo" + StringFormatter.FileNameSafe(parentCollection.config.mongoDBUrl), "");
            });
            t.Start();
        }

        public void ReOrderDataFromV1()
        {
            throw new NotImplementedException();
            /*
            
            foreach(string d in Directory.EnumerateDirectories(analyticsDirectory))
            {
                foreach(string f in Directory.EnumerateFiles(d))
                {
                    collection.InsertOne(BsonSerializer.Deserialize<BsonDocument>(File.ReadAllText(f)));
                }
            }
            
            int reordered = 0;
            Logger.Log("Reordering Analytics in base folder");
            foreach(string f in Directory.GetFiles(analyticsDirectory))
            {
                AnalyticsData data = AnalyticsData.Load(f);
                string newDir = analyticsDirectory + data.closeTime.ToString("dd.MM.yyyy") + Path.DirectorySeparatorChar;
                FileManager.CreateDirectoryIfNotExisting(newDir);
                Logger.Log("moving " + f + " to " + newDir + data.fileName);
                File.Move(f, newDir + data.fileName, true);
                reordered++;
            }
            Logger.Log("Reordered " + reordered + " Analytics into new folder sorted by date");
            */
        }

        public void AddAnalyticData(AnalyticsData analyticsData)
        {
            if(parentCollection.config.useMongoDB)
            {
                documents.InsertOne(analyticsData.ToBsonDocument());
                Logger.Log("Added " + analyticsData.fileName + " to MongoDB collection");
            } else
            {
                string f = analyticsDirectory + analyticsData.closeTime.ToString("dd.MM.yyyy") + Path.DirectorySeparatorChar;
                FileManager.CreateDirectoryIfNotExisting(f);
                File.WriteAllText(f + analyticsData.fileName, analyticsData.ToString());
            }
            
        }

        public void DeleteOldAnalytics(TimeSpan maxTime)
        {
            throw new NotImplementedException();
            /*
            DateTime now = DateTime.UtcNow;
            List<string> toDelete = new List<string>();
            foreach(string f in Directory.EnumerateDirectories(analyticsDirectory))
            {
                DateTime d = DateTime.ParseExact(Path.GetFileName(f), "dd.MM.yyyy", null);
                if (now - d > maxTime)
                {
                    
                    toDelete.Add(f);
                }
                
            }
            for(int i = 0; i < toDelete.Count; i++)
            {
                try
                {
                    Logger.Log("Deleting " + toDelete[i]);
                    Directory.Delete(toDelete[i], true);
                } catch (Exception e)
                {
                    Logger.Log("Analytics date folder failed to delete while cleanup:\n" + e.ToString(), LoggingType.Warning);
                }
            }
            */
        }

        public List<AnalyticsEndpoint> GetAllEndpointsWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            PreCalculate(queryString);
            Dictionary<string, AnalyticsEndpoint> endpoints = new Dictionary<string, AnalyticsEndpoint>();
            foreach(AnalyticsData data in usedData == null ? GetIterator() : usedData)
            {
                if (!endpoints.ContainsKey(data.endpoint))
                {
                    endpoints.Add(data.endpoint, new AnalyticsEndpoint());
                    endpoints[data.endpoint].endpoint = data.endpoint;
                    endpoints[data.endpoint].host = data.host;
                    endpoints[data.endpoint].fullUri = data.fullUri.Split('?')[0];
                }
                endpoints[data.endpoint].clicks++;
                endpoints[data.endpoint].totalDuration += data.duration;
                if (endpoints[data.endpoint].maxDuration < data.duration) endpoints[data.endpoint].maxDuration = data.duration;
                if (endpoints[data.endpoint].minDuration > data.duration) endpoints[data.endpoint].minDuration = data.duration;
                endpoints[data.endpoint].data.Add(data);
                if (!endpoints[data.endpoint].ips.Contains(data.remote))
                {
                    endpoints[data.endpoint].uniqueIPs++;
                    endpoints[data.endpoint].ips.Add(data.remote);
                }
            }
            List<AnalyticsEndpoint> endpointsL = endpoints.Values.OrderBy(x => x.clicks).ToList();
            endpoints.Clear();
            endpointsL.ForEach(new Action<AnalyticsEndpoint>(e =>
            {
                e.avgDuration = e.totalDuration / (double)e.clicks;
                if(deep)
                {
                    e.referrers = GetAllReferrersWithAssociatedData(e.data, queryString);
                }
            }));
            return endpointsL;
        }

        public List<AnalyticsHost> GetAllHostsWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            PreCalculate(queryString);
            Dictionary<string, AnalyticsHost> hosts = new Dictionary<string, AnalyticsHost>();
            foreach (AnalyticsData data in usedData == null ? GetIterator() : usedData)
            {
                if (!hosts.ContainsKey(data.host))
                {
                    hosts.Add(data.host, new AnalyticsHost());
                    hosts[data.host].host = data.host;
                }
                hosts[data.host].totalClicks++;
                hosts[data.host].data.Add(data);
                hosts[data.host].totalDuration += data.duration;
                if (hosts[data.host].maxDuration < data.duration) hosts[data.host].maxDuration = data.duration;
                if (hosts[data.host].minDuration > data.duration) hosts[data.host].minDuration = data.duration;
                if (!hosts[data.host].ips.Contains(data.remote))
                {
                    hosts[data.host].totalUniqueIPs++;
                    hosts[data.host].ips.Add(data.remote);
                }
            }
            List<AnalyticsHost> hostsL = hosts.Values.OrderBy(x => x.totalClicks).ToList();
            hosts.Clear();
            hostsL.ForEach(new Action<AnalyticsHost>(h => {
                h.endpoints = GetAllEndpointsWithAssociatedData(h.data, queryString);
                h.avgDuration = h.totalDuration / (double)h.totalClicks;
            }));
            return hostsL;
        }

        public string GetTimeString(AnalyticsData data)
        {
            if(timeunit == TimeUnit.date) return data.openTime.ToString("dd.MM.yyyy");
            if (timeunit == TimeUnit.hour) return data.openTime.ToString("HH");
            if (timeunit == TimeUnit.minute) return data.openTime.ToString("mm");
            return "";
        }

        public List<AnalyticsTime> GetAllEndpointsSortedByTimeWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            PreCalculate(queryString);
            Dictionary<string, AnalyticsTime> times = new Dictionary<string, AnalyticsTime>();
            foreach (AnalyticsData data in usedData == null ? GetIterator() : usedData)
            {
                string date = GetTimeString(data);
                if(!times.ContainsKey(date))
                {
                    times.Add(date, new AnalyticsTime());
                    times[date].time = date;
                    times[date].unix = ((DateTimeOffset)data.openTime).ToUnixTimeSeconds();
                }
                times[date].totalClicks++;
                times[date].data.Add(data);
                times[date].totalDuration += data.duration;
                if (times[date].maxDuration < data.duration) times[date].maxDuration = data.duration;
                if (times[date].minDuration > data.duration) times[date].minDuration = data.duration;
                if (!times[date].ips.Contains(data.remote))
                {
                    times[date].totalUniqueIPs++;
                    times[date].ips.Add(data.remote);
                }
            }
            times = Sorter.Sort(times);
            List<AnalyticsTime> datesL = times.Values.OrderBy(x => x.unix).ToList();
            times.Clear();
            datesL.ForEach(new Action<AnalyticsTime>(d => {
                d.avgDuration = d.totalDuration / (double)d.totalClicks;
            }));
            return datesL;
        }

        public List<AnalyticsScreen> GetAllScreensWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            PreCalculate(queryString);
            Dictionary<string, AnalyticsScreen> screens = new Dictionary<string, AnalyticsScreen>();
            foreach (AnalyticsData data in usedData == null ? GetIterator() : usedData)
            {
                string screen = data.screenWidth + "," + data.screenHeight;
                if (!screens.ContainsKey(screen))
                {
                    screens.Add(screen, new AnalyticsScreen());
                    screens[screen].screenWidth = data.screenWidth;
                    screens[screen].screenHeight = data.screenHeight;
                }
                screens[screen].clicks++;
                screens[screen].data.Add(data);
                screens[screen].totalDuration += data.duration;
                if (screens[screen].maxDuration < data.duration) screens[screen].maxDuration = data.duration;
                if (screens[screen].minDuration > data.duration) screens[screen].minDuration = data.duration;
                if (!screens[screen].ips.Contains(data.remote))
                {
                    screens[screen].uniqueIPs++;
                    screens[screen].ips.Add(data.remote);
                }
            }
            screens = Sorter.Sort(screens);
            List<AnalyticsScreen> screensL = screens.Values.ToList();
            screens.Clear();
            // Remove screens with fewer than 2 users
            for(int i = 0; i < screensL.Count; i++)
            {
                if (screensL[i].uniqueIPs <= 1)
                {
                    screensL.RemoveAt(i);
                    i--;
                    continue;
                }
                screensL[i].avgDuration = screensL[i].totalDuration / (double)screensL[i].clicks;
            }
            screensL = screensL.OrderBy(s => s.clicks).ToList();
            screensL.Reverse();
            return screensL;
        }

        public List<AnalyticsReferrer> GetAllReferrersWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            PreCalculate(queryString);
            Dictionary<string, AnalyticsReferrer> referrers = new Dictionary<string, AnalyticsReferrer>();
            foreach (AnalyticsData data in usedData == null ? GetIterator() : usedData)
            {
                if (!referrers.ContainsKey(data.referrer))
                {
                    referrers.Add(data.referrer, new AnalyticsReferrer(data.referrer));
                }
                referrers[data.referrer].referred++;
                referrers[data.referrer].totalDuration += data.duration;
                if (referrers[data.referrer].maxDuration < data.duration) referrers[data.referrer].maxDuration = data.duration;
                if (referrers[data.referrer].minDuration > data.duration) referrers[data.referrer].minDuration = data.duration;
                if (!referrers[data.referrer].ips.Contains(data.remote))
                {
                    referrers[data.referrer].uniqueIPs++;
                    referrers[data.referrer].ips.Add(data.remote);
                }
            }
            List<AnalyticsReferrer> referrersL = referrers.Values.OrderBy(x => x.referred).ToList();
            referrers.Clear();
            referrersL.ForEach(new Action<AnalyticsReferrer>(e => e.avgDuration = e.totalDuration / (double)e.referred));
            return referrersL;
        }

        // cache query string parameters for smol performance boost
        public string host = null;
        public string endpoint = null;
        public string referrer = null;
        public string screenwidth = null;
        public string screenheight = null;
        public TimeUnit timeunit = TimeUnit.date;
        public string[] time = null;
        public bool deep = false;

        public int days = 0;
        public int hours = 0;
        public int minutes = 0;
        public int seconds = 0;

        public DateTime now = DateTime.UtcNow;

        public void PreCalculate(NameValueCollection c)
        {
            if (c == null) c = new NameValueCollection();
            host = c.Get("host");
            endpoint = c.Get("endpoint");
            referrer = c.Get("referrer");
            screenheight = c.Get("screenheight");
            screenwidth = c.Get("screenwidth");
            timeunit = (TimeUnit)Enum.Parse(typeof(TimeUnit), c.Get("timeunit") == null ? "date" : c.Get("timeunit").ToLower());
            time = c.Get("time") == null ? null : c.Get("time").Split(',');
            deep = c.Get("deep") != null;

            hours = c.Get("hours") != null && Regex.IsMatch(c.Get("hours"), "[0-9]+") ? Convert.ToInt32(c.Get("hours")) : 0;
            minutes = c.Get("minutes") != null && Regex.IsMatch(c.Get("minutes"), "[0-9]+") ? Convert.ToInt32(c.Get("minutes")) : 0;
            seconds = c.Get("seconds") != null && Regex.IsMatch(c.Get("seconds"), "[0-9]+") ? Convert.ToInt32(c.Get("seconds")) : 0;
            days = c.Get("days") != null && Regex.IsMatch(c.Get("days"), "[0-9]+") ? Convert.ToInt32(c.Get("days")) : 0;

            if (days == 0 && hours == 0 && minutes == 0 && seconds == 0) days = 7;
            
            Logger.Log(days + " " + hours + " " + minutes + " " + seconds);
        }

        public bool IsNotValid(AnalyticsData d)
        {
            return false;
            //if (host != null && d.host != host) return true;
            if (endpoint != null && d.endpoint != endpoint) return true;
            if (referrer != null && d.referrer != referrer) return true;
            if (screenwidth != null && d.screenWidth.ToString() != screenwidth) return true;
            if (screenheight != null && d.screenHeight.ToString() != screenheight) return true;
            if (time != null && !time.Contains(GetTimeString(d))) return true;

            return false;
        }

        public bool IsTimeSpanNotValid(TimeSpan span)
        {
            if (days != 0 && span.TotalDays > days) return true;
            if (hours != 0 && span.TotalHours > hours) return true;
            if (minutes != 0 && span.TotalMinutes > minutes) return true;
            if (seconds != 0 && span.TotalSeconds > seconds) return true;
            return false;

        }

        public bool DoesFilenameMatchRequirements(string filename)
        {
            filename = Path.GetFileName(filename);
            return IsTimeSpanNotValid(now - TimeConverter.UnixTimeStampToDateTime(long.Parse(filename.Split(new char[] { '_', '.' })[2])));
        }
    }

    public enum TimeUnit
    {
        date,
        hour,
        minute
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

        //public List<AnalyticsQueryString> queryStrings { get; set; } = new List<AnalyticsQueryString>();
        public List<AnalyticsEndpoint> endpoints { get; set; } = new List<AnalyticsEndpoint>();
        //public List<AnalyticsReferrer> referrers { get; set; } = new List<AnalyticsReferrer>();
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

    public class AnalyticsTime
    {
        public long totalClicks { get; set; } = 0;
        public long totalUniqueIPs { get; set; } = 0;
        public string time { get; set; } = "";
        public long unix { get; set; } = 0;
        public long minDuration { get; set; } = long.MaxValue;
        public long maxDuration { get; set; } = 0;
        public double avgDuration { get; set; } = 0.0;
        public long totalDuration { get; set; } = 0;

        //public List<AnalyticsEndpoint> endpoints { get; set; } = new List<AnalyticsEndpoint>();
        //public List<AnalyticsReferrer> referrers { get; set; } = new List<AnalyticsReferrer>();
        public List<AnalyticsData> data = new List<AnalyticsData>();
        public List<string> ips = new List<string>();
    }

    public class AnalyticsScreen
    {
        public long clicks { get; set; } = 0;
        public long uniqueIPs { get; set; } = 0;
        public long screenWidth { get; set; } = 0;
        public long screenHeight { get; set; } = 0;
        public long minDuration { get; set; } = long.MaxValue;
        public long maxDuration { get; set; } = 0;
        public double avgDuration { get; set; } = 0.0;
        public long totalDuration { get; set; } = 0;

        //public List<AnalyticsEndpoint> endpoints { get; set; } = new List<AnalyticsEndpoint>();
        //public List<AnalyticsReferrer> referrers { get; set; } = new List<AnalyticsReferrer>();
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
        public long screenWidth { get; set; } = 0;// unix
        public long screenHeight { get; set; } = 0;// unix

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
            switch(data.analyticsVersion)
            {
                case "1.0":
                    // data.endpoint = request.path; idiot, this will return /analytics
                    data.fullUri = data.fullUri.Split('?')[0];
                    data.fullEndpoint = new Uri(data.fullUri).AbsolutePath;
                    data.endpoint = data.fullEndpoint.Substring(0, data.fullEndpoint.LastIndexOf("?") == -1 ? data.fullEndpoint.Length : data.fullEndpoint.LastIndexOf("?"));
                    if (!data.endpoint.EndsWith("/")) data.endpoint += "/";
                    data.host = new Uri(data.fullUri).Host;
                    data.uA = request.context.Request.UserAgent;
                    data.remote = request.context.Request.Headers["X-Forwarded-For"] == null ? request.context.Request.RemoteEndPoint.Address.ToString() : request.context.Request.Headers["X-Forwarded-For"];
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
