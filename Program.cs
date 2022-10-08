using ComputerUtils.CommandLine;
using ComputerUtils.Discord;
using ComputerUtils.Encryption;
using ComputerUtils.FileManaging;
using ComputerUtils.Logging;
using ComputerUtils.QR;
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
using System.Collections;
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
            Logger.displayLogInConsole = true;
            CommandLineCommandContainer cla = new CommandLineCommandContainer(args);
            cla.AddCommandLineArgument(new List<string> { "--workingdir" }, false, "Sets the working Directory for ComputerAnalytics", "directory", "");
            cla.AddCommandLineArgument(new List<string> { "update", "--update", "-U" }, true, "Starts in update mode (use with caution. It's best to let it do on it's own)");
            cla.AddCommandLineArgument(new List<string> { "--displayMasterToken", "-dmt" }, true, "Outputs the master token without starting the server");
            if (cla.HasArgument("help"))
            {
                cla.ShowHelp();
                return;
            }
            
            string workingDir = cla.GetValue("--workingdir");
            if (cla.HasArgument("update"))
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
                    Arguments = "\"" + destDir + "ComputerAnalytics.dll\" --workingdir \"" + workingDir + "\"",
                    UseShellExecute = true
                };
                Process.Start(i);
                Environment.Exit(0);
            }
            if (!workingDir.EndsWith(Path.DirectorySeparatorChar)) workingDir += Path.DirectorySeparatorChar;
            if (workingDir == Path.DirectorySeparatorChar.ToString()) workingDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!File.Exists(workingDir + "analytics" + Path.DirectorySeparatorChar + "config.json")) File.WriteAllText(workingDir + "analytics" + Path.DirectorySeparatorChar + "config.json", JsonSerializer.Serialize(new Config()));
            Config c = JsonSerializer.Deserialize<Config>(File.ReadAllText(workingDir + "analytics" + Path.DirectorySeparatorChar + "config.json"));
            if (cla.HasArgument("-dmt"))
            {
                QRCodeGeneratorWrapper.Display(c.masterToken);
                return;
            }
            AnalyticsServer s = new AnalyticsServer();
            s.workingDir = workingDir;
            HttpServer server = new HttpServer();
            s.AddToServer(server);
        }
    }

    class AnalyticsServer
    {
        public HttpServer server = null;
        public AnalyticsDatabaseCollection collection = null;
        public Dictionary<string, string> replace = new Dictionary<string,string> { { "<", ""}, { ">", ""}, { "\\u003C", "" }, { "\\u003E", "" } };
        public string workingDir = "";

        /// <summary>
        /// Adds analytics functionality to a existing server
        /// </summary>
        /// <param name="httpServer">server to which you want to add analytics functionality</param>
        public void AddToServer(HttpServer httpServer)
        {
            AppDomain.CurrentDomain.UnhandledException += HandleExeption;
            Logger.displayLogInConsole = true;

            if (!workingDir.EndsWith(Path.DirectorySeparatorChar)) workingDir += Path.DirectorySeparatorChar;
            if (workingDir == Path.DirectorySeparatorChar.ToString()) workingDir = AppDomain.CurrentDomain.BaseDirectory;

            Logger.Log("Working directory is " + workingDir);
            Logger.Log("Analytics directory is " + workingDir + "analytics");
            collection = new AnalyticsDatabaseCollection(this);
            this.server = httpServer;
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
            server.AddRoute("GET", "/randomtoken", new Func<ServerRequest, bool>(request =>
            {
                request.SendString(RandomExtension.CreateToken());
                return true;
            }));
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
                    //SendMasterWebhookMessage("ComputerAnalytics rejected analytic", "**Reason:** `" + e.Message + "`\n**UA:** `" + request.context.Request.UserAgent + "`", 0xDA3633);
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
                request.automaticHeaders.Add("Access-Control-Allow-Origin", request.queryString.Get("origin"));
                request.SendString(ReadResource("analytics.js").Replace("{0}", "\"" + collection.GetPublicAddress() + "/\"").Replace("{1}", collection.GetPublicToken(origin)), "application/javascript");
                return true;
            }), false, true, true, true, 0, true, 0);
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
            server.AddRoute("GET", "/analytics/countries", new Func<ServerRequest, bool>(request =>
            {
                try
                {
                    request.SendStringReplace(JsonSerializer.Serialize(collection.GetAllCountriesWithAssociatedData(request)), "application/json", 200, replace);
                }
                catch (Exception e)
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
            server.AddRoute("GET", "/analytics/newusers", new Func<ServerRequest, bool>(request =>
            {
                if (IsNotLoggedIn(request)) return true;
                try
                {
                    request.SendStringReplace(JsonSerializer.Serialize(collection.GetNewUsersPerDay(request)), "application/json", 200, replace);
                }
                catch (Exception e)
                {
                    Logger.Log("Error while crunching data:\n" + e.ToString(), LoggingType.Warning);
                    request.SendString("Error: " + e.Message, "text/plain", 500);
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
                request.SendString(collection.RenewTokens(request.bodyString.Split('|')[0], request.bodyString.Split('|')[1]), "application/json"); // url|token
                return true;
            }));
            server.AddRoute("POST", "/addtoken", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(collection.AddToken(request.bodyString.Split('|')[0], request.bodyString.Split('|')[1]), "application/json"); // url|expires
                return true;
            }));
            server.AddRoute("POST", "/deletetoken", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                request.SendString(collection.RemoveToken(request.bodyString.Split('|')[0], request.bodyString.Split('|')[1]), "application/json"); // url|token
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
                string filename = workingDir + "export.zip";
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
                if(!File.Exists(workingDir + "export.zip"))
                {
                    request.SendString("File does not exist yet. Please refresh this site in a bit or request a new export zip from /requestexport", "text/plain", 425);
                    return true;
                }
                
                Logger.Log("Sending zip");
                try
                {
                    request.SendFile(workingDir + "export.zip");
                    File.Delete(workingDir + "export.zip");
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
                m.workingDirectory = workingDir;
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
                FileManager.RecreateDirectoryIfExisting(AppDomain.CurrentDomain.BaseDirectory + "updater");
                SendMasterWebhookMessage("ComputerAnalytics Update Deployed", "**Changelog:** `" + (request.queryString.Get("changelog") == null ? "none" : request.queryString.Get("changelog")) + "`", 0x42BBEB);
                string zip = AppDomain.CurrentDomain.BaseDirectory + "updater" + Path.DirectorySeparatorChar + "update.zip";
                File.WriteAllBytes(zip, request.bodyBytes);
                foreach(string s in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory))
                {
                    if (s.EndsWith("zip")) continue;
                    Logger.Log("Copying " + s);
                    File.Copy(s, AppDomain.CurrentDomain.BaseDirectory + "updater" + Path.DirectorySeparatorChar + Path.GetFileName(s), true);
                }
                //Logger.Log("dotnet \"" + AppDomain.CurrentDomain.BaseDirectory + "updater" + Path.DirectorySeparatorChar + "ComputerAnalytics.dll\" update");
                request.SendString("Starting update. Please wait a bit and come back.");
                ProcessStartInfo i = new ProcessStartInfo
                {
                    Arguments = "\"" + AppDomain.CurrentDomain.BaseDirectory + "updater" + Path.DirectorySeparatorChar + "ComputerAnalytics.dll\" update --workingdir \"" + workingDir.Substring(0, workingDir.Length - 1) + "\"",
                    UseShellExecute = true,
                    FileName = "dotnet"
                };
                Process.Start(i);
                Environment.Exit(0);
                return true;
            }));
            server.AddRoute("POST", "/updateoculusdb", new Func<ServerRequest, bool>(request =>
            {
                if (GetToken(request) != collection.config.masterToken)
                {
                    request.Send403();
                    return true;
                }
                FileManager.RecreateDirectoryIfExisting("/mnt/DiscoExt/OculusDB/updater");
                SendMasterWebhookMessage("OculusDB vy ComputerAnalytics Update Deployed", "**Changelog:** `" + (request.queryString.Get("changelog") == null ? "none" : request.queryString.Get("changelog")) + "`", 0x42BBEB);
                string zip = "/mnt/DiscoExt/OculusDB/updater" + Path.DirectorySeparatorChar + "update.zip";
                File.WriteAllBytes(zip, request.bodyBytes);
                request.SendString("Starting update. Please wait a bit and come back.");
                string destDir = "/mnt/DiscoExt/OculusDB/";
                using (ZipArchive archive = ZipFile.OpenRead(zip))
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
                    Arguments = "\"" + destDir + "OculusDB.dll\" --workingdir \"/mnt/DiscoExt/OculusDB/\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                Process p = Process.Start(i);
                p.OutputDataReceived += (sender, args) =>
                {
                    Logger.Log("OculusDB: " + args.Data);
                };
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
                request.SendString(ReadResource("privacy.html"), "text/html");
                //request.Redirect("https://github.com/ComputerElite/ComputerAnalytics/wiki");
                return true;
            }));
            server.AddRoute("GET", "/privacy.txt", new Func<ServerRequest, bool>(request =>
            {
                request.SendString(ReadResource("privacy.txt").Replace("{0}", collection.config.useMongoDB ? "via MongoDB" : "locally").Replace("{1}", collection.config.geoLocationEnabled ? "cand to check from which country the most visitors are" : ""));
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
            collection.LoadAllDatabases(workingDir + "analytics");
            collection.ReorderDataOfAllDataSetsV1();
            server.StartServer(collection.config.port);
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
            config.Fix();
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
                if (w.HasPrivateToken(privateToken)) return w.publicToken;
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

        public string AddToken(string url, string expires)
        {
            DateTime expiration = DateTime.Parse(expires);
            for (int i = 0; i < config.Websites.Count; i++)
            {
                if (config.Websites[i].url == url)
                {
                    config.Websites[i].privateTokens.Add(new Token(CreateRandomToken(), expiration));
                    SaveConfig();
                    return "Added token which expires on " + expiration.ToString() + " for " + url;
                }
            }
            return "Website not registered";
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
                if (config.Websites[i].HasPrivateToken(privateToken))
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
            website.privateTokens.Add(new Token(CreateRandomToken(), DateTime.MaxValue));
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

        public string RenewTokens(string url, string token)
        {
            for (int i = 0; i < config.Websites.Count; i++)
            {
                if (config.Websites[i].url == url)
                {
                    if(token == config.Websites[i].publicToken) config.Websites[i].publicToken = CreateRandomToken();
                    if (token == "")
                    {
                        config.Websites[i].publicToken = CreateRandomToken();
                        for (int ii = 0; ii < config.Websites[i].privateTokens.Count; ii++)
                        {
                            config.Websites[i].privateTokens[ii].value = CreateRandomToken();
                        }
                    } else
                    {
                        for(int ii = 0; ii < config.Websites[i].privateTokens.Count; ii++)
                        {
                            if (config.Websites[i].privateTokens[ii].value == token) config.Websites[i].privateTokens[ii].value = CreateRandomToken();
                        }
                    }
                    config.Fix();
                    SaveConfig();
                    return "Renewed tokens (" + token + ") for " + url;
                }
            }
            return "Website not registered";
        }

        public string RemoveToken(string url, string token)
        {
            for (int i = 0; i < config.Websites.Count; i++)
            {
                if (config.Websites[i].url == url)
                {
                    
                    for (int ii = 0; ii < config.Websites[i].privateTokens.Count; ii++)
                    {
                        if (config.Websites[i].privateTokens[ii].value == token)
                        {
                            config.Websites[i].privateTokens.RemoveAt(ii);
                            break;
                        }
                    }
                    config.Fix();
                    SaveConfig();
                    return "Deleted token " + token + " from " + url;
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
                if (website.HasPrivateToken(privateToken)) return true;
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

        public List<AnalyticsAggregationQueryResult<AnalyticsEndpointId>> GetAllEndpointsWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if(i == -1) return new List<AnalyticsAggregationQueryResult<AnalyticsEndpointId>>();
            Logger.Log("Crunching Endpoints of database for " + config.Websites[i].url + " just because some idiot needs them");
            return databases[i].GetAllEndpointsWithAssociatedData(null, queryString);
        }

        public List<AnalyticsAggregationQueryResult<AnalyticsCountryId>> GetAllCountriesWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsAggregationQueryResult<AnalyticsCountryId>>();
            Logger.Log("Crunching Countries of database for " + config.Websites[i].url + " just because there are so many countries");
            return databases[i].GetAllCountriesWithAssociatedData(null, queryString);
        }

        public List<AnalyticsAggregationQueryResult<AnalyticsTimeId>> GetAllEndpointsSortedByTimeWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsAggregationQueryResult<AnalyticsTimeId>>();
            Logger.Log("Crunching Endpoints Sorted by time of database for " + config.Websites[i].url + " just because of DN");
            return databases[i].GetAllEndpointsSortedByTimeWithAssociatedData(null, queryString);
        }

        public List<AnalyticsAggregationQueryResult<AnalyticsScreenId>> GetAllScreensWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsAggregationQueryResult<AnalyticsScreenId>>();
            Logger.Log("Crunching screens of database for " + config.Websites[i].url + " just because of all those interesting sizes");
            return databases[i].GetAllScreensWithAssociatedData(null, queryString);
        }

        public List<AnalyticsAggregationQueryResult<AnalyticsReferrerId>> GetAllReferrersWithAssociatedData(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsAggregationQueryResult<AnalyticsReferrerId>>();
            Logger.Log("Crunching Referrers of database for " + config.Websites[i].url + " just because of IsPinkCute == true");
            return databases[i].GetAllReferrersWithAssociatedData(null, queryString);
        }

        public List<AnalyticsAggregationNewUsersResult> GetNewUsersPerDay(ServerRequest request)
        {
            string privateToken = request.cookies["token"] == null ? "" : request.cookies["token"].Value;
            NameValueCollection queryString = request.queryString;
            int i = GetDatabaseIndexWithPrivateToken(privateToken);
            if (i == -1) return new List<AnalyticsAggregationNewUsersResult>();
            Logger.Log("Crunching New Users per day of database for " + config.Websites[i].url + " because ComputerElite fucking took hours to create the mongodb query for this");
            return databases[i].GetNewUsersPerDay(queryString);
        }
    }

    class AnalyticsDatabase
    {
        public IMongoCollection<BsonDocument> documents = null;
        public AnalyticsDatabaseCollection parentCollection = null;
        public MongoClient parentMongoClient = null;
        public string analyticsDirectory = "";
        public string collectionName = "";
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
                this.collectionName = collectionName;
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

                IEnumerable<BsonDocument> docs = documents.Find(GetFilter()).ToEnumerable();
                foreach (BsonDocument document in docs)
                {
                    yield return BsonSerializer.Deserialize<AnalyticsData>(document);
                }
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
                            if (IsNotValid(data)) continue;
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
                return;
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

        public long GetLastTimeUnix()
        {
            DateTime lastTime = DateTime.Now - new TimeSpan(days, hours, minutes, seconds);
            Logger.Log("Gettings Filter from " + lastTime.ToString() + " to " + DateTime.Now);
            return TimeConverter.DateTimeToUnixTimestamp(lastTime);
        }

        public BsonDocument GetFilter(BsonElement[] filters = null, bool withTime = true)
        {
            long lastTimeUnix = GetLastTimeUnix();
            BsonDocument filter = new BsonDocument();
            //if (host != null && d.host != host) return true;
            if (withTime) filter.Add(new BsonElement("sideClose", new BsonDocument("$gte", lastTimeUnix)));
            if (endpoint != null) filter.Add(new BsonElement("endpoint", endpoint));
            if (referrer != null) filter.Add(new BsonElement("referrer", referrer));
            if (screenwidth != null) filter.Add(new BsonElement("screenWidth", screenwidth));
            if (screenheight != null) filter.Add(new BsonElement("screenHeight", screenheight));
            if(time != null)
            {
                if(timeunit == TimeUnit.date)
                {
                    filter.Add("$expr", new BsonDocument("$eq", new BsonArray { new BsonDocument("$concat", new BsonArray
                                {
                                    new BsonDocument("$toString",
                                    new BsonDocument("$dayOfMonth",
                                    new BsonDocument("$toDate", "$closeTime"))),
                                    ".",
                                    new BsonDocument("$toString",
                                    new BsonDocument("$month",
                                    new BsonDocument("$toDate", "$closeTime"))),
                                    ".",
                                    new BsonDocument("$toString",
                                    new BsonDocument("$year",
                                    new BsonDocument("$toDate", "$closeTime")))
                                }), time }));
                }
                else if (timeunit == TimeUnit.hour)
                {
                    filter.Add("$expr", new BsonDocument("$eq", new BsonArray { 
                                    new BsonDocument("$toString",
                                    new BsonDocument("$hour",
                                    new BsonDocument("$toDate", "$closeTime")))
                                ,time }));
                }
                else if (timeunit == TimeUnit.minute)
                {
                    filter.Add("$expr", new BsonDocument("$eq", new BsonArray {
                                    new BsonDocument("$toString",
                                    new BsonDocument("$minute",
                                    new BsonDocument("$toDate", "$closeTime")))
                                ,time }));
                }
            }
            if(filters != null)
            {
                foreach (BsonElement filterElement in filters) filter.Add(filterElement);
            }
            if (countryCode != null) filter.Add(new BsonElement("geolocation.countryCode", countryCode));
            return new BsonDocument("$match", filter);
        }

        public BsonDocument[] GetGroupQuery(BsonDocument id, BsonDocument returnId, BsonElement[] filter = null)
        {
            return new BsonDocument[]
                {
                    GetFilter(filter),
                    new BsonDocument("$group",
                        new BsonDocument
                            {
                                { "_id",
                                    id },
                                { "ipcount",
                        new BsonDocument("$sum", 1) },
                                { "minDuration",
                        new BsonDocument("$min", "$duration") },
                                { "maxDuration",
                        new BsonDocument("$max", "$duration") },
                                { "avgDuration",
                        new BsonDocument("$avg", "$duration") },
                                { "totalDuration",
                        new BsonDocument("$sum", "$duration") },
                                {"closeTime", new BsonDocument("$first", "$closeTime") }
                        }),
                    new BsonDocument("$group",
                        new BsonDocument
                            {
                                { "_id",
                                    returnId },
                                { "totalUniqueIPs",
                        new BsonDocument("$sum", 1) },
                                { "totalClicks",
                        new BsonDocument("$sum", "$ipcount") },
                                { "minDuration",
                        new BsonDocument("$min", "$minDuration") },
                                { "maxDuration",
                        new BsonDocument("$max", "$maxDuration") },
                                { "avgDuration",
                        new BsonDocument("$avg", "$avgDuration") },
                                { "totalDuration",
                        new BsonDocument("$sum", "$totalDuration") },
                                {"closeTime", new BsonDocument("$first", "$closeTime") }
                    }),
                    new BsonDocument("$sort", new BsonDocument("totalClicks", -1))
                };
        }

        public BsonDocument[] GetNewUsersPerDayQuery(BsonArray time, BsonArray time2, BsonDocument sort)
        {
            return new BsonDocument[]
            {
                GetFilter(null, true), // might wanna change that to false for more accurate results. But it's harder to filter after doing all the stuff below so you should filter by time here
                new BsonDocument("$sort",
                new BsonDocument("sideClose", 1)),
                new BsonDocument("$group",
                new BsonDocument
                    {
                        { "_id",
                new BsonDocument("remote", "$remote") },
                        { "firstDate",
                new BsonDocument("$first", "$closeTime") },
                        { "ipCount",
                new BsonDocument("$sum", 1) },
                        { "allDates",
                new BsonDocument("$push", "$closeTime") }
                    }),
                new BsonDocument("$unwind", "$allDates"),
                new BsonDocument("$group",
                new BsonDocument
                    {
                        { "_id",
                new BsonDocument
                        {
                            { "time",
                new BsonDocument("$concat",
                time) },
                            { "remote", "$_id.remote" }
                        } },
                        { "totalClicks",
                new BsonDocument("$sum", 1) },
                        {
                            "sideClose", new BsonDocument("$first", sort)
                        },
                        { "newClicks",
                new BsonDocument("$first",
                new BsonDocument("$switch",
                new BsonDocument
                                {
                                    { "default", 0 },
                                    { "branches",
                new BsonArray
                                    {
                                        new BsonDocument
                                        {
                                            { "case",
                                        new BsonDocument("$eq",
                                        new BsonArray
                                                {
                                                    new BsonDocument("$concat", time),
                                                    new BsonDocument("$concat", time2),
                                                }) },
                                            { "then", 1 }
                                        }
                                    } }
                                })) }
                    }),
                new BsonDocument("$group",
                new BsonDocument
                    {
                        { "_id",
                new BsonDocument("time", "$_id.time") },
                        { "totalClicks",
                new BsonDocument("$sum", "$totalClicks") },
                        { "newIPs",
                new BsonDocument("$sum", "$newClicks") },
                        { "totalUniqueIPs",
                new BsonDocument("$sum", 1) },
                    {"sideClose", new BsonDocument("$first", "$sideClose")}
                    }),
                new BsonDocument("$addFields",
                new BsonDocument
                    {
                        { "returningIPs",
                new BsonDocument("$subtract",
                new BsonArray
                            {
                                "$totalUniqueIPs",
                                "$newIPs"
                            }
                        )
                    }
                }),
                new BsonDocument("$sort",
                new BsonDocument("sideClose", 1)),
                //new BsonDocument("$addFields",
                //new BsonDocument("sideCloseLong",
                //new BsonDocument("$toLong", "$sideClose"))),
                //new BsonDocument("$match",
                //new BsonDocument("sideClose",
                //new BsonDocument("$gte", TimeConverter.DateTimeToJavaTimestamp(TimeConverter.UnixTimeStampToDateTime(GetLastTimeUnix())))))
            };
        }

        public List<AnalyticsAggregationNewUsersResult> GetNewUsersPerDay(NameValueCollection queryString)
        {
            PreCalculate(queryString);
            if(!parentCollection.config.useMongoDB)
            {
                return new List<AnalyticsAggregationNewUsersResult>();
            }
            BsonArray time = new BsonArray();
            BsonArray time2 = new BsonArray();
            BsonDocument sort = new BsonDocument();
            if(timeunit == TimeUnit.date)
            {
                time = new BsonArray
                                {
                                    new BsonDocument("$toString",
                                    new BsonDocument("$dayOfMonth",
                                    new BsonDocument("$toDate", "$allDates"))),
                                    ".",
                                    new BsonDocument("$toString",
                                    new BsonDocument("$month",
                                    new BsonDocument("$toDate", "$allDates"))),
                                    ".",
                                    new BsonDocument("$toString",
                                    new BsonDocument("$year",
                                    new BsonDocument("$toDate", "$allDates")))
                                };
                time2 = new BsonArray
                                {
                                    new BsonDocument("$toString",
                                    new BsonDocument("$dayOfMonth",
                                    new BsonDocument("$toDate", "$firstDate"))),
                                    ".",
                                    new BsonDocument("$toString",
                                    new BsonDocument("$month",
                                    new BsonDocument("$toDate", "$firstDate"))),
                                    ".",
                                    new BsonDocument("$toString",
                                    new BsonDocument("$year",
                                    new BsonDocument("$toDate", "$firstDate")))
                                };
                sort = new BsonDocument("$toDate", "$allDates");
            } else if(timeunit == TimeUnit.hour)
            {
                time = new BsonArray
                                {
                                    new BsonDocument("$toString",
                                    new BsonDocument("$hour",
                                    new BsonDocument("$toDate", "$allDates")))
                                };
                time2 = new BsonArray
                                {
                                    new BsonDocument("$toString",
                                    new BsonDocument("$hour",
                                    new BsonDocument("$toDate", "$firstDate")))
                                };
                sort = new BsonDocument("$hour", new BsonDocument("$toDate", "$allDates"));
            }
            else if (timeunit == TimeUnit.minute)
            {
                time = new BsonArray
                                {
                                    new BsonDocument("$toString",
                                    new BsonDocument("$minute",
                                    new BsonDocument("$toDate", "$allDates")))
                                };
                time2 = new BsonArray
                                {
                                    new BsonDocument("$toString",
                                    new BsonDocument("$minute",
                                    new BsonDocument("$toDate", "$firstDate")))
                                };
                sort = new BsonDocument("$minute", new BsonDocument("$toDate", "$allDates"));
            }
            return documents.Aggregate<AnalyticsAggregationNewUsersResult>(GetNewUsersPerDayQuery(time, time2, sort)).ToList();
        }

        public List<AnalyticsAggregationQueryResult<AnalyticsEndpointId>> GetAllEndpointsWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            PreCalculate(queryString);
            if(parentCollection.config.useMongoDB)
            {
                return documents.Aggregate<AnalyticsAggregationQueryResult<AnalyticsEndpointId>>(GetGroupQuery(new BsonDocument
                                {
                                    { "remote", "$remote" },
                                    { "endpoint", "$endpoint" },
                                    { "host", "$host" },
                                    { "fullUri", "$fullUri" }
                                },
                                new BsonDocument
                                {
                                    { "endpoint", "$_id.endpoint" },
                                    { "host", "$_id.host" },
                                    { "fullUri", "$_id.fullUri" }
                                })).ToList();
            }
            Dictionary<string, AnalyticsAggregationQueryResult<AnalyticsEndpointId>> endpoints = new Dictionary<string, AnalyticsAggregationQueryResult<AnalyticsEndpointId>>();
            foreach (AnalyticsData data in usedData == null ? GetIterator() : usedData)
            {
                if (!endpoints.ContainsKey(data.endpoint))
                {
                    endpoints.Add(data.endpoint, new AnalyticsAggregationQueryResult<AnalyticsEndpointId>());
                    endpoints[data.endpoint]._id.endpoint = data.endpoint;
                    endpoints[data.endpoint]._id.host = data.host;
                    endpoints[data.endpoint]._id.fullUri = data.fullUri.Split('?')[0];
                }
                endpoints[data.endpoint].totalClicks++;
                endpoints[data.endpoint].totalDuration += data.duration;
                if (endpoints[data.endpoint].maxDuration < data.duration) endpoints[data.endpoint].maxDuration = data.duration;
                if (endpoints[data.endpoint].minDuration > data.duration) endpoints[data.endpoint].minDuration = data.duration;
                if (!endpoints[data.endpoint].ips.Contains(data.remote))
                {
                    endpoints[data.endpoint].totalUniqueIPs++;
                    endpoints[data.endpoint].ips.Add(data.remote);
                }
            }
            List<AnalyticsAggregationQueryResult<AnalyticsEndpointId>> endpointsL = endpoints.Values.OrderBy(x => x.totalClicks).ToList();
            endpoints.Clear();
            endpointsL.ForEach(new Action<AnalyticsAggregationQueryResult<AnalyticsEndpointId>>(e =>
            {
                e.avgDuration = e.totalDuration / (double)e.totalClicks;
            }));
            return endpointsL;
        }

        public List<AnalyticsAggregationQueryResult<AnalyticsCountryId>> GetAllCountriesWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            PreCalculate(queryString);
            if(parentCollection.config.useMongoDB)
            {
                return documents.Aggregate<AnalyticsAggregationQueryResult<AnalyticsCountryId>>(GetGroupQuery(new BsonDocument
                                {
                                    { "remote", "$remote" },
                                    { "countryCode", "$geolocation.countryCode" }
                                },
                                new BsonDocument
                                {
                                    { "countryCode", "$_id.countryCode" }
                                }, new BsonElement[] {new BsonElement("geolocation",new BsonDocument("$exists", true)), new BsonElement("geolocation.countryCode", new BsonDocument("$exists", true)) })).ToList();
            }
            Dictionary<string, AnalyticsAggregationQueryResult<AnalyticsCountryId>> countries = new Dictionary<string, AnalyticsAggregationQueryResult<AnalyticsCountryId>>();
            foreach (AnalyticsData data in usedData == null ? GetIterator() : usedData)
            {
                if (!countries.ContainsKey(data.geolocation.countryCode))
                {
                    countries.Add(data.geolocation.countryCode, new AnalyticsAggregationQueryResult<AnalyticsCountryId>());
                    countries[data.geolocation.countryCode]._id.countryCode = data.geolocation.countryCode;
                }
                countries[data.geolocation.countryCode].totalClicks++;
                countries[data.geolocation.countryCode].totalDuration += data.duration;
                if (countries[data.geolocation.countryCode].maxDuration < data.duration) countries[data.geolocation.countryCode].maxDuration = data.duration;
                if (countries[data.geolocation.countryCode].minDuration > data.duration) countries[data.geolocation.countryCode].minDuration = data.duration;
                if (!countries[data.geolocation.countryCode].ips.Contains(data.remote))
                {
                    countries[data.geolocation.countryCode].totalUniqueIPs++;
                    countries[data.geolocation.countryCode].ips.Add(data.remote);
                }
            }
            List<AnalyticsAggregationQueryResult<AnalyticsCountryId>> countriesL = countries.Values.OrderBy(x => x.totalClicks).ToList();
            countries.Clear();
            countriesL.ForEach(new Action<AnalyticsAggregationQueryResult<AnalyticsCountryId>>(e =>
            {
                e.avgDuration = e.totalDuration / (double)e.totalClicks;
            }));
            return countriesL;
        }

        public string GetTimeString(AnalyticsData data)
        {
            if(timeunit == TimeUnit.date) return data.openTime.ToString("dd.MM.yyyy");
            if (timeunit == TimeUnit.hour) return data.openTime.ToString("HH");
            if (timeunit == TimeUnit.minute) return data.openTime.ToString("mm");
            return "";
        }

        public string GetTimeString(long unix)
        {
            DateTime openTime = TimeConverter.UnixTimeStampToDateTime(unix);
            if (timeunit == TimeUnit.date) return openTime.ToString("d.M.yyyy");
            if (timeunit == TimeUnit.hour) return openTime.ToString("HH");
            if (timeunit == TimeUnit.minute) return openTime.ToString("mm");
            return "";
        }

        public List<AnalyticsAggregationQueryResult<AnalyticsTimeId>> GetAllEndpointsSortedByTimeWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            PreCalculate(queryString);
            if(parentCollection.config.useMongoDB)
            {
                BsonDocument time = new BsonDocument();
                if(timeunit == TimeUnit.hour) time = new BsonDocument("$toString", new BsonDocument("$hour", new BsonDocument("$toDate", "$closeTime")));
                else if(timeunit == TimeUnit.minute) time = new BsonDocument("$toString", new BsonDocument("$minute", new BsonDocument("$toDate", "$closeTime")));
                else if (timeunit == TimeUnit.date) time = new BsonDocument("$concat", new BsonArray
                {
                    new BsonDocument("$toString",
                    new BsonDocument("$dayOfMonth",
                    new BsonDocument("$toDate", "$closeTime"))),
                    ".",
                    new BsonDocument("$toString",
                    new BsonDocument("$month",
                    new BsonDocument("$toDate", "$closeTime"))),
                    ".",
                    new BsonDocument("$toString",
                    new BsonDocument("$year",
                    new BsonDocument("$toDate", "$closeTime")))
                });
                List<AnalyticsAggregationQueryResult<AnalyticsTimeId>> timess = documents.Aggregate<AnalyticsAggregationQueryResult<AnalyticsTimeId>>(GetGroupQuery(new BsonDocument
                                {
                                    { "remote", "$remote" },
                                    { "time", time }
                                },
                                new BsonDocument
                                {
                                    { "time", "$_id.time" },
                                    { "unix", "$_id.unix" }
                                })).ToList();
                timess.ForEach(e =>
                {
                    if (timeunit == TimeUnit.date) e._id.unix = ((DateTimeOffset)e.closeTime.Date).ToUnixTimeSeconds();
                    else if (timeunit == TimeUnit.hour) e._id.unix = e.closeTime.Hour;
                    else if (timeunit == TimeUnit.minute) e._id.unix = e.closeTime.Minute;
                });
                return timess.OrderBy(x => x._id.unix).ToList();
            }
            Dictionary<string, AnalyticsAggregationQueryResult<AnalyticsTimeId>> times = new Dictionary<string, AnalyticsAggregationQueryResult<AnalyticsTimeId>>();
            foreach (AnalyticsData data in usedData == null ? GetIterator() : usedData)
            {
                string date = GetTimeString(data);
                if(!times.ContainsKey(date))
                {
                    times.Add(date, new AnalyticsAggregationQueryResult<AnalyticsTimeId>());
                    times[date]._id.time = date;
                    
                }
                times[date].totalClicks++;
                times[date].totalDuration += data.duration;
                if (times[date].maxDuration < data.duration) times[date].maxDuration = data.duration;
                if (times[date].minDuration > data.duration) times[date].minDuration = data.duration;
                if (!times[date].ips.Contains(data.remote))
                {
                    times[date].totalUniqueIPs++;
                    times[date].ips.Add(data.remote);
                }
            }
            List<AnalyticsAggregationQueryResult<AnalyticsTimeId>> datesL = times.Values.OrderBy(x => x._id.unix).ToList();
            times.Clear();
            datesL.ForEach(new Action<AnalyticsAggregationQueryResult<AnalyticsTimeId>>(d => {
                d.avgDuration = d.totalDuration / (double)d.totalClicks;
            }));
            return datesL;
        }

        public List<AnalyticsAggregationQueryResult<AnalyticsScreenId>> GetAllScreensWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            PreCalculate(queryString);
            if(parentCollection.config.useMongoDB)
            {
                return documents.Aggregate<AnalyticsAggregationQueryResult<AnalyticsScreenId>>(GetGroupQuery(new BsonDocument
                                {
                                    { "remote", "$remote" },
                                    { "screenWidth", "$screenWidth" },
                                    { "screenHeight", "$screenHeight" }
                                },
                                new BsonDocument
                                {
                                    { "screenWidth", "$_id.screenWidth" },
                                    { "screenHeight", "$_id.screenHeight" }
                                })).ToList();
            }
            Dictionary<string, AnalyticsAggregationQueryResult<AnalyticsScreenId>> screens = new Dictionary<string, AnalyticsAggregationQueryResult<AnalyticsScreenId>>();
            foreach (AnalyticsData data in usedData == null ? GetIterator() : usedData)
            {
                string screen = data.screenWidth + "," + data.screenHeight;
                if (!screens.ContainsKey(screen))
                {
                    screens.Add(screen, new AnalyticsAggregationQueryResult<AnalyticsScreenId>());
                    screens[screen]._id.screenWidth = data.screenWidth;
                    screens[screen]._id.screenHeight = data.screenHeight;
                }
                screens[screen].totalClicks++;
                screens[screen].totalDuration += data.duration;
                if (screens[screen].maxDuration < data.duration) screens[screen].maxDuration = data.duration;
                if (screens[screen].minDuration > data.duration) screens[screen].minDuration = data.duration;
                if (!screens[screen].ips.Contains(data.remote))
                {
                    screens[screen].totalUniqueIPs++;
                    screens[screen].ips.Add(data.remote);
                }
            }
            screens = Sorter.Sort(screens);
            List<AnalyticsAggregationQueryResult<AnalyticsScreenId>> screensL = screens.Values.ToList();
            screens.Clear();
            // Remove screens with fewer than 2 users
            for(int i = 0; i < screensL.Count; i++)
            {
                if (screensL[i].totalUniqueIPs <= 1)
                {
                    screensL.RemoveAt(i);
                    i--;
                    continue;
                }
                screensL[i].avgDuration = screensL[i].totalDuration / (double)screensL[i].totalClicks;
            }
            screensL = screensL.OrderBy(s => s.totalClicks).ToList();
            screensL.Reverse();
            return screensL;
        }

        public List<AnalyticsAggregationQueryResult<AnalyticsReferrerId>> GetAllReferrersWithAssociatedData(List<AnalyticsData> usedData = null, NameValueCollection queryString = null)
        {
            PreCalculate(queryString);
            if(parentCollection.config.useMongoDB)
            {
                return documents.Aggregate<AnalyticsAggregationQueryResult<AnalyticsReferrerId>>(GetGroupQuery(new BsonDocument
                                {
                                    { "remote", "$remote" },
                                    { "uri", "$referrer" }
                                },
                                new BsonDocument
                                {
                                    { "uri", "$_id.uri" }
                                })).ToList().FindAll(x => !x._id.uri.Contains(collectionName));
            }
            Dictionary<string, AnalyticsAggregationQueryResult<AnalyticsReferrerId>> referrers = new Dictionary<string, AnalyticsAggregationQueryResult<AnalyticsReferrerId>>();
            foreach (AnalyticsData data in usedData == null ? GetIterator() : usedData)
            {
                if (!referrers.ContainsKey(data.referrer))
                {
                    referrers.Add(data.referrer, new AnalyticsAggregationQueryResult<AnalyticsReferrerId>());
                    referrers[data.referrer]._id.uri = data.referrer;
                }
                referrers[data.referrer].totalClicks++;
                referrers[data.referrer].totalDuration += data.duration;
                if (referrers[data.referrer].maxDuration < data.duration) referrers[data.referrer].maxDuration = data.duration;
                if (referrers[data.referrer].minDuration > data.duration) referrers[data.referrer].minDuration = data.duration;
                if (!referrers[data.referrer].ips.Contains(data.remote))
                {
                    referrers[data.referrer].totalUniqueIPs++;
                    referrers[data.referrer].ips.Add(data.remote);
                }
            }
            List<AnalyticsAggregationQueryResult<AnalyticsReferrerId>> referrersL = referrers.Values.OrderBy(x => x.totalClicks).ToList();
            referrers.Clear();
            referrersL.ForEach(new Action<AnalyticsAggregationQueryResult<AnalyticsReferrerId>>(e => e.avgDuration = e.totalDuration / (double)e.totalClicks));
            return referrersL;
        }

        // cache query string parameters for smol performance boost
        public string host = null;
        public string endpoint = null;
        public string referrer = null;
        public string screenwidth = null;
        public string screenheight = null;
        public TimeUnit timeunit = TimeUnit.date;
        public string time = null;
        public bool deep = false;
        public string countryCode = null;

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
            time = c.Get("time") == null ? null : c.Get("time");
            deep = c.Get("deep") != null;
            countryCode = c.Get("countrycode") == null ? null : c.Get("countrycode").ToLower();

            hours = c.Get("hours") != null && Regex.IsMatch(c.Get("hours"), "[0-9]+") ? Convert.ToInt32(c.Get("hours")) : 0;
            minutes = c.Get("minutes") != null && Regex.IsMatch(c.Get("minutes"), "[0-9]+") ? Convert.ToInt32(c.Get("minutes")) : 0;
            seconds = c.Get("seconds") != null && Regex.IsMatch(c.Get("seconds"), "[0-9]+") ? Convert.ToInt32(c.Get("seconds")) : 0;
            days = c.Get("days") != null && Regex.IsMatch(c.Get("days"), "[0-9]+") ? Convert.ToInt32(c.Get("days")) : 0;

            if (days == 0 && hours == 0 && minutes == 0 && seconds == 0) days = 7;
        }

        public bool IsNotValid(AnalyticsData d)
        {
            if (endpoint != null && d.endpoint != endpoint) return true;
            if (countryCode != null && d.geolocation.countryCode != countryCode) return true;
            if (referrer != null && d.referrer != referrer) return true;
            if (screenwidth != null && d.screenWidth.ToString() != screenwidth) return true;
            if (screenheight != null && d.screenHeight.ToString() != screenheight) return true;
            if (time != null && !time.Contains(GetTimeString(d))) return true;

            return IsTimeSpanNotValid(now - d.closeTime);
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
}
