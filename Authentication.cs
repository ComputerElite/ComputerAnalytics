﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputerAnalytics
{
    public class Config
    {
        public List<Website> Websites { get; set; } = new List<Website>();
        public string masterToken { get; set; } = "";
        public List<string> usedTokens { get; set; } = new List<string>();
        public string publicAddress { get; set; } = "";
        public int port { get; set; } = 502;
        public string masterDiscordWebhookUrl { get; set; } = "";
        public int recievedAnalytics { get; set; } = 0;
        public int rejectedAnalytics { get; set; } = 0;
        public DateTime lastWebhookUpdate { get; set; } = DateTime.Now;
        public string mongoDBUrl { get; set; } = "";
        public bool useMongoDB { get; set; } = false;
        public bool migrateOldDataToMongoDB { get; set; } = false;
        public string mongoDBName { get; set; } = "ComputerAnalytics";
    }

    public class Metrics
    {
        public long ramUsage { get; set; } = 0;
        public string ramUsageString { get; set; } = "";
        public string workingDirectory { get; set; } = "";
    }

    public class Website
    {
        public string url { get; set; } = "";
        public string folder { get; set; } = "";
        public string publicToken { get; set; } = "";
        public string privateToken { get; set; } = "";
        public string discordWebhookUrl { get; set; } = "";
        public int siteClicks { get;set; } = 0;
        public DateTime lastWebhookUpdate { get; set; } = DateTime.Now;
        public int index = 0;
    }
}
