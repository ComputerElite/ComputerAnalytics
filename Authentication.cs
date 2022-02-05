using ComputerUtils.RandomExtensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
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
        public bool geoLocationEnabled { get; set; } = true;
        public string mongoDBName { get; set; } = "ComputerAnalytics";

        public void Fix()
        {
            for(int i = 0; i < Websites.Count; i++)
            {
                if(Websites[i].privateTokens.Count == 0)
                {
                    Websites[i].privateTokens.Add(new Token(RandomExtension.CreateToken(), DateTime.MaxValue));
                }
            }
        }
    }

    public class Token
    {
        public string value { get; set; } = "";
        public DateTime expires { get; set; } = DateTime.Now;

        public Token(string value, DateTime expires)
        {
            this.value = value;
            this.expires = expires;
        }
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
        public List<Token> privateTokens { get; set; } =  new List<Token>();
        public string discordWebhookUrl { get; set; } = "";
        public int siteClicks { get;set; } = 0;
        public DateTime lastWebhookUpdate { get; set; } = DateTime.Now;
        public int index = 0;

        public bool HasPrivateToken(string privateToken)
        {
            DateTime now = DateTime.Now;
            if(privateTokens.Count == 0)
            {
                privateTokens.Add(new Token(RandomExtension.CreateToken(), DateTime.MaxValue));
            }
            for (int i = 0; i < privateTokens.Count; i++)
            {
                if(privateTokens[i].expires <= now)
                {
                    privateTokens.RemoveAt(i);
                    i--;
                    continue;
                }
                if(privateTokens[i].value == privateToken) return true;
            }
            return false;
        }
    }
}
