using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Database.Models
{
    public class Network
    {
        public int Id { get; set; }
        public string Name { get; set; }

        public string Url { get; set; }
        public string CommunityUrl { get; set; }
        public int SortOrder { get; set; }
        public bool SaveAnalytics { get; set; }
        public string ShortName { get; set; }
        public bool DisablePongs { get; set; }
        public bool DisableWelcomeMessage { get; set; }
    }
}
