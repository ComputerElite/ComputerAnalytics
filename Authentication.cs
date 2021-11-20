using System;
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
    }

    public class Website
    {
        public string url { get; set; } = "";
        public string folder { get; set; } = "";
        public string publicToken { get; set; } = "";
        public string privateToken { get; set; } = "";
        public int index = 0;
    }
}
